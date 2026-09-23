using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pulse.Host;

/// <summary>Immutable local implementation deltas. No model-supplied filesystem path is read or executed.</summary>
public static class CandidateSnapshots
{
    public const int MaximumPaths = 4096;
    public const long MaximumFileBytes = 64L * 1024 * 1024;
    public const long MaximumTotalBytes = 256L * 1024 * 1024;
    private const int MaximumManifestBytes = 2 * 1024 * 1024;
    private const long MaximumArchiveBytes = MaximumTotalBytes + 8 * 1024 * 1024;
    private const string ReferenceName = "candidate-snapshot.json";
    private const string InputName = "candidate-input.json";

    public static async Task<JsonObject> CaptureAsync(Store store, string runId, string repoFolder, CancellationToken cancellationToken = default)
    {
        runId = Protocol.RunId(runId);
        using var held = store.AcquireLock("candidate-run-" + runId);
        var runDirectory = store.RunDirectory(runId);
        EnsureDirectory(runDirectory);
        var referencePath = Path.Combine(runDirectory, ReferenceName);
        if (Exists(referencePath))
        {
            var retained = ReadReference(store, referencePath, runId);
            if (Text(retained, "status") == "ready")
            {
                using var snapshot = Load(store, RequiredHash(retained, "snapshotHash"));
                if (Text(retained, "baseSha") != Text(snapshot.Manifest, "baseSha")) throw Invalid("The retained candidate reference has a different base revision.");
            }
            return retained;
        }
        var saved = store.ReadTask(runId);
        if (RuntimeService.ExpectedResultSchemaVersion(saved) != 3 || Text(saved["task"] as JsonObject, "actionKind") is not ("issue-fix" or "feature-implement") ||
            Text(store.ReadStatus(runId), "state") != "running")
            throw Unavailable("Only a new running implementation task can create a candidate snapshot. Historical task records are not changed.");
        var capturedAt = Protocol.Now();
        string? temporaryArchive = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = MatchingFolder(saved["config"]!.AsObject(), repoFolder);
            EnsureDirectory(folder);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromMinutes(2));
            var head = await HeadAsync(folder, deadline.Token);
            var paths = await ChangedPathsAsync(folder, deadline.Token);
            await RequireRegularGitEntries(folder, paths.All, deadline.Token);
            var storage = Storage(store, create: true);
            temporaryArchive = Path.Combine(storage, ".capture-" + Guid.NewGuid().ToString("N") + ".zip");
            var files = new JsonArray(); var deleted = new JsonArray(); long total = 0;
            await using (var output = new FileStream(temporaryArchive, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
                {
                    foreach (var relative in paths.All)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        if (paths.Deleted.Contains(relative) && !paths.Untracked.Contains(relative))
                        {
                            if (!DeletedPathAbsent(folder, relative)) throw Invalid("A deleted tracked file was replaced by data outside the captured Git delta.");
                            deleted.Add(relative); continue;
                        }
                        var path = ConfinedPath(folder, relative);
                        var attributes = CheckPath(folder, relative);
                        if (attributes is null)
                        {
                            if (paths.Untracked.Contains(relative)) throw Invalid("An untracked candidate file changed during snapshot capture.");
                            deleted.Add(relative); continue;
                        }
                        RequireRegular(attributes.Value);
                        if (new FileInfo(path).Length > MaximumFileBytes) throw Unavailable("A candidate file exceeds the 64 MiB snapshot limit.");
                        if (new FileInfo(path).Length + total > MaximumTotalBytes) throw Unavailable("The candidate delta exceeds the 256 MiB snapshot limit.");
                        var entry = zip.CreateEntry(relative, CompressionLevel.NoCompression);
                        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                        entry.ExternalAttributes = (0x8000 | 0x1a4) << 16;
                        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        await using var destination = entry.Open();
                        var copied = await CopyAndHash(source, destination, Math.Min(MaximumFileBytes, MaximumTotalBytes - total), deadline.Token);
                        total = checked(total + copied.Length);
                        if (total > MaximumTotalBytes) throw Unavailable("The candidate delta exceeds the 256 MiB snapshot limit.");
                        files.Add(new JsonObject { ["path"] = relative, ["length"] = copied.Length, ["sha256"] = copied.Hash });
                    }
                }
                await output.FlushAsync(deadline.Token); output.Flush(flushToDisk: true);
            }
            // Detect source edits during collection. This is evidence of one complete delta,
            // never a best-effort mixture of files observed at different repository revisions.
            if (await HeadAsync(folder, deadline.Token) != head || !(await ChangedPathsAsync(folder, deadline.Token)).All.SequenceEqual(paths.All))
                throw Invalid("The repository revision or changed-file set moved during candidate capture.");
            foreach (var row in files.OfType<JsonObject>())
            {
                var relative = row["path"]!.GetValue<string>(); RequireRegular(CheckPath(folder, relative) ?? throw Invalid("A candidate file disappeared during capture."));
                await using var source = new FileStream(ConfinedPath(folder, relative), FileMode.Open, FileAccess.Read, FileShare.Read);
                var current = await CopyAndHash(source, Stream.Null, MaximumFileBytes, deadline.Token);
                if (current.Length != row["length"]!.GetValue<long>() || current.Hash != Text(row, "sha256")) throw Invalid("A candidate file changed during capture.");
            }
            foreach (var row in deleted)
                if (!DeletedPathAbsent(folder, row!.GetValue<string>())) throw Invalid("A deleted candidate path reappeared during capture.");
            var manifest = new JsonObject
            {
                ["version"] = 1, ["status"] = "ready", ["kind"] = "local-candidate", ["baseSha"] = head,
                ["archiveSha256"] = HashFile(temporaryArchive, deadline.Token), ["files"] = files, ["deletedPaths"] = deleted, ["totalBytes"] = total
            };
            var snapshotHash = Protocol.Fingerprint(manifest); manifest["snapshotHash"] = snapshotHash;
            if (Encoding.UTF8.GetByteCount(manifest.ToJsonString(Protocol.JsonOptions)) > MaximumManifestBytes)
                throw Unavailable("The candidate path manifest exceeds the 2 MiB snapshot limit.");
            using (store.AcquireLock("candidate-artifact-" + snapshotHash))
            {
                var archivePath = Path.Combine(storage, snapshotHash + ".zip");
                var manifestPath = Path.Combine(storage, snapshotHash + ".json");
                if (Exists(manifestPath))
                {
                    using var existing = Load(store, snapshotHash, deadline.Token);
                    if (!JsonNode.DeepEquals(existing.Manifest, manifest)) throw Invalid("An existing candidate artifact does not match its content identity.");
                }
                else
                {
                    if (Exists(archivePath))
                    {
                        EnsureRegularPath(archivePath);
                        if (HashFile(archivePath, deadline.Token) != Text(manifest, "archiveSha256")) throw Invalid("An incomplete candidate artifact has unexpected contents.");
                    }
                    else File.Move(temporaryArchive, archivePath, overwrite: false);
                    store.WriteJson(manifestPath, manifest);
                }
                using (Load(store, snapshotHash, deadline.Token)) { }
            }
            var reference = Reference(runId, capturedAt, head, snapshotHash);
            store.WriteJson(referencePath, reference);
            return reference;
        }
        catch (Exception error) when (Recoverable(error))
        {
            var reference = new JsonObject
            {
                ["version"] = 1, ["status"] = "unavailable", ["kind"] = "local-candidate", ["runId"] = runId, ["capturedAt"] = capturedAt,
                ["diagnostic"] = new JsonObject { ["code"] = "CANDIDATE_SNAPSHOT_UNAVAILABLE", ["message"] = Limit(OutputRedactor.Redact(error.Message), 2048) }
            };
            store.WriteJson(referencePath, reference);
            return reference;
        }
        finally
        {
            // The only temporary file here is the exact uniquely created path under the
            // validated artifact directory. Published artifacts are never automatically removed.
            if (temporaryArchive is not null && Exists(temporaryArchive)) File.Delete(temporaryArchive);
        }
    }

    public static JsonObject? Describe(Store store, string parentRunId)
    {
        parentRunId = Protocol.RunId(parentRunId);
        var runDirectory = store.RunDirectory(parentRunId);
        if (!Directory.Exists(runDirectory)) return null;
        EnsureDirectory(runDirectory);
        var path = Path.Combine(runDirectory, ReferenceName);
        if (!Exists(path))
        {
            var saved = store.ReadTask(parentRunId);
            if (Text(saved["task"] as JsonObject, "actionKind") is "issue-fix" or "feature-implement") return null;
            path = Path.Combine(runDirectory, InputName);
            if (!Exists(path)) return null;
        }
        var reference = ReadReference(store, path, parentRunId);
        if (Text(reference, "status") == "unavailable") return null;
        var hash = RequiredHash(reference, "snapshotHash");
        using var snapshot = Load(store, hash);
        if (Text(reference, "baseSha") != Text(snapshot.Manifest, "baseSha")) throw Invalid("The candidate reference base differs from its immutable artifact.");
        return new JsonObject { ["baseSha"] = snapshot.Manifest["baseSha"]!.DeepClone(), ["snapshotHash"] = hash, ["kind"] = "local-candidate" };
    }

    public static async Task ApplyAsync(Store store, string childRunId, JsonObject config, CancellationToken cancellationToken = default)
    {
        childRunId = Protocol.RunId(childRunId);
        var saved = store.ReadTask(childRunId); var task = saved["task"]!.AsObject();
        if (task["planSource"] is not JsonObject source) return;
        if (Text(source, "sourceKind") != "local-candidate")
        {
            if (source.ContainsKey("candidateSnapshotHash")) throw Invalid("The accepted candidate source kind is missing or invalid.");
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (Text(task, "actionKind") is not ("feature-implement" or "issue-fix" or "reproduction-setup" or "issue-verify"))
            throw Invalid("This task type cannot restore a local implementation candidate.");
        Protocol.RunId(Protocol.RequiredString(source, "parentRunId", 36));
        var hash = RequiredHash(source, "candidateSnapshotHash");
        var expectedHead = RequiredSha(source, "revisionSha");
        var folder = MatchingFolder(saved["config"]!.AsObject(), Protocol.RequiredString(config, "repoFolder", 4096));
        if (Text(config, "worktreeBase") != expectedHead || Text(saved["config"] as JsonObject, "worktreeBase") != expectedHead)
            throw BaseMismatch();
        EnsureDirectory(folder);
        using var held = store.AcquireLock("candidate-apply-" + childRunId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromMinutes(2));
        using var snapshot = Load(store, hash, deadline.Token); // Verifies every byte before touching the child.
        if (Text(snapshot.Manifest, "baseSha") != expectedHead) throw BaseMismatch();
        if (await HeadAsync(folder, deadline.Token) != expectedHead) throw BaseMismatch();
        var receiptPath = Path.Combine(store.RunDirectory(childRunId), InputName);
        EnsureDirectory(store.RunDirectory(childRunId));
        if (Exists(receiptPath))
        {
            var receipt = ReadReference(store, receiptPath, childRunId);
            if (Text(receipt, "snapshotHash") != hash || Text(receipt, "baseSha") != expectedHead || !await MatchesApplied(folder, snapshot.Manifest, deadline.Token))
                throw Invalid("The candidate was already restored and its child workspace has changed. Start a separate verification task.");
            return;
        }
        if ((await ReadGit(folder, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], deadline.Token)).Length != 0)
            throw Invalid("The new verification worktree is not clean. Candidate restoration will not overwrite existing changes.");
        var files = snapshot.Manifest["files"]!.AsArray().OfType<JsonObject>().ToArray();
        var deleted = snapshot.Manifest["deletedPaths"]!.AsArray().Select(row => row!.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in deleted)
        {
            var attributes = CheckPath(folder, path);
            if (attributes is not null) RequireRegular(attributes.Value);
        }
        foreach (var file in files) CheckDestination(folder, file["path"]!.GetValue<string>(), deleted);
        var stage = Path.Combine(store.RunDirectory(childRunId), ".candidate-apply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            for (var index = 0; index < files.Length; index++)
            {
                var entry = snapshot.Entries[files[index]["path"]!.GetValue<string>()];
                await using var input = entry.Open();
                await using var output = new FileStream(Path.Combine(stage, index.ToString(System.Globalization.CultureInfo.InvariantCulture)), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var copied = await CopyAndHash(input, output, MaximumFileBytes, deadline.Token);
                if (copied.Length != files[index]["length"]!.GetValue<long>() || copied.Hash != Text(files[index], "sha256")) throw Invalid("Candidate archive bytes changed during extraction.");
            }
            // No malformed archive, unsafe path, or base mismatch can reach a mutation.
            foreach (var relative in deleted.OrderByDescending(path => path.Length))
            {
                var attributes = CheckPath(folder, relative);
                if (attributes is null) continue;
                RequireRegular(attributes.Value); File.Delete(ConfinedPath(folder, relative));
            }
            for (var index = 0; index < files.Length; index++)
            {
                var relative = files[index]["path"]!.GetValue<string>();
                var destination = ConfinedPath(folder, relative);
                CheckDestination(folder, relative, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                if (Directory.Exists(destination)) RemoveEmptyTree(folder, relative);
                EnsureParents(folder, relative);
                CheckPath(folder, relative);
                // Move replaces a directory entry; it never writes through an existing hard link.
                File.Move(Path.Combine(stage, index.ToString(System.Globalization.CultureInfo.InvariantCulture)), destination, overwrite: true);
            }
            if (await HeadAsync(folder, deadline.Token) != expectedHead || !await MatchesApplied(folder, snapshot.Manifest, deadline.Token))
                throw Invalid("The restored candidate could not be verified against its complete immutable delta.");
            store.WriteJson(receiptPath, Reference(childRunId, Protocol.Now(), expectedHead, hash));
        }
        finally
        {
            if (Directory.Exists(stage))
            {
                EnsureDirectory(stage);
                foreach (var path in Directory.EnumerateFiles(stage)) { EnsureRegularPath(path); File.Delete(path); }
                Directory.Delete(stage, recursive: false);
            }
        }
    }

    private static async Task<bool> MatchesApplied(string folder, JsonObject manifest, CancellationToken token)
    {
        foreach (var file in manifest["files"]!.AsArray().OfType<JsonObject>())
        {
            var relative = file["path"]!.GetValue<string>();
            var attributes = CheckPath(folder, relative);
            if (attributes is null || (attributes.Value & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return false;
            await using var stream = new FileStream(ConfinedPath(folder, relative), FileMode.Open, FileAccess.Read, FileShare.Read);
            var current = await CopyAndHash(stream, Stream.Null, MaximumFileBytes, token);
            if (current.Hash != Text(file, "sha256") || current.Length != file["length"]!.GetValue<long>()) return false;
        }
        foreach (var path in manifest["deletedPaths"]!.AsArray())
        {
            var relative = path!.GetValue<string>();
            if (!DeletedPathAbsent(folder, relative)) return false;
        }
        var expected = manifest["files"]!.AsArray().OfType<JsonObject>().Select(file => file["path"]!.GetValue<string>())
            .Concat(manifest["deletedPaths"]!.AsArray().Select(path => path!.GetValue<string>())).ToHashSet(StringComparer.Ordinal);
        return (await ChangedPathsAsync(folder, token)).All.All(expected.Contains);
    }

    private sealed class Loaded(JsonObject manifest, FileStream stream, ZipArchive zip, Dictionary<string, ZipArchiveEntry> entries) : IDisposable
    {
        public JsonObject Manifest { get; } = manifest;
        public Dictionary<string, ZipArchiveEntry> Entries { get; } = entries;
        public void Dispose() { zip.Dispose(); stream.Dispose(); }
    }

    private static Loaded Load(Store store, string hash, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Hash(hash)) throw Invalid("The candidate identity is invalid.");
        FileStream? stream = null; ZipArchive? zip = null;
        try
        {
            var storage = Storage(store, create: false);
            var manifestPath = Path.Combine(storage, hash + ".json"); var archivePath = Path.Combine(storage, hash + ".zip");
            if (!Exists(manifestPath) || !Exists(archivePath)) throw Unavailable("The retained candidate artifact is unavailable. It cannot be replaced by the original source revision.");
            EnsureRegularPath(manifestPath); EnsureRegularPath(archivePath);
            if (new FileInfo(manifestPath).Length > MaximumManifestBytes || new FileInfo(archivePath).Length > MaximumArchiveBytes) throw Invalid("Candidate artifact limits were exceeded.");
            var manifest = store.ReadJson(manifestPath) ?? throw Invalid("The candidate manifest is missing.");
            var canonical = manifest.DeepClone().AsObject(); canonical.Remove("snapshotHash");
            Protocol.OnlyKeys(manifest, "version", "status", "kind", "baseSha", "archiveSha256", "files", "deletedPaths", "totalBytes", "snapshotHash");
            if (manifest["version"]?.GetValue<int>() != 1 || Text(manifest, "status") != "ready" || Text(manifest, "kind") != "local-candidate" ||
                Text(manifest, "snapshotHash") != hash || Protocol.Fingerprint(canonical) != hash || !Sha(Text(manifest, "baseSha")) || !Hash(Text(manifest, "archiveSha256")) ||
                manifest["files"] is not JsonArray files || manifest["deletedPaths"] is not JsonArray deleted || files.Count + deleted.Count > MaximumPaths)
                throw Invalid("The candidate manifest does not match its immutable content identity.");
            var rows = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
            foreach (var item in files)
            {
                if (item is not JsonObject row) throw Invalid("A candidate file entry is invalid.");
                Protocol.OnlyKeys(row, "path", "length", "sha256");
                var path = Protocol.RequiredString(row, "path", 4096); ValidateRelative(path);
                if (!allPaths.Add(path) || !Hash(Text(row, "sha256")) || row["length"] is not JsonValue length || !length.TryGetValue<long>(out var bytes) || bytes < 0 || bytes > MaximumFileBytes)
                    throw Invalid("Candidate file paths or byte bounds are invalid.");
                total = checked(total + bytes); rows.Add(path, row);
            }
            foreach (var item in deleted)
            {
                var path = item?.GetValue<string>() ?? throw Invalid("A deleted candidate path is invalid."); ValidateRelative(path);
                if (!allPaths.Add(path)) throw Invalid("Candidate file and deletion paths conflict.");
            }
            if (total > MaximumTotalBytes || manifest["totalBytes"]?.GetValue<long>() != total) throw Invalid("The candidate total size is invalid.");
            stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (CopyAndHash(stream, Stream.Null, MaximumArchiveBytes, cancellationToken).GetAwaiter().GetResult().Hash != Text(manifest, "archiveSha256")) throw Invalid("The candidate archive checksum does not match.");
            stream.Position = 0; zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true, Encoding.UTF8);
            if (zip.Entries.Count != rows.Count) throw Invalid("The candidate archive contains unexpected or missing entries.");
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            foreach (var entry in zip.Entries)
            {
                ValidateRelative(entry.FullName);
                var mode = (entry.ExternalAttributes >> 16) & 0xf000;
                if (mode is not (0 or 0x8000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || !rows.TryGetValue(entry.FullName, out var row) || !entries.TryAdd(entry.FullName, entry) || entry.Length != row["length"]!.GetValue<long>())
                    throw Invalid("The candidate archive contains a link, directory, duplicate, or mismatched file entry.");
                using var content = entry.Open();
                var digest = CopyAndHash(content, Stream.Null, MaximumFileBytes, cancellationToken).GetAwaiter().GetResult();
                if (digest.Length != entry.Length || digest.Hash != Text(row, "sha256")) throw Invalid("A candidate archive file checksum does not match.");
            }
            return new Loaded(manifest, stream, zip, entries);
        }
        catch (Exception error) when (Recoverable(error))
        {
            zip?.Dispose(); stream?.Dispose();
            if (error is OperationCanceledException) throw;
            if (error is ProtocolException { Code: "CANDIDATE_SNAPSHOT_UNAVAILABLE" }) throw;
            throw Invalid("The retained candidate snapshot is invalid: " + Limit(OutputRedactor.Redact(error.Message), 1024));
        }
    }

    private static JsonObject Reference(string runId, string capturedAt, string head, string hash) => new()
    { ["version"] = 1, ["status"] = "ready", ["kind"] = "local-candidate", ["runId"] = runId, ["capturedAt"] = capturedAt, ["baseSha"] = head, ["snapshotHash"] = hash };
    private static JsonObject ReadReference(Store store, string path, string runId)
    {
        try
        {
            EnsureRegularPath(path);
            if (new FileInfo(path).Length > 8192) throw Invalid("The candidate reference is oversized.");
            var reference = store.ReadJson(path) ?? throw Invalid("The candidate reference is missing.");
            if (reference["version"]?.GetValue<int>() != 1 || Text(reference, "runId") != runId || Text(reference, "kind") != "local-candidate" ||
                Text(reference, "status") is not ("ready" or "unavailable")) throw Invalid("The candidate reference is invalid.");
            if (Text(reference, "status") == "ready") { RequiredSha(reference, "baseSha"); RequiredHash(reference, "snapshotHash"); }
            return reference;
        }
        catch (Exception error) when (Recoverable(error)) { throw Invalid("The candidate reference cannot be verified: " + Limit(error.Message, 1024)); }
    }
    private static string Storage(Store store, bool create)
    {
        EnsureDirectory(store.Root);
        var path = Path.Combine(store.Root, "candidate-snapshots");
        if (create && !Exists(path)) Directory.CreateDirectory(path);
        if (!Directory.Exists(path)) throw Unavailable("The retained candidate artifact directory is unavailable.");
        EnsureDirectory(path); return path;
    }
    private static string MatchingFolder(JsonObject config, string folder)
    {
        var configured = Protocol.RequiredString(config, "repoFolder", 4096);
        if (!Path.IsPathFullyQualified(configured) || !Path.IsPathFullyQualified(folder)) throw Invalid("Candidate worktree paths must be absolute saved locations.");
        var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
        var supplied = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!string.Equals(expected, supplied, StringComparison.OrdinalIgnoreCase)) throw Invalid("Candidate paths must use the task's saved worktree.");
        return supplied;
    }
    private static async Task<string> HeadAsync(string folder, CancellationToken token)
    {
        var top = (await ReadGit(folder, ["rev-parse", "--show-toplevel"], token)).Trim();
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(top)), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase))
            throw Invalid("The saved candidate directory is not the Git worktree root.");
        var head = (await ReadGit(folder, ["rev-parse", "--verify", "HEAD"], token)).Trim().ToLowerInvariant();
        if (!Sha(head)) throw Unavailable("The candidate repository has no verifiable commit base."); return head;
    }
    private sealed record Paths(string[] All, HashSet<string> Untracked, HashSet<string> Deleted);
    private static async Task<Paths> ChangedPathsAsync(string folder, CancellationToken token)
    {
        var tracked = SplitPaths(await ReadGit(folder, ["diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--name-only", "-z", "HEAD", "--"], token));
        var deleted = SplitPaths(await ReadGit(folder, ["diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--diff-filter=D", "--name-only", "-z", "HEAD", "--"], token)).ToHashSet(StringComparer.Ordinal);
        var untracked = SplitPaths(await ReadGit(folder, ["ls-files", "--others", "--exclude-standard", "-z", "--"], token)).ToHashSet(StringComparer.Ordinal);
        var paths = tracked.Concat(untracked).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (paths.Length > MaximumPaths || paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length) throw Unavailable("The candidate delta exceeds 4096 paths or contains paths that collide on Windows.");
        foreach (var path in paths) ValidateRelative(path);
        return new Paths(paths, untracked, deleted);
    }
    private static string[] SplitPaths(string value) => value.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    private static async Task RequireRegularGitEntries(string folder, string[] paths, CancellationToken token)
    {
        if (paths.Length == 0) return;
        var changed = paths.ToHashSet(StringComparer.Ordinal);
        foreach (var row in SplitPaths(await ReadGit(folder, ["ls-files", "--stage", "-z", "--"], token)))
        {
            var separator = row.IndexOf('\t');
            if (separator < 0) throw Invalid("The candidate Git index could not be inspected.");
            var metadata = row[..separator].Split(' ');
            if (changed.Contains(row[(separator + 1)..]) && (metadata.Length != 3 || metadata[2] != "0" || metadata[0] is not ("100644" or "100755")))
                throw Unavailable("The candidate contains a Git link, submodule, or unresolved index entry that cannot be snapshotted as a regular file.");
        }
    }
    private static async Task<string> ReadGit(string folder, string[] arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var git = RuntimeService.ResolveExecutable("git") ?? throw Unavailable("Git is unavailable for candidate capture.");
        using var process = WindowsProcess.Cli(git, arguments, folder, new Dictionary<string, string>
        { ["GIT_TERMINAL_PROMPT"] = "0", ["GCM_INTERACTIVE"] = "never", ["GIT_OPTIONAL_LOCKS"] = "0" });
        var output = ReadBounded(process.Output!, token); var error = ReadBounded(process.Error!, token);
        process.Input!.Close(); process.Resume();
        try
        {
            await process.WaitAsync(token); process.Kill(); var value = await output; await error;
            if (process.ExitCode != 0) throw Unavailable("Git could not inspect the candidate worktree."); return value;
        }
        finally { process.Kill(); try { await Task.WhenAll(output, error); } catch { } }
    }
    private static async Task<string> ReadBounded(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder(); var buffer = new char[8192]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        { if (text.Length + count > 16 * 1024 * 1024) throw Unavailable("Candidate Git metadata exceeds the bounded capture budget."); text.Append(buffer, 0, count); }
        return text.ToString();
    }
    private static async Task<(long Length, string Hash)> CopyAndHash(Stream input, Stream output, long maximum, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536]; long total = 0; int count;
        while ((count = await input.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            total += count; if (total > maximum) throw Unavailable("A candidate file exceeds its bounded snapshot size.");
            hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        return (total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }
    private static string HashFile(string path, CancellationToken token = default)
    { EnsureRegularPath(path); using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return CopyAndHash(file, Stream.Null, MaximumArchiveBytes, token).GetAwaiter().GetResult().Hash; }
    private static void ValidateRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Contains('\\') || path.StartsWith('/') || Path.IsPathRooted(path)) throw Invalid("A candidate path is not relative to its worktree.");
        foreach (var part in path.Split('/'))
        {
            if (part is "" or "." or ".." || part.Equals(".git", StringComparison.OrdinalIgnoreCase) || part.TrimEnd(' ', '.') != part ||
                part.Any(character => character < 32 || "<>:\"|?*".Contains(character)) || Regex.IsMatch(part.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw Invalid("A candidate path contains traversal, repository metadata, or an unsupported Windows filename.");
        }
    }
    private static string ConfinedPath(string root, string relative)
    {
        ValidateRelative(relative); var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw Invalid("A candidate path escaped its worktree."); return full;
    }
    private static FileAttributes? CheckPath(string root, string relative)
    {
        ValidateRelative(relative); EnsureDirectory(root); var parts = relative.Split('/'); var current = root;
        for (var index = 0; index < parts.Length; index++)
        {
            current = Path.Combine(current, parts[index]); var attributes = Attributes(current);
            if (attributes is null) return null;
            if ((attributes.Value & FileAttributes.ReparsePoint) != 0) throw Invalid("Candidate paths cannot contain symbolic links or reparse points.");
            if (index < parts.Length - 1 && (attributes.Value & FileAttributes.Directory) == 0) throw Invalid("A candidate parent path is not a directory.");
            if (index == parts.Length - 1) return attributes;
        }
        return null;
    }
    private static bool DeletedPathAbsent(string root, string relative)
    {
        ValidateRelative(relative); EnsureDirectory(root); var parts = relative.Split('/'); var current = root;
        for (var index = 0; index < parts.Length; index++)
        {
            current = Path.Combine(current, parts[index]); var attributes = Attributes(current);
            if (attributes is null) return true;
            if ((attributes.Value & FileAttributes.ReparsePoint) != 0) throw Invalid("Deleted candidate paths cannot contain links or reparse points.");
            if (index < parts.Length - 1 && (attributes.Value & FileAttributes.Directory) == 0) return true;
            if (index == parts.Length - 1) return (attributes.Value & FileAttributes.Directory) != 0;
        }
        return true;
    }
    private static void CheckDestination(string root, string relative, HashSet<string> deleted)
    {
        ValidateRelative(relative); EnsureDirectory(root); var parts = relative.Split('/'); var prefix = "";
        for (var index = 0; index < parts.Length; index++)
        {
            prefix = prefix.Length == 0 ? parts[index] : prefix + "/" + parts[index];
            var attributes = Attributes(ConfinedPath(root, prefix)); if (attributes is null) return;
            if ((attributes.Value & FileAttributes.ReparsePoint) != 0) throw Invalid("Candidate destinations cannot contain links or reparse points.");
            if (index < parts.Length - 1 && (attributes.Value & FileAttributes.Directory) == 0)
            { if (deleted.Contains(prefix)) return; throw Invalid("A candidate destination conflicts with an existing file."); }
            if (index == parts.Length - 1 && (attributes.Value & FileAttributes.Directory) != 0) RequireOnlyDeletedDescendants(root, prefix, deleted);
        }
    }
    private static void RequireOnlyDeletedDescendants(string root, string relative, HashSet<string> deleted)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(ConfinedPath(root, relative)))
        {
            var child = relative + "/" + Path.GetFileName(path); var attributes = CheckPath(root, child)!.Value;
            if ((attributes & FileAttributes.Directory) != 0) RequireOnlyDeletedDescendants(root, child, deleted);
            else if (!deleted.Contains(child)) throw Invalid("A candidate file would replace a directory containing unrelated files.");
        }
    }
    private static void RemoveEmptyTree(string root, string relative)
    {
        RequireOnlyDeletedDescendants(root, relative, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        foreach (var directory in Directory.EnumerateDirectories(ConfinedPath(root, relative))) RemoveEmptyTree(root, relative + "/" + Path.GetFileName(directory));
        Directory.Delete(ConfinedPath(root, relative), recursive: false);
    }
    private static void EnsureParents(string root, string relative)
    {
        var parts = relative.Split('/'); var current = root;
        for (var index = 0; index < parts.Length - 1; index++)
        {
            current = Path.Combine(current, parts[index]);
            if (!Exists(current)) Directory.CreateDirectory(current);
            EnsureDirectory(current);
        }
    }
    private static void EnsureDirectory(string path)
    {
        var full = Path.GetFullPath(path); var root = Path.GetPathRoot(full)!; var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part); var attributes = Attributes(current);
            if (attributes is null || (attributes.Value & FileAttributes.Directory) == 0 || (attributes.Value & FileAttributes.ReparsePoint) != 0)
                throw Invalid("Candidate directories must exist without symbolic links or reparse points.");
        }
    }
    private static void EnsureRegularPath(string path) { EnsureDirectory(Path.GetDirectoryName(path)!); RequireRegular(Attributes(path) ?? throw Invalid("A candidate file is missing.")); }
    private static void RequireRegular(FileAttributes attributes)
    { if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) throw Invalid("Only regular candidate files can be retained or restored."); }
    private static FileAttributes? Attributes(string path)
    { try { return File.GetAttributes(path); } catch (FileNotFoundException) { return null; } catch (DirectoryNotFoundException) { return null; } }
    private static bool Exists(string path) => Attributes(path) is not null;
    private static string RequiredSha(JsonObject row, string field) => Sha(Text(row, field)) ? Text(row, field)! : throw Invalid("The candidate base revision is invalid.");
    private static string RequiredHash(JsonObject row, string field) => Hash(Text(row, field)) ? Text(row, field)! : throw Invalid("The candidate checksum is invalid.");
    private static bool Sha(string? value) => value is not null && Regex.IsMatch(value, "^[a-f0-9]{40}$", RegexOptions.CultureInvariant);
    private static bool Hash(string? value) => value is not null && Regex.IsMatch(value, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    private static string? Text(JsonObject? row, string field) => row?[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];
    private static bool Recoverable(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or FormatException or System.ComponentModel.Win32Exception or OperationCanceledException or ProtocolException or OverflowException or System.Text.Json.JsonException;
    private static ProtocolException Invalid(string message) => new("CANDIDATE_SNAPSHOT_INVALID", message, "Preserve the original task and artifacts; start a new implementation task if the retained candidate cannot be verified.");
    private static ProtocolException Unavailable(string message) => new("CANDIDATE_SNAPSHOT_UNAVAILABLE", message, "Keep the implementation result and artifacts. Verification cannot substitute the original source for a missing local candidate.");
    private static ProtocolException BaseMismatch() => new("CANDIDATE_BASE_MISMATCH", "The verification worktree does not match the retained candidate's exact commit base.", "Start a new linked verification task; do not apply the candidate to a different revision.");
}
