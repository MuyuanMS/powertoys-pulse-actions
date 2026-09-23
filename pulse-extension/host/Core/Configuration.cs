using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pulse.Host;

public sealed partial class Configuration(Store store, Func<Task<JsonObject>>? getAccounts = null, Func<string, Task<JsonArray>>? listInstallations = null)
{
    public const string PowerToysRepository = "microsoft/powertoys";

    public JsonObject Read()
    {
        var saved = store.ReadJson(Path.Combine(store.Root, "config.json"));
        var main = saved?["mainRepoFolder"]?.GetValue<string>();
        if (main is null && saved?["repositories"] is JsonArray old)
            main = old.OfType<JsonObject>().FirstOrDefault(row => string.Equals(row["repository"]?.GetValue<string>(), PowerToysRepository, StringComparison.OrdinalIgnoreCase))?["repoFolder"]?.GetValue<string>();
        return new JsonObject
        {
            ["agent"] = saved?["agent"]?.DeepClone() ?? JsonValue.Create("codex"),
            ["agentDefaults"] = ExecutionOptions.Defaults(saved?["agentDefaults"]),
            ["cliSelections"] = CliSelections.Read(saved?["cliSelections"]),
            ["permission"] = saved?["permission"]?.DeepClone() ?? JsonValue.Create("yolo"),
            ["mainRepoFolder"] = main ?? "", ["worktreeRoot"] = saved?["worktreeRoot"]?.DeepClone() ?? JsonValue.Create(""),
            ["githubAccount"] = saved?["githubAccount"]?.DeepClone() ?? JsonValue.Create(""),
            ["prPrompt"] = saved?["prPrompt"]?.DeepClone() ?? JsonValue.Create(TaskPrompt.DefaultPrPrompt),
            ["issuePrompt"] = saved?["issuePrompt"]?.DeepClone() ?? JsonValue.Create(TaskPrompt.DefaultIssuePrompt),
            ["e2ePrompt"] = saved?["e2ePrompt"]?.DeepClone() ?? JsonValue.Create(TaskPrompt.DefaultE2ePrompt),
            ["reproductionPrompt"] = saved?["reproductionPrompt"]?.DeepClone() ?? JsonValue.Create("")
        };
    }

