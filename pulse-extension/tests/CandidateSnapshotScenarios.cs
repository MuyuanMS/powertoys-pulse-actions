using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Pulse.Host.Tests;

/// <summary>Bounded local Git fixtures exercise candidate preservation and replay without network or model calls.</summary>
internal static class CandidateSnapshotScenarios
{
    private const string DeletedPath = "tracked-deletion-删除.txt";
    private const string UnicodePath = "notes/候補 😀.txt";
    private static readonly byte[] OriginalBinary = [0, 255, 13, 10, 128, 65, 0];

    internal static async Task RunAllAsync()
    {
        using var fixture = await Fixture.CreateAsync();
        await CapturesAndReplaysTheFirstCandidate(fixture);
        await ReplaysFileAndDirectoryTransitions(fixture);
        await DeduplicatesContentAndRetainsCleanBases(fixture);
        await PreparesVerificationFromTheRetainedCandidate(fixture);
        await AcceptedChildSurvivesParentRecordDeletion(fixture);
        await MissingAndIneligibleSourcesStayUnavailable(fixture);
        await OversizedAndLinkedFilesStayUnavailable(fixture);
        await PreCancelledOperationsPreserveTheWorkspace(fixture);
        await CorruptionCannotWriteToTheChild(fixture);
        await MismatchedBasesCannotWriteToTheChild(fixture);
        Console.WriteLine("PASS candidate snapshots: immutable binary/Unicode/deletion/type-change replay, local worktree/clone isolation, bounded capture, cancellation, integrity/link/traversal rejection and base identity (offline Git)");
    }

