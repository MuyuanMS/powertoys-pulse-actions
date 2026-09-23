using System.Text.Json.Nodes;

namespace Pulse.Host;

public sealed class Dispatcher(Store store, bool developmentOrigins = false)
{
    internal static readonly string[] PublicWorkflowKinds = ["pr-review", "issue-fix", "reproduction-setup", "e2e", "feature-research", "bug-investigation"];
    private readonly Configuration configuration = new(store);
    private readonly GitHubService github = new(store);
    private readonly PromptCatalog prompts = new(store);
    private readonly ActionReadiness readiness = new(store);
    private readonly WebActions webActions = new(store);
    private readonly ResultActions resultActions = new(store);

    public async Task<JsonObject> HandleAsync(JsonObject request)
    {
        var id = request["id"]?.DeepClone();
        try
        {
            Protocol.OnlyKeys(request, "id", "protocolVersion", "type", "payload");
            Protocol.RequiredString(request, "id", 128);
            if (request["protocolVersion"] is not JsonValue version || !version.TryGetValue<int>(out var v) || v != Protocol.Version)
                throw new ProtocolException("PROTOCOL_MISMATCH", "The extension and Host protocol versions are incompatible.", "Use matching extension and Host versions.");
            var type = Protocol.RequiredString(request, "type");
            var payload = request["payload"] is null ? new JsonObject() : Protocol.RequireObject(request["payload"]);
            JsonNode data = type switch
            {
                "hello" => await HelloAsync(),
                "config.get" => configuration.Read(),
                "config.save" => await configuration.SaveAsync(payload),
                "github.accounts" => await GitHubSession.GetAccountsAsync(),
                "prompts.list" => prompts.List(),
                "prompts.get" => GetPrompt(payload),
                "prompts.sync" => await SyncPromptsAsync(payload),
                "actions.check" => await readiness.CheckAsync(payload),
                "agents.defaults" => GetAgentDefaults(payload),
                "agents.list" => await ListAgentsAsync(payload),
                "targets.get" => await github.GetTargetAsync(payload, configuration.Read()),
                "webActions.prepare" => PrepareWebAction(payload),
                "webActions.get" => GetWebAction(payload),
                "webActions.preview" => await webActions.PreviewAsync(payload),
                "webActions.submit" => await webActions.SubmitAsync(payload),
                "webActions.cancel" => webActions.Cancel(payload),
                "webActions.reconcile" => await webActions.ReconcileAsync(payload),
                "resultActions.prepare" => resultActions.Prepare(payload),
                "resultActions.list" => resultActions.List(payload),
                "agents.test.start" => await StartAgentTestAsync(payload),
                "agents.test.get" => RuntimeService.GetAgentTest(store, RequiredTestId(payload)),
                "agents.test.cancel" => RuntimeService.CancelAgentTest(store, RequiredTestId(payload)),
                "tasks.submit" => await SubmitAsync(payload),
                "tasks.lookup" => Lookup(payload),
                "tasks.list" => List(payload),
                "tasks.get" => Get(RequiredRunId(payload)),
                "tasks.resultPage" => ResultPages.Read(store, payload),
                "tasks.events" => store.Events(RequiredRunId(payload), Number(payload, "afterSequence", 0, 0, long.MaxValue), (int)Number(payload, "limit", 50, 1, 100)),
                "tasks.logs" => ReadLogs(payload),
                "tasks.cancel" => Cancel(RequiredRunId(payload)),
                "tasks.read" => store.SetView(RequiredRunId(payload), "read", payload["value"]?.GetValue<bool>() ?? true),
                "tasks.handle" => store.SetView(RequiredRunId(payload), "handled", payload["value"]?.GetValue<bool>() ?? true),
                "tasks.rerun" => await RerunAsync(payload),
                "tasks.startFromResult" => await SubmitAsync(ResultTaskActions.Prepare(store, payload), allowInternalReviewVerification: true),
                "reviews.verify" => await SubmitAsync(ReviewFollowUps.Prepare(store, payload), allowInternalReviewVerification: true),
                "reviews.related" => ReviewFollowUps.Related(store, payload),
                "tasks.delete" => Delete(RequiredRunId(payload)),
                "operations.preview" => await github.PreviewAsync(RequiredRunId(payload)),
                "operations.submit" => await github.SubmitAsync(payload),
                "operations.list" => github.List(RequiredRunId(payload)),
                "operations.reconcile" => await github.ReconcileAsync(payload),
                _ => throw new ProtocolException("UNSUPPORTED_OPERATION", "This message type is not supported.")
            };
            return new JsonObject { ["id"] = id, ["protocolVersion"] = Protocol.Version, ["ok"] = true, ["data"] = data };
        }
        catch (Exception e)
        {
            var error = e switch
            {
                ProtocolException known => known,
                IOException or UnauthorizedAccessException => new ProtocolException("RECORD_UNREADABLE", "Local files could not be read or saved.", "Check data-folder permissions, repository permissions, and disk space, then reconnect."),
                InvalidOperationException or FormatException or System.Text.Json.JsonException or ArgumentException => new ProtocolException("INVALID_REQUEST", "A request field has an invalid type or format."),
                _ => new ProtocolException("HOST_ERROR", "The Host could not complete this request.", "Preserve local task records and reconnect. Do not resend an operation with an unknown outcome.")
            };
            return new JsonObject { ["id"] = id, ["protocolVersion"] = Protocol.Version, ["ok"] = false, ["error"] = error.ToJson() };
        }
    }