    public async Task<JsonObject> SaveAsync(JsonObject input)
    {
        Protocol.OnlyKeys(input, "agent", "agentDefaults", "cliSelections", "permission", "mainRepoFolder", "worktreeRoot", "githubAccount", "prPrompt", "issuePrompt", "e2ePrompt", "reproductionPrompt");
        var config = (JsonObject)input.DeepClone();
        var previousSelections = Read()["cliSelections"]!.AsObject();
        config["cliSelections"] = input.ContainsKey("cliSelections")
            ? await CliSelections.ValidateSaveAsync(input["cliSelections"], previousSelections, listInstallations ?? RuntimeService.ListAgentInstallationsAsync)
            : previousSelections.DeepClone();
        if (input.ContainsKey("agentDefaults") && input["agentDefaults"] is not JsonObject)
            throw new ProtocolException("INVALID_EXECUTION", "Agent defaults must be an object.");
        config["agentDefaults"] = input.ContainsKey("agentDefaults") ? ExecutionOptions.Defaults(input["agentDefaults"]) : Read()["agentDefaults"]!.DeepClone();
        if (Protocol.RequiredString(config, "agent") is not ("codex" or "copilot"))
            throw new ProtocolException("INVALID_CONFIG", "Choose Codex or Copilot.");
        if (Protocol.RequiredString(config, "permission") is not ("read-only" or "workspace-write" or "yolo"))
            throw new ProtocolException("INVALID_CONFIG", "Choose read-only, workspace-write, or YOLO.");
        var main = OptionalFolder(config, "mainRepoFolder");
        var worktrees = OptionalFolder(config, "worktreeRoot");
        if (main.Length > 0) main = (await ResolveMainRepositoryAsync(main)).Folder;
        if (worktrees.Length > 0) worktrees = ValidateWorktreeRoot(worktrees, main);
        var account = config["githubAccount"]?.GetValue<string>() ?? "";
        if (account.Length > 0)
        {
            if (!AccountPattern().IsMatch(account)) throw new ProtocolException("INVALID_CONFIG", "The GitHub account name is invalid.");
            var accounts = getAccounts is null ? await GitHubSession.GetAccountsAsync() : await getAccounts();
            var selected = accounts["accounts"]?.AsArray().OfType<JsonObject>().FirstOrDefault(row =>
                string.Equals(row["login"]?.GetValue<string>(), account, StringComparison.OrdinalIgnoreCase) && row["state"]?.GetValue<string>() == "success");
            if (selected is null) throw new ProtocolException("GITHUB_ACCOUNT_UNAVAILABLE", "The selected account is not signed in through the local gh CLI.", "Refresh GitHub sign-in status, select an available account, and save.");
            account = selected["login"]!.GetValue<string>();
        }
        config["mainRepoFolder"] = main; config["worktreeRoot"] = worktrees; config["githubAccount"] = account;
        var catalog = new PromptCatalog(store);
        foreach (var (field, actionKind) in new[] { ("prPrompt", "pr-review"), ("issuePrompt", "issue-fix"), ("e2ePrompt", "e2e"), ("reproductionPrompt", "reproduction-setup") })
        {
            if ((field is "e2ePrompt" or "reproductionPrompt") && !input.ContainsKey(field))
            {
                // Older extension versions cannot edit new action choices; preserve them on save.
                config[field] = Read()[field]!.DeepClone();
                continue;
            }
            var selectedPrompt = "";
            if (config[field] is not null)
            {
                if (config[field] is not JsonValue value || !value.TryGetValue<string>(out selectedPrompt) || selectedPrompt.Length > 150 || selectedPrompt.Contains('\0'))
                    throw new ProtocolException("INVALID_PROMPT_NAME", "Prompt selections must be local catalog filenames.");
            }
            TaskPrompt.ValidateSelection(catalog, selectedPrompt, actionKind);
            config[field] = selectedPrompt;
        }
        using var held = store.AcquireLock("config");
        store.WriteJson(Path.Combine(store.Root, "config.json"), config);
        return Read();
    }

    public async Task<JsonObject> SnapshotAsync(string repository, string runId, JsonNode? taskExecution = null)
    {
        RequirePowerToys(repository);
        var config = Read();
        var execution = ExecutionOptions.Resolve(config, taskExecution);
        var agent = execution["agent"]!.GetValue<string>();
        CliSelections.RequirePath(config, agent);
        var resolved = await ResolveExecutionFoldersAsync(config);
        var probe = await CliSelections.ProbeSelectedAsync(config, agent);
        CliSelections.RequireAvailable(probe);
        var id = Protocol.RunId(runId);
        var baseSha = (await GitAsync(resolved.Folder, ["rev-parse", "--verify", "HEAD^{commit}"])).Trim();
        ValidateSha(baseSha);
        return new JsonObject
        {
            ["agent"] = agent, ["cliPath"] = probe["path"]!.DeepClone(), ["cliVersion"] = probe["version"]?.DeepClone(),
            ["model"] = execution["model"]!.DeepClone(), ["reasoningEffort"] = execution["reasoningEffort"]!.DeepClone(),
            ["executionSource"] = execution["executionSource"]!.DeepClone(),
            ["mainRepoFolder"] = resolved.Folder, ["worktreeRoot"] = resolved.WorktreeRoot,
            ["repoFolder"] = Path.Combine(resolved.WorktreeRoot, "pulse-" + id), ["worktreeBranch"] = "codex/pulse-" + id,
            ["worktreeBase"] = baseSha, ["repositoryKey"] = resolved.Key, ["permission"] = config["permission"]!.DeepClone(),
            ["githubAccount"] = config["githubAccount"]!.DeepClone(),
            ["prPrompt"] = config["prPrompt"]!.DeepClone(), ["issuePrompt"] = config["issuePrompt"]!.DeepClone(),
            ["e2ePrompt"] = config["e2ePrompt"]!.DeepClone(), ["reproductionPrompt"] = config["reproductionPrompt"]!.DeepClone()
        };
    }