    private static async Task ReplaysFileAndDirectoryTransitions(Fixture fixture)
    {
        const string nestedOriginal = "tracked-directory/nested/original.txt";
        var folder = await fixture.WorktreeAsync("type-transitions");
        File.Delete(Path.Combine(folder, "tracked.txt"));
        File.Delete(LocalPath(folder, nestedOriginal));
        Directory.Delete(LocalPath(folder, "tracked-directory/nested"));
        Directory.Delete(LocalPath(folder, "tracked-directory"));
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["tracked.txt/child.bin"] = [0, 255, 1, 128, 0],
            ["tracked-directory"] = Encoding.UTF8.GetBytes("A directory became this regular file. 目录\n")
        };
        foreach (var (path, bytes) in files) Write(folder, path, bytes);
        var parentTree = Tree(folder); var runId = fixture.CreateRun(folder);
        var reference = await CandidateSnapshots.CaptureAsync(fixture.Store, runId, folder);
        Check(Text(reference, "status") == "ready", "A delta can replace a tracked file with a directory and a tracked directory with a regular file.");
        var manifest = fixture.Store.ReadJson(fixture.ManifestPath(runId))!;
        Check(manifest["files"]!.AsArray().OfType<JsonObject>().Select(row => Text(row, "path")!).Order(StringComparer.Ordinal).SequenceEqual(files.Keys.Order(StringComparer.Ordinal)) &&
            manifest["deletedPaths"]!.AsArray().Select(row => row!.GetValue<string>()).Order(StringComparer.Ordinal)
                .SequenceEqual(new[] { "tracked.txt", nestedOriginal }.Order(StringComparer.Ordinal)),
            "Type transitions retain exact replacement file paths and removed tracked paths without inventing directory entries.");
        var child = await fixture.CreateChildAsync(new Captured(runId, folder, manifest, files));
        await CandidateSnapshots.ApplyAsync(fixture.Store, child.RunId, child.Config);
        CheckTreesEqual(parentTree, Tree(child.Folder), "Both file/directory transitions replay completely without leaving obsolete nested directories.");
        await CandidateSnapshots.ApplyAsync(fixture.Store, child.RunId, child.Config);
        CheckTreesEqual(parentTree, Tree(child.Folder), "Reopening a completed type-transition replay accepts the retained matching candidate.");
        CheckTreesEqual(parentTree, Tree(folder), "Replaying type transitions leaves the parent candidate untouched.");
    }

    private static async Task CapturesAndReplaysTheFirstCandidate(Fixture fixture)
    {
        var captured = await fixture.CaptureCandidateAsync("feature-implement");
        var manifest = captured.Manifest;
        var reference = fixture.Store.ReadJson(fixture.ReferencePath(captured.RunId))!;
        Check(manifest["version"]?.GetValue<int>() == 1 && Text(manifest, "status") == "ready" && Text(manifest, "kind") == "local-candidate" &&
            Text(reference, "runId") == captured.RunId && Text(manifest, "baseSha") == fixture.BaseSha && Text(reference, "baseSha") == fixture.BaseSha &&
            Text(reference, "snapshotHash") == Text(manifest, "snapshotHash"), "A run reference identifies the immutable candidate and its original complete Git revision.");
        Check(Text(manifest, "archiveSha256") == Hash(File.ReadAllBytes(fixture.ArchivePath(captured.RunId))) &&
            Text(manifest, "snapshotHash") == ManifestHash(manifest), "Snapshot identity must bind both ZIP bytes and the complete manifest metadata.");
        var files = manifest["files"]!.AsArray().OfType<JsonObject>().ToArray();
        Check(files.Select(row => Text(row, "path")!).Order(StringComparer.Ordinal).SequenceEqual(captured.Files.Keys.Order(StringComparer.Ordinal)) &&
            manifest["deletedPaths"]!.AsArray().Select(node => node!.GetValue<string>()).SequenceEqual([DeletedPath]),
            "The delta contains changed tracked and untracked files plus the tracked deletion, excluding ignored files.");
        foreach (var row in files)
        {
            var bytes = captured.Files[Text(row, "path")!];
            Check(row["length"]?.GetValue<long>() == bytes.LongLength && Text(row, "sha256") == Hash(bytes),
                "Every manifest file records its exact byte count and SHA256, including binary and Unicode paths.");
        }
        Check(manifest["totalBytes"]?.GetValue<long>() == captured.Files.Values.Sum(bytes => bytes.LongLength), "Total bytes account for the complete captured delta.");
        using (var archive = ZipFile.OpenRead(fixture.ArchivePath(captured.RunId)))
        {
            Check(archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).SequenceEqual(captured.Files.Keys.Order(StringComparer.Ordinal)),
                "ZIP entries use exact repository-relative paths without directory or unrelated entries.");
            foreach (var entry in archive.Entries)
            {
                using var stream = entry.Open(); using var bytes = new MemoryStream(); stream.CopyTo(bytes);
                Check(bytes.ToArray().SequenceEqual(captured.Files[entry.FullName]), "ZIP contents preserve exact original file bytes.");
            }
        }
        var descriptor = CandidateSnapshots.Describe(fixture.Store, captured.RunId)!;
        Check(Text(descriptor, "baseSha") == fixture.BaseSha && Text(descriptor, "snapshotHash") == Text(manifest, "snapshotHash") &&
            Text(descriptor, "kind") == "local-candidate", "Only a fully verified snapshot yields a replay descriptor.");
        descriptor["baseSha"] = new string('a', 40);
        Check(Text(CandidateSnapshots.Describe(fixture.Store, captured.RunId)!, "baseSha") == fixture.BaseSha, "Descriptors are detached from the immutable stored source.");

        var manifestBytes = File.ReadAllBytes(fixture.ManifestPath(captured.RunId));
        var archiveBytes = File.ReadAllBytes(fixture.ArchivePath(captured.RunId));
        var referenceBytes = File.ReadAllBytes(fixture.ReferencePath(captured.RunId));
        Write(captured.Folder, "tracked.txt", Encoding.UTF8.GetBytes("later parent edit\n"));
        Write(captured.Folder, "binary.bin", [71, 0, 88]);
        File.Delete(LocalPath(captured.Folder, UnicodePath));
        Write(captured.Folder, "later.txt", Encoding.UTF8.GetBytes("created after the first capture\n"));
        var terminal = fixture.Store.ReadStatus(captured.RunId); terminal["state"] = "succeeded";
        fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(captured.RunId), "status.json"), terminal);
        var parentAfterEditing = Tree(captured.Folder);
        var recaptured = await CandidateSnapshots.CaptureAsync(fixture.Store, captured.RunId, captured.Folder);
        Check(JsonNode.DeepEquals(recaptured, reference) && File.ReadAllBytes(fixture.ManifestPath(captured.RunId)).SequenceEqual(manifestBytes) &&
            File.ReadAllBytes(fixture.ArchivePath(captured.RunId)).SequenceEqual(archiveBytes) &&
            File.ReadAllBytes(fixture.ReferencePath(captured.RunId)).SequenceEqual(referenceBytes), "A later capture request retains the first run reference, manifest and archive byte-for-byte.");

        foreach (var clone in new[] { false, true })
        {
            var child = await fixture.CreateChildAsync(captured, clone);
            var childTaskBytes = File.ReadAllBytes(Path.Combine(fixture.Store.RunDirectory(child.RunId), "task.json"));
            await CandidateSnapshots.ApplyAsync(fixture.Store, child.RunId, child.Config);
            foreach (var (path, bytes) in captured.Files)
                Check(File.ReadAllBytes(LocalPath(child.Folder, path)).SequenceEqual(bytes), "A fresh child receives the first captured candidate, including binary and Unicode bytes.");
            Check(!File.Exists(LocalPath(child.Folder, DeletedPath)) && !File.Exists(Path.Combine(child.Folder, "later.txt")) &&
                !Directory.Exists(Path.Combine(child.Folder, "ignored")), "Replay applies the recorded deletion without importing later parent edits or ignored files.");
            Check(File.ReadAllText(Path.Combine(child.Folder, "unchanged.txt")) == "unchanged base\n" &&
                (await fixture.GitAsync(child.Folder, "rev-parse", "HEAD")).Trim() == fixture.BaseSha &&
                (await fixture.GitAsync(child.Folder, "rev-list", "--count", "HEAD")).Trim() == "1",
                "Replay changes only the candidate delta and never creates a Git commit.");
            Check((await fixture.GitAsync(child.Folder, "symbolic-ref", "-q", "HEAD", allowFailure: true)).ExitCode != 0,
                "A child can replay the candidate while remaining at a detached base revision.");
            Check(File.ReadAllBytes(Path.Combine(fixture.Store.RunDirectory(child.RunId), "task.json")).SequenceEqual(childTaskBytes),
                "Applying a candidate preserves the accepted child task record.");
        }
        CheckTreesEqual(parentAfterEditing, Tree(captured.Folder), "Replaying into separate worktree and clone children must preserve all later parent files.");
        Check((await fixture.GitAsync(captured.Folder, "rev-parse", "HEAD")).Trim() == fixture.BaseSha &&
            (await fixture.GitAsync(fixture.Repository, "rev-list", "--all", "--count")).Trim() == "1", "Capture and replay never commit or change the source revision.");
    }

    private static async Task DeduplicatesContentAndRetainsCleanBases(Fixture fixture)
    {
        var first = await fixture.CaptureCandidateAsync();
        var second = await fixture.CaptureCandidateAsync("feature-implement");
        Check(first.RunId != second.RunId && Text(first.Manifest, "snapshotHash") == Text(second.Manifest, "snapshotHash") &&
            fixture.ManifestPath(first.RunId) == fixture.ManifestPath(second.RunId) && fixture.ArchivePath(first.RunId) == fixture.ArchivePath(second.RunId),
            "Identical candidate bytes, paths, deletions and base share one immutable artifact across separate implementation runs.");
        Check(Text(fixture.Store.ReadJson(fixture.ReferencePath(first.RunId))!, "runId") == first.RunId &&
            Text(fixture.Store.ReadJson(fixture.ReferencePath(second.RunId))!, "runId") == second.RunId,
            "Content deduplication preserves each run's own reference identity.");

        var folder = await fixture.WorktreeAsync("clean"); var runId = fixture.CreateRun(folder);
        var reference = await CandidateSnapshots.CaptureAsync(fixture.Store, runId, folder);
        var manifest = fixture.Store.ReadJson(fixture.ManifestPath(runId))!;
        Check(Text(reference, "status") == "ready" && Text(manifest, "baseSha") == fixture.BaseSha &&
            manifest["files"] is JsonArray { Count: 0 } && manifest["deletedPaths"] is JsonArray { Count: 0 } && manifest["totalBytes"]!.GetValue<long>() == 0,
            "A clean candidate retains its actual complete HEAD with an explicit zero-byte delta.");
        var child = await fixture.CreateChildAsync(new Captured(runId, folder, manifest, new Dictionary<string, byte[]>()));
        var before = Tree(child.Folder);
        await CandidateSnapshots.ApplyAsync(fixture.Store, child.RunId, child.Config);
        CheckTreesEqual(before, Tree(child.Folder), "Applying a zero-delta candidate preserves the clean child repository.");
        Check(JsonNode.DeepEquals(CandidateSnapshots.Describe(fixture.Store, runId), CandidateSnapshots.Describe(fixture.Store, child.RunId)),
            "A verification child retains the verified input descriptor for later follow-up preparation.");
    }

    private static async Task AcceptedChildSurvivesParentRecordDeletion(Fixture fixture)
    {
        var captured = await fixture.CaptureCandidateAsync();
        var child = await fixture.CreateChildAsync(captured);
        var manifestPath = fixture.ManifestPath(captured.RunId); var archivePath = fixture.ArchivePath(captured.RunId);
        var manifest = File.ReadAllBytes(manifestPath); var archive = File.ReadAllBytes(archivePath);
        var status = fixture.Store.ReadStatus(captured.RunId); status["state"] = "succeeded";
        fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(captured.RunId), "status.json"), status);
        fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(captured.RunId), "view.json"), new JsonObject { ["read"] = true, ["handled"] = true });
        fixture.Store.DeleteRun(captured.RunId);
        Check(!Directory.Exists(fixture.Store.RunDirectory(captured.RunId)) && File.ReadAllBytes(manifestPath).SequenceEqual(manifest) &&
            File.ReadAllBytes(archivePath).SequenceEqual(archive), "Deleting a completed parent record preserves the immutable candidate held by an accepted child.");
        await CandidateSnapshots.ApplyAsync(fixture.Store, child.RunId, child.Config);
        foreach (var (path, bytes) in captured.Files)
            Check(File.ReadAllBytes(LocalPath(child.Folder, path)).SequenceEqual(bytes), "An accepted child can replay its saved candidate after its parent record is deleted.");
        Check(!File.Exists(LocalPath(child.Folder, DeletedPath)), "Parent record deletion cannot discard the accepted child's captured deletion.");
    }

    private static async Task PreparesVerificationFromTheRetainedCandidate(Fixture fixture)
    {
        var captured = await fixture.CaptureCandidateAsync("feature-implement");
        var result = PublishVerificationPlan(fixture, captured.RunId);
        var descriptor = CandidateSnapshots.Describe(fixture.Store, captured.RunId)!;
        var parentTaskBytes = File.ReadAllBytes(Path.Combine(fixture.Store.RunDirectory(captured.RunId), "task.json"));
        var parentResultBytes = File.ReadAllBytes(Path.Combine(fixture.Store.RunDirectory(captured.RunId), "result.json"));
        var parentTree = Tree(captured.Folder);
        JsonObject Payload(string runId, JsonObject report) => new()
        {
            ["runId"] = runId, ["requestId"] = Guid.NewGuid().ToString("D"),
            ["proposalId"] = WorkflowResult.WithProposalIds(report)["nextActions"]!.AsArray().OfType<JsonObject>()
                .Single(action => Text(action, "kind") == "start-task")["proposalId"]!.DeepClone()
        };
        var prepared = ResultTaskActions.Prepare(fixture.Store, Payload(captured.RunId, result));
        var task = prepared["task"]!.AsObject(); var source = task["planSource"]!.AsObject();
        var context = task["context"]!["resultPlan"]!.AsObject();
        Check(Text(task, "actionKind") == "issue-verify" && Text(source, "sourceKind") == "local-candidate" &&
            Text(source, "candidateSnapshotHash") == Text(descriptor, "snapshotHash") && Text(source, "revisionSha") == Text(descriptor, "baseSha") &&
            Text(source, "parentRunId") == captured.RunId, "Preparing an implementation follow-up binds its exact retained candidate identity and base.");
        Check(JsonNode.DeepEquals(context["plan"], result["plans"]![0]) && Text(context, "sourceKind") == "local-candidate" &&
            Text(context, "candidateSnapshotHash") == Text(descriptor, "snapshotHash") && Text(context, "initialRevisionSha") == Text(descriptor, "baseSha"),
            "The prepared context carries the complete saved verification plan and the same candidate identity unchanged.");

        var childFolder = await fixture.WorktreeAsync("prepared-verification"); var childId = Guid.NewGuid().ToString("D");
        var config = new JsonObject { ["repoFolder"] = childFolder, ["worktreeBase"] = fixture.BaseSha };
        fixture.Store.CreateRun(childId, task, config, Text(prepared, "sourceOrigin")!, new JsonObject { ["schemaVersion"] = 3 });
        await CandidateSnapshots.ApplyAsync(fixture.Store, childId, config);
        foreach (var (path, bytes) in captured.Files)
            Check(File.ReadAllBytes(LocalPath(childFolder, path)).SequenceEqual(bytes), "The real prepared task replays every retained binary, Unicode and untracked file byte.");
        Check(!File.Exists(LocalPath(childFolder, DeletedPath)) && (await fixture.GitAsync(childFolder, "rev-parse", "HEAD")).Trim() == fixture.BaseSha,
            "A prepared verification task restores the recorded deletion while retaining the exact base revision.");
        CheckTreesEqual(parentTree, Tree(captured.Folder), "Preparing and replaying verification cannot change the parent candidate.");
        Check(File.ReadAllBytes(Path.Combine(fixture.Store.RunDirectory(captured.RunId), "task.json")).SequenceEqual(parentTaskBytes) &&
            File.ReadAllBytes(Path.Combine(fixture.Store.RunDirectory(captured.RunId), "result.json")).SequenceEqual(parentResultBytes),
            "The real result-action path preserves the accepted parent task and complete final report.");

        var unavailableFolder = await fixture.WorktreeAsync("implementation-without-snapshot");
        var unavailableId = fixture.CreateRun(unavailableFolder, "feature-implement");
        var unavailableResult = PublishVerificationPlan(fixture, unavailableId);
        Check(CandidateSnapshots.Describe(fixture.Store, unavailableId) is null &&
            Text(fixture.Store.ReadTask(unavailableId)["config"]!.AsObject(), "worktreeBase") == fixture.BaseSha,
            "The missing-snapshot fixture still has a valid saved repository base.");
        Expect("CANDIDATE_SNAPSHOT_UNAVAILABLE", () => ResultTaskActions.Prepare(fixture.Store, Payload(unavailableId, unavailableResult)));
    }

    private static JsonObject PublishVerificationPlan(Fixture fixture, string runId)
    {
        var parent = fixture.Store.ReadTask(runId)["task"]!.AsObject();
        var model = WorkflowV3Scenarios.Model("feature-implement");
        model["assessment"]!["subject"] = "local-candidate"; model["assessment"]!["revisionSha"] = fixture.BaseSha;
        var plan = WorkflowV3Scenarios.Plan("issue-verify");
        plan["summary"] = "Verify the exact retained implementation candidate.";
        plan["steps"] = new JsonArray("Inspect binary.bin and notes/候補 😀.txt in the restored child.", "Confirm the removed tracked file remains absent and record the observed behavior.");
        plan["acceptanceCriteria"] = new JsonArray("Binary and Unicode candidate contents match the retained implementation.", "The recorded deletion and targeted behavior remain correct.");
        plan["prerequisites"] = new JsonArray("Use the separate child worktree with the Host-restored candidate.");
        plan["evidence"] = new JsonArray("The completed parent report identifies the implemented paths and retained source.");
        model["plans"] = new JsonArray(plan.DeepClone());
        model["nextActions"] = new JsonArray(new JsonObject
        {
            ["kind"] = "start-task", ["reason"] = "Run the saved candidate verification plan.", ["body"] = "", ["recommended"] = true,
            ["taskKind"] = "issue-verify", ["planId"] = plan["id"]!.DeepClone()
        });
        var result = WorkflowResult.FromModel(model.ToJsonString(), parent, "succeeded", 0, null, null, expectedSchemaVersion: 3);
        Check(WorkflowResult.IsValidStoredV3(result) && WorkflowResult.IsFinalReportComplete(result) && JsonNode.DeepEquals(result["plans"]![0], plan),
            "The integration fixture publishes a complete valid v3 implementation report without changing its verification plan.");
        fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(runId), "result.json"), result);
        var status = fixture.Store.ReadStatus(runId); status["state"] = "succeeded"; status["exitCode"] = 0;
        fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(runId), "status.json"), status);
        return result;
    }

    private static async Task MissingAndIneligibleSourcesStayUnavailable(Fixture fixture)
    {
        var folder = await fixture.WorktreeAsync("missing");
        var missingId = fixture.CreateRun(folder);
        Check(CandidateSnapshots.Describe(fixture.Store, missingId) is null, "Historical runs without a snapshot cannot claim a local candidate.");
        var missing = new Captured(missingId, folder, new JsonObject { ["snapshotHash"] = new string('a', 64) }, new Dictionary<string, byte[]>());
        var child = await fixture.CreateChildAsync(missing);
        await RejectWithoutChildWrites(fixture, child, "CANDIDATE_SNAPSHOT_UNAVAILABLE");

        foreach (var (kind, version, state) in new[] { ("issue-fix", 2, "running"), ("bug-investigation", 3, "running"), ("issue-fix", 3, "succeeded") })
        {
            var id = fixture.CreateRun(folder, kind, version, state);
            await ExpectAsync("CANDIDATE_SNAPSHOT_UNAVAILABLE", () => CandidateSnapshots.CaptureAsync(fixture.Store, id, folder));
            Check(!File.Exists(fixture.ReferencePath(id)) && CandidateSnapshots.Describe(fixture.Store, id) is null,
                "An ineligible workflow is rejected without retroactively adding candidate metadata to its record.");
        }
        var notRepository = Path.Combine(fixture.Root, "not-a-repository"); Directory.CreateDirectory(notRepository);
        var invalidId = fixture.CreateRun(notRepository);
        var unavailable = await CandidateSnapshots.CaptureAsync(fixture.Store, invalidId, notRepository);
        Check(Text(unavailable, "status") == "unavailable" && CandidateSnapshots.Describe(fixture.Store, invalidId) is null,
            "A failed repository observation is an explicit unavailable snapshot.");
    }

    private static async Task OversizedAndLinkedFilesStayUnavailable(Fixture fixture)
    {
        var oversized = await fixture.WorktreeAsync("oversized");
        using (var file = new FileStream(Path.Combine(oversized, "oversized.bin"), FileMode.CreateNew, FileAccess.Write))
            file.SetLength(64L * 1024 * 1024 + 1);
        var oversizedId = fixture.CreateRun(oversized);
        Check(Text(await CandidateSnapshots.CaptureAsync(fixture.Store, oversizedId, oversized), "status") == "unavailable" &&
            CandidateSnapshots.Describe(fixture.Store, oversizedId) is null, "A file over the capture budget is rejected without allocating or archiving its full contents.");

        var linked = await fixture.WorktreeAsync("linked");
        var target = Path.Combine(fixture.Root, "link-target.txt"); File.WriteAllText(target, "outside the candidate worktree\n");
        try { File.CreateSymbolicLink(Path.Combine(linked, "candidate-link.txt"), target); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Console.WriteLine("NOTE candidate snapshots: this environment did not allow a symbolic-link fixture; link-capture execution remains unverified.");
            return;
        }
        var linkedId = fixture.CreateRun(linked);
        Check(Text(await CandidateSnapshots.CaptureAsync(fixture.Store, linkedId, linked), "status") == "unavailable" &&
            CandidateSnapshots.Describe(fixture.Store, linkedId) is null && File.ReadAllText(target) == "outside the candidate worktree\n",
            "Capture rejects a symbolic link without following or modifying its target.");
    }

    private static async Task CorruptionCannotWriteToTheChild(Fixture fixture)
    {
        var captured = await fixture.CaptureCandidateAsync();
        var originalReference = File.ReadAllBytes(fixture.ReferencePath(captured.RunId));
        var originalManifestPath = fixture.ManifestPath(captured.RunId); var originalArchivePath = fixture.ArchivePath(captured.RunId);
        var originalManifest = File.ReadAllBytes(fixture.ManifestPath(captured.RunId));
        var originalArchive = File.ReadAllBytes(fixture.ArchivePath(captured.RunId));
        var parentTree = Tree(captured.Folder);
        void Restore()
        {
            File.WriteAllBytes(fixture.ReferencePath(captured.RunId), originalReference);
            File.WriteAllBytes(originalManifestPath, originalManifest);
            File.WriteAllBytes(originalArchivePath, originalArchive);
        }
        async Task Reject(JsonObject? changedManifest = null, string code = "CANDIDATE_SNAPSHOT_INVALID")
        {
            Expect(code, () => CandidateSnapshots.Describe(fixture.Store, captured.RunId));
            var source = captured with { Manifest = changedManifest ?? captured.Manifest };
            await RejectWithoutChildWrites(fixture, await fixture.CreateChildAsync(source), code);
            CheckTreesEqual(parentTree, Tree(captured.Folder), "Invalid source data must never alter the retained parent candidate.");
            Restore();
        }

        File.WriteAllBytes(fixture.ArchivePath(captured.RunId), [.. originalArchive, 42]);
        await Reject();

        var changed = captured.Manifest.DeepClone().AsObject(); changed["deletedPaths"]!.AsArray().Add("unchanged.txt");
        fixture.Store.WriteJson(fixture.ManifestPath(captured.RunId), changed);
        await Reject();

        File.WriteAllText(fixture.ManifestPath(captured.RunId), "{invalid manifest");
        await Reject();

        File.Delete(fixture.ArchivePath(captured.RunId));
        await Reject(code: "CANDIDATE_SNAPSHOT_UNAVAILABLE");

        // Keep both overall hashes valid so verification must read and hash each file's actual content.
        var forgedFiles = captured.Files.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
        forgedFiles["tracked.txt"][0] ^= 0x7f;
        RewriteArchive(fixture.ArchivePath(captured.RunId), forgedFiles.Select(pair => (pair.Key, pair.Value)));
        changed = RewriteIdentity(fixture, captured.RunId, captured.Manifest);
        await Reject(changed);

        // Correct content and aggregate checksums cannot turn a Unix symlink entry into a regular file.
        RewriteArchive(fixture.ArchivePath(captured.RunId), captured.Files.Select(pair => (pair.Key, pair.Value)), symlinkPath: "tracked.txt");
        changed = RewriteIdentity(fixture, captured.RunId, captured.Manifest);
        await Reject(changed);

        var absoluteCanary = Path.Combine(fixture.Root, "absolute-entry-canary.txt");
        const string canaryContents = "Outside the child; preserve this fixture canary.\n";
        File.WriteAllText(absoluteCanary, canaryContents);
        var absoluteEntry = absoluteCanary.Replace(Path.DirectorySeparatorChar, '/');
        var driveRoot = Path.GetPathRoot(absoluteCanary)!;
        var rootRelativeEntry = OperatingSystem.IsWindows() && driveRoot.Length == 3 && driveRoot[1] == ':' ? absoluteEntry[2..] : absoluteEntry;
        foreach (var unsafePath in new[] { "../escape.txt", "nested/../../escape.txt", rootRelativeEntry, absoluteEntry, "nested\\..\\escape.txt", ".git/config", "tracked.txt:stream" }.Distinct(StringComparer.Ordinal))
        {
            var attack = Encoding.UTF8.GetBytes("must not reach a destination\n");
            RewriteArchive(fixture.ArchivePath(captured.RunId), captured.Files.Select(pair => (pair.Key, pair.Value)).Append((unsafePath, attack)));
            changed = captured.Manifest.DeepClone().AsObject();
            changed["files"]!.AsArray().Add(new JsonObject { ["path"] = unsafePath, ["length"] = attack.Length, ["sha256"] = Hash(attack) });
            changed["totalBytes"] = changed["totalBytes"]!.GetValue<long>() + attack.LongLength;
            changed = RewriteIdentity(fixture, captured.RunId, changed);
            await Reject(changed);
            Check(!File.Exists(Path.Combine(fixture.Root, "worktrees", "escape.txt")) && File.ReadAllText(absoluteCanary) == canaryContents,
                "Traversal and rooted entries cannot write outside a child worktree, including other fixture-owned paths.");
        }

        changed = captured.Manifest.DeepClone().AsObject(); changed["deletedPaths"]!.AsArray().Add("../escape.txt");
        changed = RewriteIdentity(fixture, captured.RunId, changed);
        await Reject(changed);

        // An unlisted or duplicate ZIP entry cannot be silently applied, even with a correct archive digest.
        foreach (var extra in new[] { ("unlisted.txt", Encoding.UTF8.GetBytes("unlisted\n")), ("tracked.txt", captured.Files["tracked.txt"]) })
        {
            RewriteArchive(fixture.ArchivePath(captured.RunId), captured.Files.Select(pair => (pair.Key, pair.Value)).Append(extra));
            changed = RewriteIdentity(fixture, captured.RunId, captured.Manifest);
            await Reject(changed);
        }

        var missingIdentity = captured.Manifest.DeepClone().AsObject(); missingIdentity["snapshotHash"] = new string('0', 64);
        await RejectWithoutChildWrites(fixture, await fixture.CreateChildAsync(captured with { Manifest = missingIdentity }), "CANDIDATE_SNAPSHOT_UNAVAILABLE");
        Check(File.ReadAllBytes(fixture.ManifestPath(captured.RunId)).SequenceEqual(originalManifest) &&
            File.ReadAllBytes(fixture.ArchivePath(captured.RunId)).SequenceEqual(originalArchive), "Failed replay never rewrites the immutable snapshot source.");
    }

    private static async Task PreCancelledOperationsPreserveTheWorkspace(Fixture fixture)
    {
        var folder = await fixture.WorktreeAsync("cancelled-capture");
        Write(folder, "tracked.txt", Encoding.UTF8.GetBytes("candidate present before cancellation\n"));
        var parentTree = Tree(folder); var runId = fixture.CreateRun(folder);
        var storage = Path.Combine(fixture.Store.Root, "candidate-snapshots");
        var artifacts = Directory.EnumerateFiles(storage).Order(StringComparer.Ordinal).ToArray();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var unavailable = await CandidateSnapshots.CaptureAsync(fixture.Store, runId, folder, cancelled.Token);
        Check(Text(unavailable, "status") == "unavailable" && CandidateSnapshots.Describe(fixture.Store, runId) is null &&
            Directory.EnumerateFiles(storage).Order(StringComparer.Ordinal).SequenceEqual(artifacts),
            "A pre-cancelled capture creates no immutable artifact and exposes an unavailable source.");
        CheckTreesEqual(parentTree, Tree(folder), "A cancelled capture cannot change the parent candidate.");

        var captured = await fixture.CaptureCandidateAsync(); var child = await fixture.CreateChildAsync(captured);
        var childTree = Tree(child.Folder);
        try
        {
            await CandidateSnapshots.ApplyAsync(fixture.Store, child.RunId, child.Config, cancelled.Token);
            throw new InvalidOperationException("Expected candidate replay cancellation.");
        }
        catch (OperationCanceledException) { }
        CheckTreesEqual(childTree, Tree(child.Folder), "A pre-cancelled replay must not change any child file.");
        Check(!File.Exists(Path.Combine(fixture.Store.RunDirectory(child.RunId), "candidate-input.json")),
            "A pre-cancelled replay cannot record a completed candidate input.");
    }

    private static async Task MismatchedBasesCannotWriteToTheChild(Fixture fixture)
    {
        var captured = await fixture.CaptureCandidateAsync();
        var child = await fixture.CreateChildAsync(captured);
        Write(child.Folder, "unrelated-commit.txt", Encoding.UTF8.GetBytes("a different child revision\n"));
        await fixture.GitAsync(child.Folder, "add", "--", "unrelated-commit.txt");
        await fixture.GitAsync(child.Folder, "commit", "-m", "different fixture base");
        Check((await fixture.GitAsync(child.Folder, "rev-parse", "HEAD")).Trim() != fixture.BaseSha, "The fixture must have a genuinely different actual HEAD.");
        await RejectWithoutChildWrites(fixture, child, "CANDIDATE_BASE_MISMATCH");

        child = await fixture.CreateChildAsync(captured);
        child.Config["worktreeBase"] = new string('0', 40);
        await RejectWithoutChildWrites(fixture, child, "CANDIDATE_BASE_MISMATCH");
    }

    private static async Task RejectWithoutChildWrites(Fixture fixture, Child child, string code)
    {
        var before = Tree(child.Folder);
        var head = await fixture.GitAsync(child.Folder, "rev-parse", "HEAD");
        var status = await fixture.GitAsync(child.Folder, "status", "--porcelain=v1", "-z", "--untracked-files=all");
        await ExpectAsync(code, () => CandidateSnapshots.ApplyAsync(fixture.Store, child.RunId, child.Config));
        CheckTreesEqual(before, Tree(child.Folder), "A rejected snapshot must not partially create, overwrite or delete any child file: " + code);
        Check(await fixture.GitAsync(child.Folder, "rev-parse", "HEAD") == head &&
            await fixture.GitAsync(child.Folder, "status", "--porcelain=v1", "-z", "--untracked-files=all") == status,
            "A rejected snapshot must preserve the child revision, index and worktree status: " + code);
    }

    private static JsonObject RewriteIdentity(Fixture fixture, string runId, JsonObject manifest)
    {
        var changed = manifest.DeepClone().AsObject();
        var archive = File.ReadAllBytes(fixture.ArchivePath(runId));
        changed["archiveSha256"] = Hash(archive);
        changed["snapshotHash"] = ManifestHash(changed);
        var hash = Text(changed, "snapshotHash")!;
        fixture.Store.WriteJson(fixture.GlobalManifestPath(hash), changed);
        File.WriteAllBytes(fixture.GlobalArchivePath(hash), archive);
        var reference = fixture.Store.ReadJson(fixture.ReferencePath(runId))!; reference["snapshotHash"] = hash;
        fixture.Store.WriteJson(fixture.ReferencePath(runId), reference);
        return changed;
    }

    private static string ManifestHash(JsonObject manifest)
    {
        var identity = manifest.DeepClone().AsObject(); identity.Remove("snapshotHash");
        return Protocol.Fingerprint(identity);
    }

    private static void RewriteArchive(string path, IEnumerable<(string Path, byte[] Bytes)> files, string? symlinkPath = null)
    {
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var (name, bytes) in files)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            if (name == symlinkPath) entry.ExternalAttributes = (0xa000 | 0x1ff) << 16;
            using var content = entry.Open(); content.Write(bytes);
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string? Text(JsonObject value, string key) => value[key]?.GetValue<string>();
    private static string LocalPath(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static void Write(string root, string relative, byte[] bytes)
    {
        var path = LocalPath(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes);
    }

    private static Dictionary<string, string> Tree(string folder)
    {
        var tree = new Dictionary<string, string>(StringComparer.Ordinal);
        void Visit(string directory)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var relative = Path.GetRelativePath(folder, path).Replace(Path.DirectorySeparatorChar, '/');
                if (relative == ".git") continue;
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) tree[relative] = "reparse:" + new FileInfo(path).LinkTarget;
                else if ((attributes & FileAttributes.Directory) != 0) { tree[relative + "/"] = "directory"; Visit(path); }
                else tree[relative] = Hash(File.ReadAllBytes(path));
            }
        }
        Visit(folder); return tree;
    }

    private static void CheckTreesEqual(Dictionary<string, string> expected, Dictionary<string, string> actual, string message) =>
        Check(expected.Count == actual.Count && expected.All(pair => actual.TryGetValue(pair.Key, out var value) && pair.Value == value), message);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect(string code, Action action)
    {
        try { action(); }
        catch (ProtocolException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected " + code + ".");
    }
    private static async Task ExpectAsync(string code, Func<Task> action)
    {
        try { await action(); }
        catch (ProtocolException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected " + code + ".");
    }

    private sealed record Captured(string RunId, string Folder, JsonObject Manifest, Dictionary<string, byte[]> Files);
    private sealed record Child(string RunId, string Folder, JsonObject Config);

    private sealed class Fixture : IDisposable
    {
        private const string Prefix = "PulseCandidateSnapshotTests-";
        private readonly string git = RuntimeService.ResolveExecutable("git") ?? throw new InvalidOperationException("Git is required for offline candidate snapshot fixtures.");
        public string Root { get; } = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        public string Repository => Path.Combine(Root, "source");
        public string BaseSha { get; private set; } = "";
        public Store Store { get; }
        private Fixture() { Store = new Store(Path.Combine(Root, "store")); }

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            try
            {
                Directory.CreateDirectory(fixture.Repository); Directory.CreateDirectory(Path.Combine(fixture.Root, "hooks"));
                File.WriteAllText(Path.Combine(fixture.Root, "git-empty.config"), "");
                await fixture.GitAsync(fixture.Repository, "init", "--initial-branch=fixture-main");
                foreach (var (key, value) in new[]
                {
                    ("user.name", "Pulse candidate fixture"), ("user.email", "candidate-fixture@example.invalid"),
                    ("commit.gpgsign", "false"), ("tag.gpgsign", "false"), ("core.autocrlf", "false"),
                    ("core.safecrlf", "false"), ("core.fsmonitor", "false"), ("core.untrackedCache", "false"),
                    ("core.hooksPath", Path.Combine(fixture.Root, "hooks")), ("gc.auto", "0"), ("maintenance.auto", "false")
                }) await fixture.GitAsync(fixture.Repository, "config", "--local", key, value);
                Write(fixture.Repository, "tracked.txt", Encoding.UTF8.GetBytes("base tracked\n"));
                Write(fixture.Repository, "binary.bin", OriginalBinary);
                Write(fixture.Repository, DeletedPath, Encoding.UTF8.GetBytes("tracked deletion baseline\n"));
                Write(fixture.Repository, "unchanged.txt", Encoding.UTF8.GetBytes("unchanged base\n"));
                Write(fixture.Repository, "tracked-directory/nested/original.txt", Encoding.UTF8.GetBytes("tracked nested baseline\n"));
                Write(fixture.Repository, ".gitignore", Encoding.UTF8.GetBytes("ignored/\n"));
                await fixture.GitAsync(fixture.Repository, "add", "--all");
                await fixture.GitAsync(fixture.Repository, "commit", "-m", "candidate snapshot fixture baseline");
                fixture.BaseSha = (await fixture.GitAsync(fixture.Repository, "rev-parse", "HEAD")).Trim();
                Check(fixture.BaseSha.Length == 40, "The fixture requires the default SHA1 Git object format.");
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }

        public async Task<string> WorktreeAsync(string label)
        {
            var folder = Path.Combine(Root, "worktrees", label + "-" + Guid.NewGuid().ToString("N"));
            await GitAsync(Repository, "worktree", "add", "--detach", folder, BaseSha);
            return folder;
        }

        public string CreateRun(string folder, string kind = "issue-fix", int version = 3, string state = "running", JsonObject? source = null)
        {
            var id = Guid.NewGuid().ToString("D");
            var task = new JsonObject
            {
                ["requestId"] = id, ["actionId"] = "candidate-snapshot-fixture", ["actionKind"] = kind,
                ["repository"] = "microsoft/powertoys", ["target"] = new JsonObject { ["type"] = "issue", ["number"] = 50027 },
                ["prompt"] = "Controlled offline candidate snapshot fixture."
            };
            if (kind == "feature-implement" && source is null)
                source = new JsonObject
                {
                    ["parentRunId"] = Guid.NewGuid().ToString("D"), ["parentResultFingerprint"] = new string('b', 64),
                    ["proposalId"] = "implementation-proposal", ["planId"] = "implementation-plan", ["repository"] = "microsoft/powertoys",
                    ["target"] = task["target"]!.DeepClone(), ["revisionSha"] = BaseSha, ["sourceKind"] = "original"
                };
            if (source is not null) task["planSource"] = source.DeepClone();
            task = Protocol.ValidateTask(task, allowFollowUp: source is not null);
            Store.CreateRun(id, task, Config(folder), Protocol.ProductionOrigin, new JsonObject { ["schemaVersion"] = version });
            var status = Store.ReadStatus(id); status["state"] = state;
            Store.WriteJson(Path.Combine(Store.RunDirectory(id), "status.json"), status);
            return id;
        }

        public async Task<Captured> CaptureCandidateAsync(string kind = "issue-fix")
        {
            var folder = await WorktreeAsync("parent");
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["tracked.txt"] = Encoding.UTF8.GetBytes("candidate tracked 漢字\n"),
                ["binary.bin"] = Enumerable.Range(0, 8192).Select(index => (byte)(index % 256)).ToArray(),
                [UnicodePath] = Encoding.UTF8.GetBytes("untracked Unicode candidate\n"),
                ["new directory/nested.txt"] = Encoding.UTF8.GetBytes("nested untracked candidate\n")
            };
            foreach (var (path, bytes) in files) Write(folder, path, bytes);
            await GitAsync(folder, "add", "--", "tracked.txt");
            File.Delete(LocalPath(folder, DeletedPath));
            Write(folder, "ignored/private.txt", Encoding.UTF8.GetBytes("ignored fixture data\n"));
            var id = CreateRun(folder, kind); var before = Tree(folder);
            var taskBytes = File.ReadAllBytes(Path.Combine(Store.RunDirectory(id), "task.json"));
            var status = await GitAsync(folder, "status", "--porcelain=v1", "-z", "--untracked-files=all");
            var reference = await CandidateSnapshots.CaptureAsync(Store, id, folder);
            Check(Text(reference, "status") == "ready", "The complete controlled candidate must be capturable.");
            var manifest = Store.ReadJson(ManifestPath(id))!;
            CheckTreesEqual(before, Tree(folder), "Capture preserves every parent file, including ignored data.");
            Check(await GitAsync(folder, "status", "--porcelain=v1", "-z", "--untracked-files=all") == status &&
                (await GitAsync(folder, "rev-parse", "HEAD")).Trim() == BaseSha &&
                File.ReadAllBytes(Path.Combine(Store.RunDirectory(id), "task.json")).SequenceEqual(taskBytes),
                "Capture must preserve the parent Git state and immutable accepted task.");
            return new Captured(id, folder, manifest, files);
        }

        public async Task<Child> CreateChildAsync(Captured source, bool clone = false)
        {
            string folder;
            if (clone)
            {
                folder = Path.Combine(Root, "clone-" + Guid.NewGuid().ToString("N"));
                await GitAsync(Root, "clone", "--no-hardlinks", "--no-checkout", "--", Repository, folder);
                await GitAsync(folder, "checkout", "--detach", BaseSha);
            }
            else folder = await WorktreeAsync("child");
            var planSource = new JsonObject
            {
                ["parentRunId"] = source.RunId, ["parentResultFingerprint"] = new string('b', 64), ["proposalId"] = "candidate-proposal",
                ["planId"] = "candidate-plan", ["repository"] = "microsoft/powertoys",
                ["target"] = new JsonObject { ["type"] = "issue", ["number"] = 50027 }, ["revisionSha"] = BaseSha,
                ["sourceKind"] = "local-candidate", ["candidateSnapshotHash"] = Text(source.Manifest, "snapshotHash")
            };
            var id = CreateRun(folder, "issue-verify", source: planSource);
            return new Child(id, folder, Config(folder));
        }

        private JsonObject Config(string folder) => new() { ["repoFolder"] = folder, ["worktreeBase"] = BaseSha };
        public string ReferencePath(string id) => Path.Combine(Store.RunDirectory(id), "candidate-snapshot.json");
        public string ManifestPath(string id) => GlobalManifestPath(Text(Store.ReadJson(ReferencePath(id))!, "snapshotHash")!);
        public string ArchivePath(string id) => GlobalArchivePath(Text(Store.ReadJson(ReferencePath(id))!, "snapshotHash")!);
        public string GlobalManifestPath(string hash) => Path.Combine(Store.Root, "candidate-snapshots", hash + ".json");
        public string GlobalArchivePath(string hash) => Path.Combine(Store.Root, "candidate-snapshots", hash + ".zip");

        public async Task<string> GitAsync(string folder, params string[] arguments) => (await RunGitAsync(folder, arguments)).Output;
        public Task<(int ExitCode, string Output)> GitAsync(string folder, string a, string b, string c, bool allowFailure) =>
            RunGitAsync(folder, [a, b, c], allowFailure);
        private async Task<(int ExitCode, string Output)> RunGitAsync(string folder, string[] arguments, bool allowFailure = false)
        {
            Check(IsInsideRoot(folder), "Every Git fixture command must remain inside its dedicated temporary root.");
            var start = new ProcessStartInfo(git)
            {
                WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1"; start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(Root, "git-empty.config");
            start.Environment["GIT_TERMINAL_PROMPT"] = "0"; start.Environment["GCM_INTERACTIVE"] = "never";
            foreach (var option in new[] { "-c", "core.hooksPath=" + Path.Combine(Root, "hooks"), "-c", "commit.gpgsign=false", "-c", "protocol.file.allow=always" }) start.ArgumentList.Add(option);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The offline Git fixture could not start.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            process.StandardInput.Close();
            var output = ReadBoundedAsync(process.StandardOutput, deadline.Token); var error = ReadBoundedAsync(process.StandardError, deadline.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token); var text = await output; var diagnostic = await error;
                if (process.ExitCode != 0 && !allowFailure) throw new InvalidOperationException("Offline Git fixture failed: " + arguments[0] + ": " + diagnostic);
                return (process.ExitCode, text);
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                try { await Task.WhenAll(output, error); } catch (Exception failure) when (failure is IOException or OperationCanceledException) { }
            }
        }

        private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
        {
            var text = new StringBuilder(); var buffer = new char[4096]; int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
            {
                if (text.Length + count > 256 * 1024) throw new IOException("Offline Git fixture output exceeded its limit.");
                text.Append(buffer, 0, count);
            }
            return text.ToString();
        }

        private bool IsInsideRoot(string path)
        {
            var full = Path.GetFullPath(path); var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root));
            return full.Equals(root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root));
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            var name = Path.GetFileName(root);
            Check(Path.GetDirectoryName(root)!.Equals(temporary, StringComparison.OrdinalIgnoreCase) && name.StartsWith(Prefix, StringComparison.Ordinal) &&
                Guid.TryParseExact(name[Prefix.Length..], "N", out _), "Fixture cleanup requires its exact unique direct-child temporary directory.");
            void Remove(string path)
            {
                Check(IsInsideRoot(path), "Cleanup cannot leave the validated fixture directory.");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(path); else File.Delete(path);
                    return;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(path)) Remove(entry);
                    File.SetAttributes(path, FileAttributes.Normal); Directory.Delete(path);
                }
                else { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
            }
            if (Directory.Exists(root)) Remove(root);
        }
    }
}