    private async Task<JsonObject> HelloAsync()
    {
        var config = configuration.Read();
        var codex = CliSelections.ProbeSelectedAsync(config, "codex");
        var copilot = CliSelections.ProbeSelectedAsync(config, "copilot");
        var identity = github.GetIdentityAsync(config);
        await Task.WhenAll(codex, copilot, identity);
        return new JsonObject
        {
            ["hostVersion"] = "0.2.0", ["protocolVersion"] = Protocol.Version,
            ["reviewModes"] = new JsonArray(ReviewModes.Values.Select(mode => (JsonNode?)JsonValue.Create(mode)).ToArray()),
            ["workflowKinds"] = new JsonArray(PublicWorkflowKinds.Select(kind => (JsonNode?)JsonValue.Create(kind)).ToArray()),
            ["resultSchemaVersions"] = new JsonArray(2, 3),
            ["agents"] = new JsonObject { ["codex"] = await codex, ["copilot"] = await copilot }, ["github"] = await identity,
            ["limits"] = new JsonObject { ["messageBytes"] = Protocol.MaxInputBytes, ["logBytes"] = Store.MaxLogBytes, ["maxConcurrentPerRepository"] = 1 },
            ["dataDirectory"] = store.Root, ["developmentOrigins"] = developmentOrigins
        };
    }

    private async Task<JsonObject> SubmitAsync(JsonObject payload, bool allowInternalReviewVerification = false)
    {
        Protocol.OnlyKeys(payload, "task", "sourceOrigin");
        var task = Protocol.ValidateTask(Protocol.RequireObject(payload["task"]), allowFollowUp: allowInternalReviewVerification);
        var source = RequireSourceOrigin(payload);
        using var held = store.AcquireLock("accept", TimeSpan.FromSeconds(30));
        store.EnsureAvailable();
        var existing = store.FindRequest(task, source);
        if (existing is not null) return Get(existing);
        // Preserve exact replay, origin isolation, conflicts and removed-request
        // tombstones before applying today's admission rules to a new request.
        task = Protocol.ValidateNewTask(task, allowFollowUp: allowInternalReviewVerification);
        var id = Guid.NewGuid().ToString("D");
        var snapshot = await configuration.SnapshotAsync(task["repository"]!.GetValue<string>(), id, task["execution"]);
        if (task["expectedHeadSha"] is not null) snapshot["worktreeBase"] = task["expectedHeadSha"]!.DeepClone();
        else if (task["planSource"]?["revisionSha"] is JsonValue sourceRevision) snapshot["worktreeBase"] = sourceRevision.DeepClone();
        var promptTemplate = TaskPrompt.Snapshot(prompts, task, snapshot);
        foreach (var runId in store.RunIds())
        {
            var saved = store.ReadTask(runId);
            if (saved["config"]?["repositoryKey"]?.GetValue<string>() != snapshot["repositoryKey"]?.GetValue<string>()) continue;
            Reconcile(runId);
            if (Protocol.IsActive(store.ReadStatus(runId)["state"]?.GetValue<string>()))
                throw new ProtocolException("REPOSITORY_BUSY", "This Git repository already has an active run, possibly in another worktree or browser.", "Wait for that run to finish or cancel it from Tasks. Runs are not queued.");
        }
        // Validate the remote revision before accepting or letting a CLI act on stale PR context.
        await github.VerifyTaskContextAsync(task, snapshot);
        store.CreateRun(id, task, snapshot, source, promptTemplate);
        try { RuntimeService.StartWorker(store, id); }
        catch (Exception e)
        {
            var error = e as ProtocolException ?? new ProtocolException("WORKER_START_FAILED", "The background worker could not start.", "Check the Host installation and system process policy. Preserve this failed run and retry explicitly.");
            using var runLock = store.AcquireLock("run-" + id);
            store.Complete(id, "failed", null, FailureResult(error, task, promptTemplate?["schemaVersion"]?.GetValue<int>()), error.ToJson());
        }
        return store.Detail(id);
    }