    internal static async Task<(string Folder, string Key, string WorktreeRoot)> ResolveExecutionFoldersAsync(JsonObject config)
    {
        var main = OptionalFolder(config, "mainRepoFolder");
        var worktrees = OptionalFolder(config, "worktreeRoot");
        if (main.Length == 0 || worktrees.Length == 0)
            throw new ProtocolException("REPO_NOT_CONFIGURED", "Configure the main PowerToys checkout and worktree root folder.", "Enter both local folders in Settings. CLI paths are detected automatically.");
        var resolved = await ResolveMainRepositoryAsync(main);
        return (resolved.Folder, resolved.Key, ValidateWorktreeRoot(worktrees, resolved.Folder));
    }

    // Runs in the persistent worker under its common-repository lock; never checks out the main tree.
    public static async Task PrepareWorktreeAsync(Store store, string runId)
    {
        using var cancellation = new CancellationTokenSource();
        var watching = WatchCancellationAsync(store, runId, cancellation);
        try { await PrepareWorktreeCoreAsync(store, runId, cancellation.Token); }
        catch (OperationCanceledException) { throw new ProtocolException("CANCELLED", "Worktree preparation was cancelled. Existing folders and changes are preserved."); }
        finally { cancellation.Cancel(); await watching; }
    }

    private static async Task PrepareWorktreeCoreAsync(Store store, string runId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var saved = store.ReadTask(runId);
        var config = saved["config"]!.AsObject();
        var task = saved["task"]!.AsObject();
        if (config["mainRepoFolder"] is null || config["worktreeRoot"] is null) return; // Legacy execution snapshot.
        RequirePowerToys(Protocol.RequiredString(task, "repository"));
        var main = await ResolveMainRepositoryAsync(Protocol.RequiredString(config, "mainRepoFolder", 4096), token);
        if (main.Key != Protocol.RequiredString(config, "repositoryKey")) throw new ProtocolException("REPOSITORY_MISMATCH", "The main folder now points to a different Git repository.");
        var root = ValidateWorktreeRoot(Protocol.RequiredString(config, "worktreeRoot", 4096), main.Folder);
        var folder = Path.GetFullPath(Protocol.RequiredString(config, "repoFolder", 4096));
        var expectedFolder = Path.Combine(root, "pulse-" + Protocol.RunId(runId));
        var branch = "codex/pulse-" + Protocol.RunId(runId);
        if (!PathEquals(folder, expectedFolder) || config["worktreeBranch"]?.GetValue<string>() != branch)
            throw new ProtocolException("WORKTREE_PATH_INVALID", "The worktree path or branch does not match this run.");
        var metadataPath = Path.Combine(store.RunDirectory(runId), "worktree.json");
        var metadata = store.ReadJson(metadataPath);
        if (Directory.Exists(folder) || File.Exists(folder))
        {
            if (metadata?["state"]?.GetValue<string>() != "ready" || !PathEquals(metadata?["path"]?.GetValue<string>() ?? "", folder))
                throw new ProtocolException("WORKTREE_ALREADY_EXISTS", "The worktree destination already exists. Its contents were not overwritten.", "Inspect the existing folder. Start a new run from Tasks if needed.");
            var common = CanonicalDirectory((await GitAsync(folder, ["rev-parse", "--path-format=absolute", "--git-common-dir"], token)).Trim());
            var actualBranch = (await GitAsync(folder, ["symbolic-ref", "--short", "HEAD"], token)).Trim();
            if (Protocol.Hash(common.ToUpperInvariant()) != main.Key || actualBranch != branch)
                throw new ProtocolException("WORKTREE_IDENTITY_MISMATCH", "The worktree identity or branch has changed and cannot be reused.");
            return;
        }
        {
            token.ThrowIfCancellationRequested();
            var baseSha = Protocol.RequiredString(config, "worktreeBase", 64);
            ValidateSha(baseSha);
            var expected = task["expectedHeadSha"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(expected))
            {
                // Acceptance already checked GitHub metadata. Fetch this exact PR ref now and
                // compare its SHA again; the cancellable Git Job also covers remote-head changes.
                token.ThrowIfCancellationRequested();
                var number = Protocol.RequireObject(task["target"])["number"]!.GetValue<int>();
                var fetchRef = "refs/pulse/runs/" + Protocol.RunId(runId);
                store.AppendEvent(runId, "worktree.preparing", "Fetching the requested PR revision and preparing an isolated worktree.");
                await GitAsync(main.Folder, ["fetch", "--no-tags", "https://github.com/microsoft/PowerToys.git", $"refs/pull/{number}/head:{fetchRef}"], token);
                baseSha = (await GitAsync(main.Folder, ["rev-parse", "--verify", fetchRef + "^{commit}"], token)).Trim();
                if (!string.Equals(baseSha, expected, StringComparison.OrdinalIgnoreCase))
                    throw new ProtocolException("STALE_CONTEXT", "The fetched PR revision does not match the expected SHA. Analysis was not started.", "Refresh the PR context in Pulse and start a new run.");
            }
            Directory.CreateDirectory(root);
            store.WriteJson(metadataPath, WorktreeRecord("preparing", folder, branch, baseSha));
            await GitAsync(main.Folder, ["worktree", "add", "--no-track", "-b", branch, "--", folder, baseSha], token);
            store.WriteJson(metadataPath, WorktreeRecord("ready", folder, branch, baseSha));
            store.AppendEvent(runId, "worktree.ready", "The isolated worktree is ready. Its folder and changes will remain after completion or cancellation.", new JsonObject { ["branch"] = branch });
        }
    }

