using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Pulse.Host.Tests;

internal static class CoreScenarios
{
    public static async Task RunAllAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "Pulse tests 中文 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await FramingAsync();
            Validation();
            StoreRecovery(root);
            await ConfigurationAsync(root);
            await DispatcherAsync(root);
        }
        finally
        {
            // Git marks loose objects read-only on Windows. This directory was created by this
            // scenario under GetTempPath and contains only its isolated fixture repositories.
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task FramingAsync()
    {
        var request = new JsonObject { ["id"] = "测试\"\\", ["protocolVersion"] = 1, ["type"] = "hello", ["payload"] = new JsonObject() };
        using var wire = new MemoryStream();
        await NativeFraming.WriteAsync(wire, request);
        using var fragmented = new FragmentedStream(wire.ToArray());
        var decoded = await NativeFraming.ReadAsync(fragmented);
        Check(JsonNode.DeepEquals(request, decoded), "Fragmented Unicode native frame round trip");
        Check(await NativeFraming.ReadAsync(fragmented) is null, "EOF is connection close");
        await ExpectAsync("INVALID_FRAME", () => NativeFraming.ReadAsync(new MemoryStream([4, 0])).AsTaskAdapter());
        await ExpectAsync("INVALID_FRAME", () => NativeFraming.ReadAsync(new MemoryStream([4, 0, 0, 0, 123])).AsTaskAdapter());
        var oversized = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(oversized, Protocol.MaxInputBytes + 1u);
        await ExpectAsync("INPUT_TOO_LARGE", () => NativeFraming.ReadAsync(new MemoryStream(oversized)).AsTaskAdapter());
        using var output = new MemoryStream();
        await NativeFraming.WriteAsync(output, new JsonObject { ["id"] = "large", ["data"] = new string('a', Protocol.MaxOutputBytes + 1) });
        output.Position = 0;
        Check((await NativeFraming.ReadAsync(output))?["error"]?["code"]?.GetValue<string>() == "OUTPUT_TOO_LARGE", "Oversized output gives bounded actionable error");
    }

    private static void Validation()
    {
        var valid = Task();
        Check(Protocol.ValidateTask(valid)["repository"]!.GetValue<string>() == "microsoft/powertoys", "PowerToys repository normalized");
        var unsupported = (JsonObject)valid.DeepClone(); unsupported["repository"] = "owner/another";
        Expect("REPOSITORY_NOT_SUPPORTED", () => Protocol.ValidateTask(unsupported));
        foreach (var forbidden in new[] { "repoFolder", "agent", "cliPath", "permission", "command", "arguments" })
        {
            var invalid = (JsonObject)valid.DeepClone(); invalid[forbidden] = "override";
            Expect("INVALID_REQUEST", () => Protocol.ValidateTask(invalid));
        }
        var review = Task(); review["actionKind"] = "pr-review"; review["target"] = new JsonObject { ["type"] = "pr", ["number"] = 42 };
        Expect("STALE_CONTEXT", () => Protocol.ValidateTask(review));
        review["expectedHeadSha"] = new string('a', 40);
        Protocol.ValidateTask(review);
        review["expectedHeadSha"] = "short";
        Expect("INVALID_REQUEST", () => Protocol.ValidateTask(review));
        foreach (var (kind, wrongType) in new[] { ("e2e", "issue"), ("reproduction-setup", "pr"), ("issue-fix", "pr") })
        {
            var mismatch = Task();
            mismatch["actionKind"] = kind;
            mismatch["target"] = new JsonObject { ["type"] = wrongType, ["number"] = 42 };
            Expect("INVALID_REQUEST", () => Protocol.ValidateTask(mismatch));
        }
        foreach (var kind in Dispatcher.PublicWorkflowKinds)
        {
            var missing = Task(); missing["actionKind"] = kind; missing.Remove("target");
            Expect("INVALID_REQUEST", () => Protocol.ValidateNewTask(missing));
            if (kind is "issue-fix" or "reproduction-setup" or "e2e")
            {
                var frozen = missing.ToJsonString();
                Check(Protocol.ValidateTask(missing)["target"] is null && missing.ToJsonString() == frozen,
                    "Historical request normalization must preserve absent targets without allowing new admission.");
            }
            var type = kind is "pr-review" or "e2e" ? "pr" : "issue";
            var linked = (JsonObject)missing.DeepClone(); linked["target"] = new JsonObject { ["type"] = type, ["number"] = 42 };
            if (type == "pr")
            {
                Expect("STALE_CONTEXT", () => Protocol.ValidateNewTask(linked));
                linked["expectedHeadSha"] = new string('A', 40);
            }
            Check(Protocol.ValidateNewTask(linked)["target"]!["type"]!.GetValue<string>() == type, "New tasks require their matching Issue or PR target.");
            linked["target"]!["type"] = type == "pr" ? "issue" : "pr";
            Expect("INVALID_REQUEST", () => Protocol.ValidateNewTask(linked));
        }
        Expect("INVALID_REQUEST", () => Protocol.RunId("../../outside"));
        var reordered = (JsonObject)JsonNode.Parse("{\"b\":2,\"a\":1}")!;
        Check(Protocol.Fingerprint(reordered) == Protocol.Fingerprint(JsonNode.Parse("{\"a\":1,\"b\":2}")!), "Fingerprint is property-order independent");
        Check(Configuration.ParseGitHubRemote("git@github.com:Owner/Repo.git") == "Owner/Repo", "SSH origin parsing");
        Check(Configuration.ParseGitHubRemote("https://github.com/Owner/Repo.git") == "Owner/Repo", "HTTPS origin parsing");
        Check(Configuration.ParseGitHubRemote("https://github.com.evil.invalid/Owner/Repo.git") is null, "Lookalike GitHub host rejected");
        Check(Configuration.ParseGitHubRemote("https://github.com/Owner/Repo?token=secret") is null, "Remote query rejected");
    }

    private static void StoreRecovery(string root)
    {
        var store = new Store(Path.Combine(root, "data"));
        var id = Guid.NewGuid().ToString("D");
        var task = Protocol.ValidateTask(Task());
        var config = new JsonObject { ["agent"] = "codex", ["repoFolder"] = root, ["repositoryKey"] = "test", ["permission"] = "read-only" };
        store.CreateRun(id, task, config, Protocol.ProductionOrigin);
        config["permission"] = "workspace-write";
        Check(store.ReadTask(id)["config"]!["permission"]!.GetValue<string>() == "read-only", "Accepted configuration is an immutable snapshot");
        Check(store.FindRequest(task, Protocol.ProductionOrigin) == id, "Request replay returns original run");
        var conflict = (JsonObject)task.DeepClone(); conflict["prompt"] = "different";
        Expect("REQUEST_CONFLICT", () => store.FindRequest(conflict, Protocol.ProductionOrigin));
        File.Delete(Directory.GetFiles(Path.Combine(store.Root, "requests"), "*.json").Single());
        Check(store.FindRequest(task, Protocol.ProductionOrigin) == id, "Dedupe index rebuilds from task files");
        using (store.AcquireLock("repo-test")) Expect("RESOURCE_BUSY", () => store.AcquireLock("repo-test", TimeSpan.Zero));
        using (store.AcquireLock("repo-test", TimeSpan.Zero)) { }
        var sequence = store.AppendEvent(id, "progress", "token ghp_abcdefghijklmno and 中文");
        var eventsPath = Path.Combine(store.RunDirectory(id), "events.jsonl");
        File.AppendAllText(eventsPath, "{\"sequence\":999,\"text\":\"incomplete", Encoding.UTF8);
        var events = store.Events(id, 0, 100);
        Check(events["nextSequence"]!.GetValue<long>() == sequence, "Partial JSONL tail does not advance cursor");
        Check(!events.ToJsonString().Contains("ghp_abcdefghijklmno"), "Known credential patterns are redacted");
        store.AppendEvent(id, "progress", "Recovery after an incomplete append");
        Check(store.Events(id, sequence, 100)["events"]!.AsArray().Count == 1, "Interrupted append is repaired before new events");
        var before = store.ReadStatus(id);
        store.Complete(id, "succeeded", 0, new JsonObject { ["summary"] = "A concrete result", ["nextSteps"] = new JsonArray() });
        store.WriteJson(Path.Combine(store.RunDirectory(id), "status.json"), before);
        store.RecoverCompletion(id);
        Check(store.ReadStatus(id)["state"]!.GetValue<string>() == "succeeded", "Terminal journal repairs result/status crash window");
        store.Complete(id, "interrupted", null, new JsonObject { ["summary"] = "wrong" });
        Check(store.ReadResult(id)!["summary"]!.GetValue<string>() == "A concrete result", "Terminal result is never overwritten during recovery");
        store.SetView(id, "read", true);
        Check(store.ReadView(id)["handled"]!.GetValue<bool>() == false, "Read and handled are distinct");
        Expect("TASK_NOT_DISPOSABLE", () => store.DeleteRun(id));
        store.SetView(id, "handled", true);
        store.DeleteRun(id);
        Check(!Directory.Exists(store.RunDirectory(id)), "Only handled terminal records can be removed");
        Expect("TASK_REMOVED", () => store.FindRequest(task, Protocol.ProductionOrigin));

        var failedId = Guid.NewGuid().ToString("D");
        var failedTask = Protocol.ValidateTask(Task());
        store.CreateRun(failedId, failedTask, config, Protocol.ProductionOrigin);
        store.Complete(failedId, "failed", null, WorkflowResult.Failure("failed", "AUTH_REQUIRED", "Sign in to the selected CLI.", failedTask));
        var compact = store.Detail(failedId, summaryOnly: true)["result"]!.AsObject();
        Check(compact["schemaVersion"]!.GetValue<int>() == 2 && compact["outcome"]!.GetValue<string>() == "failed" && compact["phase"]!.GetValue<string>() == "setup", "Task cards retain v2 failure outcome and phase independently of lifecycle state.");
        Check(!compact.ContainsKey("diagnostics") && !compact.ContainsKey("findings") && !compact.ContainsKey("rawOutput"), "List summaries do not expose full private result payloads.");
        Check(store.Events(failedId, 0, 100)["events"]!.AsArray().Last()!["type"]!.GetValue<string>() == "workflow.failed", "New terminal events name the workflow outcome.");
    }

    private static async Task ConfigurationAsync(string root)
    {
        var repo = Path.Combine(root, "repo with 空格"); Directory.CreateDirectory(repo);
        await Git(repo, "init", "--quiet");
        await Git(repo, "remote", "add", "origin", "https://github.com/Owner/Repo.git");
        await Git(repo, "-c", "user.name=Pulse Tests", "-c", "user.email=pulse-tests@example.invalid", "-c", "commit.gpgsign=false", "commit", "--allow-empty", "-m", "fixture", "--quiet");
        var first = await Configuration.ResolveRepositoryAsync("owner/repo", repo);
        var worktree = Path.Combine(root, "linked worktree");
        await Git(repo, "worktree", "add", "--detach", worktree);
        var second = await Configuration.ResolveRepositoryAsync("owner/repo", worktree);
        Check(first.Key == second.Key, "Linked worktrees share repository execution identity");
        await ExpectAsync("REPOSITORY_MISMATCH", async () => { await Configuration.ResolveRepositoryAsync("owner/other", repo); });
        await Git(repo, "remote", "add", "upstream", "https://github.com/microsoft/PowerToys.git");
        var store = new Store(Path.Combine(root, "settings"));
        var configuration = new Configuration(store, () => System.Threading.Tasks.Task.FromResult(new JsonObject { ["available"] = true, ["accounts"] = new JsonArray(new JsonObject { ["login"] = "user_enterprise", ["state"] = "success", ["active"] = false }) }));
        var config = configuration.Read();
        Check(config["permission"]!.GetValue<string>() == "yolo", "New settings default to YOLO");
        config["prPrompt"] = ""; config["issuePrompt"] = ""; config["e2ePrompt"] = "";
        config["mainRepoFolder"] = repo; config["worktreeRoot"] = Path.Combine(root, "PowerToys worktrees");
        config["githubAccount"] = "user_enterprise";
        await configuration.SaveAsync(config);
        Check(configuration.Read()["mainRepoFolder"]!.GetValue<string>() == repo, "Main PowerToys checkout accepts an upstream remote alongside a fork");
        Check(!configuration.Read().ContainsKey("cliPaths") && !configuration.Read().ContainsKey("timeoutSeconds") && !configuration.Read().ContainsKey("repositories"), "Settings have no manual CLI path, deadline or repository map");
        foreach (var permission in new[] { "read-only", "workspace-write", "yolo" })
        {
            config["permission"] = permission; await configuration.SaveAsync(config);
            Check(configuration.Read()["permission"]!.GetValue<string>() == permission, "Explicit permission choice persists: " + permission);
        }
        config["githubAccount"] = "unknown_user";
        await ExpectAsync("GITHUB_ACCOUNT_UNAVAILABLE", async () => { await configuration.SaveAsync(config); });
        config["githubAccount"] = "";
        config["mainRepoFolder"] = worktree;
        await ExpectAsync("MAIN_REPO_REQUIRED", async () => { await configuration.SaveAsync(config); });
        config["mainRepoFolder"] = repo; config["worktreeRoot"] = Path.Combine(repo, "nested");
        await ExpectAsync("WORKTREE_PATH_INVALID", async () => { await configuration.SaveAsync(config); });
        config["worktreeRoot"] = Path.Combine(root, "PowerToys worktrees");
        config["permission"] = "unrestricted";
        await ExpectAsync("INVALID_CONFIG", async () => { await configuration.SaveAsync(config); });
        await WorktreeIsolationAsync(store, repo, config["worktreeRoot"]!.GetValue<string>(), first.Key);

        var legacyStore = new Store(Path.Combine(root, "legacy-settings"));
        legacyStore.WriteJson(Path.Combine(legacyStore.Root, "config.json"), new JsonObject
        {
            ["agent"] = "copilot", ["timeoutSeconds"] = 30, ["cliPaths"] = new JsonObject { ["codex"] = "obsolete.exe" },
            ["repositories"] = new JsonArray(new JsonObject { ["repository"] = "microsoft/PowerToys", ["repoFolder"] = repo }), ["permission"] = "workspace-write"
        });
        var migrated = new Configuration(legacyStore).Read();
        Check(migrated["mainRepoFolder"]!.GetValue<string>() == repo && migrated["agent"]!.GetValue<string>() == "copilot" && !migrated.ContainsKey("timeoutSeconds"), "Legacy settings migrate without retaining execution deadlines");
        Check(migrated["permission"]!.GetValue<string>() == "workspace-write", "A new default preserves the permission saved in existing settings");
    }

    private static async Task WorktreeIsolationAsync(Store store, string repo, string worktreeRoot, string key)
    {
        await File.WriteAllTextAsync(Path.Combine(repo, "sample.txt"), "committed content");
        await Git(repo, "add", "sample.txt");
        await Git(repo, "-c", "user.name=Pulse Tests", "-c", "user.email=pulse-tests@example.invalid", "-c", "commit.gpgsign=false", "commit", "-m", "tracked fixture", "--quiet");
        var head = (await Git(repo, "rev-parse", "HEAD")).Trim();
        await File.WriteAllTextAsync(Path.Combine(repo, "sample.txt"), "main local changes");
        var before = await Git(repo, "status", "--porcelain");
        (string Id, string Folder) Create()
        {
            var id = Guid.NewGuid().ToString("D"); var folder = Path.Combine(worktreeRoot, "pulse-" + id);
            store.CreateRun(id, Protocol.ValidateTask(Task()), new JsonObject
            {
                ["agent"] = "codex", ["permission"] = "workspace-write", ["mainRepoFolder"] = repo, ["worktreeRoot"] = worktreeRoot,
                ["repoFolder"] = folder, ["repositoryKey"] = key, ["worktreeBase"] = head, ["worktreeBranch"] = "codex/pulse-" + id, ["githubAccount"] = ""
            }, Protocol.ProductionOrigin);
            return (id, folder);
        }
        var first = Create();
        await Configuration.PrepareWorktreeAsync(store, first.Id);
        Check(await File.ReadAllTextAsync(Path.Combine(first.Folder, "sample.txt")) == "committed content", "Each worktree starts at the accepted commit and excludes dirty main changes");
        Check(await Git(repo, "status", "--porcelain") == before && (await Git(repo, "rev-parse", "HEAD")).Trim() == head, "Worktree creation leaves the main branch and local changes intact");
        await Configuration.PrepareWorktreeAsync(store, first.Id);
        Check((await Git(first.Folder, "branch", "--show-current")).Trim() == "codex/pulse-" + first.Id, "Prepared worktree identity can be read without creating a duplicate branch");
        await File.WriteAllTextAsync(Path.Combine(first.Folder, "sample.txt"), "task changes");
        store.Complete(first.Id, "succeeded", 0, new JsonObject { ["summary"] = "fixture" });
        store.SetView(first.Id, "read", true); store.SetView(first.Id, "handled", true); store.DeleteRun(first.Id);
        Check(await File.ReadAllTextAsync(Path.Combine(first.Folder, "sample.txt")) == "task changes", "Deleting task history retains the worktree and its code changes");
        var occupied = Create(); Directory.CreateDirectory(occupied.Folder); await File.WriteAllTextAsync(Path.Combine(occupied.Folder, "keep.txt"), "keep");
        await ExpectAsync("WORKTREE_ALREADY_EXISTS", () => Configuration.PrepareWorktreeAsync(store, occupied.Id));
        Check(File.Exists(Path.Combine(occupied.Folder, "keep.txt")), "Pre-existing worktree destination is never overwritten");
        var cancelled = Create(); store.WriteJson(Path.Combine(store.RunDirectory(cancelled.Id), "cancel.json"), new JsonObject());
        await ExpectAsync("CANCELLED", () => Configuration.PrepareWorktreeAsync(store, cancelled.Id));
    }

    private static async Task DispatcherAsync(string root)
    {
        var dispatcher = new Dispatcher(new Store(Path.Combine(root, "dispatch")));
        var invalidVersion = await dispatcher.HandleAsync(new JsonObject { ["id"] = "1", ["protocolVersion"] = 99, ["type"] = "config.get" });
        Check(invalidVersion["error"]!["code"]!.GetValue<string>() == "PROTOCOL_MISMATCH", "Protocol mismatch is explicit");
        var invalidOrigin = await dispatcher.HandleAsync(new JsonObject { ["id"] = "2", ["protocolVersion"] = 1, ["type"] = "tasks.submit", ["payload"] = new JsonObject { ["task"] = Task(), ["sourceOrigin"] = "https://evil.invalid" } });
        Check(invalidOrigin["error"]!["code"]!.GetValue<string>() == "ORIGIN_NOT_ALLOWED", "Host enforces source origin independently");
        var invalidOverride = Task(); invalidOverride["command"] = "whoami";
        var rejected = await dispatcher.HandleAsync(new JsonObject { ["id"] = "3", ["protocolVersion"] = 1, ["type"] = "tasks.submit", ["payload"] = new JsonObject { ["task"] = invalidOverride, ["sourceOrigin"] = Protocol.ProductionOrigin } });
        Check(rejected["error"]!["code"]!.GetValue<string>() == "INVALID_REQUEST", "Host rejects execution overrides before configuration or process launch");
        await TaskViewsAsync(root);
        await FinishedTaskViewsAsync(root);
    }

    private static async Task TaskViewsAsync(string root)
    {
        var store = new Store(Path.Combine(root, "task-views"));
        var dispatcher = new Dispatcher(store);
        var states = new[] { "accepted", "running", "succeeded", "failed", "cancelled", "interrupted" };
        foreach (var type in new[] { "pr", "issue" })
        foreach (var state in states)
        {
            var id = Guid.NewGuid().ToString("D"); var task = Task();
            task["requestId"] = id; task["target"] = new JsonObject { ["type"] = type, ["number"] = 42 };
            store.CreateRun(id, task, new JsonObject { ["repositoryKey"] = "views", ["agent"] = "codex", ["repoFolder"] = root }, Protocol.ProductionOrigin);
            if (Protocol.IsActive(state))
            {
                var status = store.ReadStatus(id); status["state"] = state;
                // A deliberately unverifiable identity keeps this list-only fixture active without
                // allowing lifecycle reconciliation to terminate any real process on a slow test run.
                status["worker"] = new JsonObject { ["pid"] = 0, ["startTimeUtc"] = "list-fixture" };
                store.WriteJson(Path.Combine(store.RunDirectory(id), "status.json"), status);
            }
            else store.Complete(id, state, 0, new JsonObject { ["summary"] = "fixture" });
        }
        foreach (var view in new[] { "tasks", "prs", "issues" })
        {
            var items = new List<JsonObject>(); string? cursor = null;
            do
            {
                var result = await dispatcher.HandleAsync(new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["protocolVersion"] = 1, ["type"] = "tasks.list", ["payload"] = new JsonObject { ["view"] = view, ["limit"] = 2, ["cursor"] = cursor } });
                if (!result["ok"]!.GetValue<bool>()) throw new Exception("Task view query failed: " + result.ToJsonString());
                var data = result["data"]!.AsObject(); items.AddRange(data["runs"]!.AsArray().OfType<JsonObject>());
                Check(data["runningCount"]!.GetValue<int>() == 4 && data["prCount"]!.GetValue<int>() == 4 && data["issueCount"]!.GetValue<int>() == 4, "View counts span the full catalog");
                cursor = data["nextCursor"]?.GetValue<string>();
            } while (cursor is not null);
            Check(items.Count == 4 && items.All(item => view == "tasks" ? Protocol.IsActive(item["status"]!["state"]!.GetValue<string>()) :
                !Protocol.IsActive(item["status"]!["state"]!.GetValue<string>()) && item["task"]!["target"]!["type"]!.GetValue<string>() == (view == "prs" ? "pr" : "issue")), "Target and completion filters apply before pagination: " + view);
        }
    }

    private static async Task FinishedTaskViewsAsync(string root)
    {
        var store = new Store(Path.Combine(root, "finished-task-views"));
        var dispatcher = new Dispatcher(store);
        string Create(string created, string? ended, string updated)
        {
            var id = Guid.NewGuid().ToString("D");
            var task = Task(); task["requestId"] = id;
            store.CreateRun(id, task, new JsonObject { ["repositoryKey"] = "finished-views", ["agent"] = "codex", ["repoFolder"] = root }, Protocol.ProductionOrigin);
            store.Complete(id, "failed", 1, new JsonObject { ["summary"] = "fixture failure" });
            var status = store.ReadStatus(id);
            status["createdAt"] = created; status["updatedAt"] = updated;
            if (ended is null) status.Remove("endedAt"); else status["endedAt"] = ended;
            store.WriteJson(Path.Combine(store.RunDirectory(id), "status.json"), status);
            // These synthetic dates represent historical records without an authoritative
            // completion journal; journal replay is covered by CompletionScenarios.
            File.Delete(Path.Combine(store.RunDirectory(id), Store.CompletionJournalFile));
            File.Delete(Path.Combine(store.RunDirectory(id), "completion.json"));
            return id;
        }
        var longTask = Create("2026-09-01T00:00:00Z", "2026-09-10T12:00:00Z", "2026-09-10T12:00:00Z");
        var newTask = Create("2026-09-09T00:00:00Z", "2026-09-10T10:00:00Z", "2026-09-10T10:00:00Z");
        var legacyTask = Create("2026-09-08T00:00:00Z", null, "2026-09-10T11:00:00Z");
        async System.Threading.Tasks.Task<JsonObject> Query(JsonObject payload)
            => await dispatcher.HandleAsync(new JsonObject { ["id"] = Guid.NewGuid().ToString("D"), ["protocolVersion"] = 1, ["type"] = "tasks.list", ["payload"] = payload });
        var first = await Query(new JsonObject { ["view"] = "history", ["order"] = "finished", ["limit"] = 1 });
        Check(first["data"]!["runs"]![0]!["runId"]!.GetValue<string>() == longTask, "A newly finished long-running task is visible first even when it was created before newer tasks.");
        var second = await Query(new JsonObject { ["view"] = "history", ["order"] = "finished", ["limit"] = 1, ["cursor"] = first["data"]!["nextCursor"]!.DeepClone() });
        Check(second["data"]!["runs"]![0]!["runId"]!.GetValue<string>() == legacyTask, "Finished-history pagination falls back to updatedAt for older terminal records without endedAt.");
        var defaultOrder = await Query(new JsonObject { ["view"] = "history", ["limit"] = 1 });
        Check(defaultOrder["data"]!["runs"]![0]!["runId"]!.GetValue<string>() == newTask, "Existing history callers retain creation ordering unless they request finished ordering.");
        foreach (var payload in new[] { new JsonObject { ["view"] = "history", ["order"] = "unknown" }, new JsonObject { ["view"] = "tasks", ["order"] = "finished" } })
            Check((await Query(payload))["error"]!["code"]!.GetValue<string>() == "INVALID_REQUEST", "Invalid task ordering is rejected explicitly.");
    }

    private static JsonObject Task() => new() { ["requestId"] = "request-1", ["actionId"] = "action-1", ["actionKind"] = "issue-fix", ["repository"] = "Microsoft/PowerToys", ["prompt"] = "检查 Unicode \"quotes\" and $(literal) with `ticks`." };
    internal static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
    internal static void Expect(string code, Action action)
    {
        try { action(); } catch (ProtocolException e) when (e.Code == code) { Console.WriteLine("PASS: rejects " + code); return; }
        throw new Exception("Expected " + code);
    }
    internal static async Task ExpectAsync(string code, Func<Task> action)
    {
        try { await action(); } catch (ProtocolException e) when (e.Code == code) { Console.WriteLine("PASS: rejects " + code); return; }
        throw new Exception("Expected " + code);
    }
    private static async Task<string> Git(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); var text = await stdout;
        if (process.ExitCode != 0) throw new Exception(await stderr);
        await stderr;
        return text;
    }
    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, 2)], cancellationToken);
    }
}

internal static class TaskExtensions
{
    public static async Task AsTaskAdapter<T>(this Task<T> task) { await task; }
}