    private async Task<JsonObject> RerunAsync(JsonObject payload)
    {
        var submission = TaskRerun.Prepare(store, payload);
        return await SubmitAsync(submission, allowInternalReviewVerification: submission["task"]?["actionKind"]?.GetValue<string>() == "pr-verify" || submission["task"]?["planSource"] is JsonObject);
    }

    private JsonObject List(JsonObject payload)
    {
        var limit = (int)Number(payload, "limit", 20, 1, 50);
        var cursor = payload["cursor"]?.GetValue<string>();
        var view = payload["view"]?.GetValue<string>();
        var order = payload["order"]?.GetValue<string>();
        if (view is not (null or "tasks" or "prs" or "issues" or "running" or "pending" or "history")) throw new ProtocolException("INVALID_REQUEST", "Unknown task view.");
        if (order is not (null or "created" or "finished") || (order == "finished" && view != "history"))
            throw new ProtocolException("INVALID_REQUEST", "Finished ordering requires the history view; otherwise use created ordering.");
        var all = new List<JsonObject>();
        var errors = new JsonArray();
        foreach (var runId in store.RunIds())
        {
            try { Reconcile(runId); all.Add(store.Detail(runId, summaryOnly: true)); }
            catch (ProtocolException e) { errors.Add(new JsonObject { ["runId"] = runId, ["error"] = e.ToJson() }); }
        }
        var runningCount = all.Count(item => Protocol.IsActive(item["status"]?["state"]?.GetValue<string>()));
        var unreadCount = all.Count(item => !Protocol.IsActive(item["status"]?["state"]?.GetValue<string>()) && item["view"]?["read"]?.GetValue<bool>() != true);
        var prCount = all.Count(item => !Protocol.IsActive(item["status"]?["state"]?.GetValue<string>()) && item["task"]?["target"]?["type"]?.GetValue<string>() == "pr");
        var issueCount = all.Count(item => !Protocol.IsActive(item["status"]?["state"]?.GetValue<string>()) && item["task"]?["target"]?["type"]?.GetValue<string>() == "issue");
        if (view is "running" or "tasks") all = all.Where(item => Protocol.IsActive(item["status"]?["state"]?.GetValue<string>())).ToList();
        if (view is "prs" or "issues") all = all.Where(item => !Protocol.IsActive(item["status"]?["state"]?.GetValue<string>()) && item["task"]?["target"]?["type"]?.GetValue<string>() == (view == "prs" ? "pr" : "issue")).ToList();
        if (view == "pending") all = all.Where(item => !Protocol.IsActive(item["status"]?["state"]?.GetValue<string>()) && item["view"]?["handled"]?.GetValue<bool>() != true).ToList();
        if (view == "history") all = all.Where(item => !Protocol.IsActive(item["status"]?["state"]?.GetValue<string>())).ToList();
        var ordered = order == "finished" ? all.OrderByDescending(FinishedAt)
            : all.OrderByDescending(item => item["status"]?["createdAt"]?.GetValue<string>(), StringComparer.Ordinal);
        all = ordered.ThenByDescending(item => item["runId"]?.GetValue<string>(), StringComparer.Ordinal).ToList();
        var start = cursor is null ? 0 : all.FindIndex(item => item["runId"]?.GetValue<string>() == cursor) + 1;
        if (cursor is not null && start == 0) throw new ProtocolException("CURSOR_EXPIRED", "The task-list cursor has expired.", "Refresh the task list from its first page.");
        var page = all.Skip(start).Take(limit).ToArray();
        return new JsonObject
        {
            ["runs"] = new JsonArray(page.Cast<JsonNode?>().ToArray()),
            ["nextCursor"] = start + page.Length < all.Count ? page.Last()["runId"]!.DeepClone() : null,
            ["runningCount"] = runningCount,
            ["unreadCount"] = unreadCount,
            ["prCount"] = prCount, ["issueCount"] = issueCount,
            ["errors"] = errors
        };
    }

    private JsonObject Get(string runId) { Reconcile(runId); return store.Detail(runId); }
    private static DateTimeOffset FinishedAt(JsonObject item)
    {
        foreach (var field in new[] { "endedAt", "updatedAt", "createdAt" })
            if (item["status"]?[field] is JsonValue value && value.TryGetValue<string>(out var timestamp) && DateTimeOffset.TryParse(timestamp, out var parsed))
                return parsed;
        return DateTimeOffset.MinValue;
    }