    private static JsonObject WorktreeRecord(string state, string path, string branch, string sha) => new()
        { ["state"] = state, ["path"] = path, ["branch"] = branch, ["baseSha"] = sha, ["createdAt"] = Protocol.Now() };
    private static async Task WatchCancellationAsync(Store store, string runId, CancellationTokenSource cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (File.Exists(Path.Combine(store.RunDirectory(runId), "cancel.json"))) { cancellation.Cancel(); return; }
                await Task.Delay(100, cancellation.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    public static async Task<(string Folder, string Key)> ResolveRepositoryAsync(string repository, string folder, CancellationToken token = default)
    {
        if (!RepoPattern().IsMatch(repository)) throw new ProtocolException("INVALID_CONFIG", "The repository identifier is invalid.");
        var details = await RepositoryDetailsAsync(folder, token);
        var remotes = (await GitAsync(details.Folder, ["remote"], token)).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var matched = false;
        foreach (var remoteName in remotes)
        {
            var remote = (await GitAsync(details.Folder, ["remote", "get-url", remoteName], token)).Trim();
            if (string.Equals(ParseGitHubRemote(remote), repository, StringComparison.OrdinalIgnoreCase)) matched = true;
        }
        if (!matched) throw new ProtocolException("REPOSITORY_MISMATCH", "The folder has no Git remote matching the PowerToys repository.", "Choose the main PowerToys checkout. Forks may keep their origin and add an upstream remote for microsoft/PowerToys.");
        return (details.Folder, Protocol.Hash(details.Common.ToUpperInvariant()));
    }
    private static async Task<(string Folder, string Key)> ResolveMainRepositoryAsync(string folder, CancellationToken token = default)
    {
        var resolved = await ResolveRepositoryAsync(PowerToysRepository, folder, token);
        var details = await RepositoryDetailsAsync(resolved.Folder, token);
        if (!PathEquals(details.GitDirectory, details.Common))
            throw new ProtocolException("MAIN_REPO_REQUIRED", "Choose the main PowerToys checkout, not an existing linked worktree.");
        return resolved;
    }
    private static async Task<(string Folder, string Common, string GitDirectory)> RepositoryDetailsAsync(string folder, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)) throw new ProtocolException("REPO_PATH_INVALID", "The main checkout folder is missing or is not an absolute path.");
        var top = CanonicalDirectory((await GitAsync(folder, ["rev-parse", "--show-toplevel"], token)).Trim());
        var common = CanonicalDirectory((await GitAsync(folder, ["rev-parse", "--path-format=absolute", "--git-common-dir"], token)).Trim());
        var gitDirectory = CanonicalDirectory((await GitAsync(folder, ["rev-parse", "--absolute-git-dir"], token)).Trim());
        return (top, common, gitDirectory);
    }
    private static string ValidateWorktreeRoot(string folder, string main)
    {
        if (!Path.IsPathFullyQualified(folder) || File.Exists(folder)) throw new ProtocolException("WORKTREE_PATH_INVALID", "The worktree root must be an absolute local folder path.");
        var resolved = CanonicalDirectory(folder);
        if (main.Length > 0 && IsWithin(resolved, CanonicalDirectory(main)))
            throw new ProtocolException("WORKTREE_PATH_INVALID", "The worktree root must be outside the main PowerToys checkout.", "For example: main checkout C:\\source\\PowerToys and worktree root C:\\source\\PowerToys-worktrees.");
        return resolved;
    }

    public static string? ParseGitHubRemote(string remote)
    {
        string path;
        if (remote.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase)) path = remote[15..];
        else if (Uri.TryCreate(remote, UriKind.Absolute, out var uri) && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) && uri.Scheme is "https" or "ssh" && uri.Query.Length == 0 && uri.Fragment.Length == 0)
            path = uri.AbsolutePath.TrimStart('/');
        else return null;
        path = path.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        return RepoPattern().IsMatch(path) ? path : null;
    }
    private static string CanonicalDirectory(string path)
    {
        var absolute = Path.GetFullPath(path);
        var root = Path.GetPathRoot(absolute)!;
        var current = root;
        foreach (var part in absolute[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Where(x => x.Length > 0))
        {
            var directory = new DirectoryInfo(Path.Combine(current, part));
            current = directory.Exists ? directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory.FullName : directory.FullName;
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }
    private static bool PathEquals(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);
    private static bool IsWithin(string path, string root) => PathEquals(path, root) || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string OptionalFolder(JsonObject config, string key)
    {
        if (config[key] is not JsonValue value || !value.TryGetValue<string>(out var path) || path.Length > 4096 || path.Contains('\0'))
            throw new ProtocolException("INVALID_CONFIG", $"{key} must contain a valid folder path.");
        return path.Trim();
    }
    public static void RequirePowerToys(string repository)
    {
        if (!string.Equals(repository, PowerToysRepository, StringComparison.OrdinalIgnoreCase))
            throw new ProtocolException("REPOSITORY_NOT_SUPPORTED", "This extension only handles microsoft/PowerToys.");
    }
    private static void ValidateSha(string sha)
    {
        if (!Regex.IsMatch(sha, "^(?:[a-fA-F0-9]{40}|[a-fA-F0-9]{64})$")) throw new ProtocolException("REPO_PATH_INVALID", "Git did not return a valid committed revision.");
    }
    private static async Task<string> GitAsync(string folder, string[] arguments, CancellationToken cancellationToken = default)
    {
        var executable = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(entry => entry.Trim().Trim('"')).Where(Path.IsPathFullyQualified)
            .Select(directory => Path.Combine(directory, "git.exe")).FirstOrDefault(File.Exists)
            ?? throw new ProtocolException("GIT_MISSING", "Git was not found on this computer.", "Install Git and ensure git.exe is available on the Host PATH.");
        try
        {
            using var process = WindowsProcess.Cli(executable, arguments, folder,
                new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0", ["GCM_INTERACTIVE"] = "never" });
            var output = ReadBoundedAsync(process.Output!); var error = ReadBoundedAsync(process.Error!);
            process.Input!.Close(); process.Resume();
            try { await process.WaitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                process.Kill(); await process.WaitAsync();
                await Task.WhenAll(output, error); throw;
            }
            process.Kill(); // Also release pipes held by helpers after Git itself exits.
            var text = await output; await error;
            if (process.ExitCode != 0) throw new ProtocolException("GIT_OPERATION_FAILED", "Git could not inspect the repository or prepare the worktree.", "Check the main folder, commit, upstream, permissions, and network. Existing folders and changes are preserved; global Git settings are not modified.");
            return text;
        }
        catch (System.ComponentModel.Win32Exception) { throw new ProtocolException("GIT_MISSING", "Git was not found on this computer.", "Install Git and ensure browser-launched Host processes can find git.exe on PATH."); }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var buffer = new char[4096]; var output = new StringBuilder(); int count;
        while ((count = await reader.ReadAsync(buffer)) != 0)
            if (output.Length < 16384) output.Append(buffer, 0, Math.Min(count, 16384 - output.Length));
        return output.ToString();
    }
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,38}$", RegexOptions.CultureInvariant)]
    private static partial Regex AccountPattern();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,38}/[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex RepoPattern();
}