    private JsonObject Lookup(JsonObject payload)
    {
        Protocol.OnlyKeys(payload, "task", "sourceOrigin");
        var task = Protocol.ValidateTask(Protocol.RequireObject(payload["task"]));
        var source = RequireSourceOrigin(payload);
        using var held = store.AcquireLock("accept", TimeSpan.FromSeconds(30));
        // A lost acknowledgement must not turn a status lookup into a first submission.
        // This read remains available during maintenance; only the rebuildable request index
        // and existing-run reconciliation may change. No admission or worker-start path runs.
        var existing = store.FindRequest(task, source);
        return new JsonObject { ["run"] = existing is null ? null : Get(existing) };
    }
    private string RequireSourceOrigin(JsonObject payload)
    {
        var source = Protocol.RequiredString(payload, "sourceOrigin", 256);
        if (source != Protocol.ProductionOrigin && !(developmentOrigins && source is (
            "http://localhost:8080" or "http://127.0.0.1:8080" or
            "http://localhost:8081" or "http://127.0.0.1:8081")))
            throw new ProtocolException("ORIGIN_NOT_ALLOWED", "The web origin is not allowed.");
        return source;
    }
    private JsonObject PrepareWebAction(JsonObject payload)
    {
        Protocol.OnlyKeys(payload, "draft", "sourceOrigin");
        var source = RequireSourceOrigin(payload);
        return webActions.Prepare(new JsonObject { ["draft"] = payload["draft"]?.DeepClone() }, source);
    }
    private JsonObject GetWebAction(JsonObject payload)
    {
        Protocol.OnlyKeys(payload, "operationId", "sourceOrigin");
        var source = RequireSourceOrigin(payload);
        return webActions.Get(new JsonObject { ["operationId"] = payload["operationId"]?.DeepClone() }, source);
    }
    private JsonObject Cancel(string runId) { Reconcile(runId); RuntimeService.RequestCancel(store, runId); return store.Detail(runId); }
    private JsonObject Delete(string runId) { Reconcile(runId); store.DeleteRun(runId); return new JsonObject { ["deleted"] = true }; }
    private void Reconcile(string runId) { store.RecoverCompletion(runId); RuntimeService.Reconcile(store, runId); }
    private static string RequiredRunId(JsonObject payload) => Protocol.RunId(Protocol.RequiredString(payload, "runId"));
    private static string RequiredTestId(JsonObject payload)
    {
        Protocol.OnlyKeys(payload, "testId");
        return Protocol.RunId(Protocol.RequiredString(payload, "testId"));
    }
    private async Task<JsonObject> StartAgentTestAsync(JsonObject payload)
    {
        Protocol.OnlyKeys(payload, "agent", "cliPath");
        var path = payload.ContainsKey("cliPath") ? Protocol.RequiredString(payload, "cliPath", 4096) : null;
        return await RuntimeService.StartAgentTestAsync(store, Protocol.RequiredString(payload, "agent"), path);
    }
    private async Task<JsonObject> ListAgentsAsync(JsonObject payload)
    {
        Protocol.OnlyKeys(payload);
        return await CliSelections.ListAsync(configuration.Read());
    }
    private JsonObject GetAgentDefaults(JsonObject payload)
    {
        Protocol.OnlyKeys(payload);
        return ExecutionOptions.PublicDefaults(configuration.Read());
    }
    private JsonObject GetPrompt(JsonObject payload)
    {
        Protocol.OnlyKeys(payload, "name");
        return prompts.Get(Protocol.RequiredString(payload, "name", 150));
    }
    private Task<JsonObject> SyncPromptsAsync(JsonObject payload)
    {
        Protocol.OnlyKeys(payload, "githubAccount");
        // Compatibility with older extension clients; bundled prompts never contact GitHub.
        if (payload["githubAccount"] is not null) Protocol.RequiredString(payload, "githubAccount", 39);
        return Task.FromResult(prompts.List());
    }
    private JsonObject ReadLogs(JsonObject payload)
    {
        Protocol.OnlyKeys(payload, "runId", "stream", "cursor", "limitBytes");
        return RunLogs.Read(store, RequiredRunId(payload), Protocol.RequiredString(payload, "stream"), Number(payload, "cursor", 0, 0, long.MaxValue), (int)Number(payload, "limitBytes", 65536, 1024, 131072));
    }

    private static long Number(JsonObject value, string field, long fallback, long min, long max)
    {
        if (value[field] is null) return fallback;
        var valid = value[field] is JsonValue number && (number.TryGetValue<long>(out _) || number.TryGetValue<int>(out _));
        var result = valid && value[field]!.AsValue().TryGetValue<long>(out var wide) ? wide : valid ? value[field]!.GetValue<int>() : 0;
        if (!valid || result < min || result > max)
            throw new ProtocolException("INVALID_REQUEST", $"{field} must be between {min} and {max}.");
        return result;
    }

    public static JsonObject FailureResult(ProtocolException error, JsonObject? task = null, int? schemaVersion = null)
        => WorkflowResult.Failure("failed", error.Code, error.Message, task, expectedSchemaVersion: schemaVersion);
}
