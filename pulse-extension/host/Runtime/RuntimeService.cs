using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pulse.Host;

public static partial class RuntimeService
{
    private static readonly HashSet<string> SupportedAgents = ["codex", "copilot"];

    public static async Task<JsonObject> ProbeAgentAsync(string agent, string? configuredPath)
    {
        if (!SupportedAgents.Contains(agent))
            return Unavailable(null, "CLI_NOT_SUPPORTED", "Only Codex CLI and GitHub Copilot CLI are supported.");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            // An accepted run owns one executable path. Never switch to another installation
            // if the user-selected executable is later removed or moved.
            var exact = Path.IsPathFullyQualified(configuredPath) ? ResolveExecutable(configuredPath) : null;
            return await ProbeAgentCandidatesAsync(agent, exact is null ? [] : [exact], ProbeCommand);
        }
        return await ProbeAgentCandidatesAsync(agent, ResolveExecutableCandidates(agent).Take(1), ProbeCommand);
    }

    internal static async Task<JsonObject> ProbeAgentCandidatesAsync(string agent, IEnumerable<string> candidates,
        Func<string, IReadOnlyList<string>, Task<string>> runProbe)
    {
        if (!SupportedAgents.Contains(agent))
            return Unavailable(null, "CLI_NOT_SUPPORTED", "Only Codex CLI and GitHub Copilot CLI are supported.");
        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Path.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) continue;
            return await ProbeAgentExecutableAsync(agent, path, runProbe);
        }
        return Unavailable(null, "CLI_NOT_FOUND",
            "No local CLI was found. Install Codex CLI or GitHub Copilot CLI and try again; the extension discovers installation paths automatically.");
    }

    private static async Task<JsonObject> ProbeAgentExecutableAsync(string agent, string path,
        Func<string, IReadOnlyList<string>, Task<string>> runProbe)
    {
        var probe = new JsonObject
        {
            ["path"] = path, ["version"] = null, ["available"] = true, ["fileExists"] = true,
            ["authentication"] = "not_probed", ["executionValidated"] = false
        };
        try
        {
            var versionOutput = await runProbe(path, ["--version"]);
            var version = Regex.Match(versionOutput, @"\b\d+\.\d+\.\d+\b").Value;
            if (version.Length > 0) probe["version"] = version;
            return probe;
        }
        catch (Exception exception) when (exception is IOException or Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            // Version is optional display metadata. A failed query does not prevent choosing
            // an existing executable or attempting the requested task with that executable.
            probe["versionProbeError"] = Error("CLI_PROBE_FAILED", "The CLI version could not be read: " + OutputRedactor.Redact(exception.Message));
            return probe;
        }
    }

    public static void StartWorker(Store store, string runId)
    {
        using var held = store.AcquireLock("run-" + runId);
        var status = store.ReadStatus(runId);
        if (!Active(status) || status["worker"] is not null) return;
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate Pulse Host executable.");
        var arguments = new List<string>();
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            arguments.Add(typeof(RuntimeService).Assembly.Location);
        arguments.AddRange(["--worker", runId, "--data-root", store.Root]);
        using var worker = WindowsProcess.DetachedWorker(executable, arguments);
        status["worker"] = Identity(worker);
        status["updatedAt"] = Now();
        store.WriteJson(Path.Combine(store.RunDirectory(runId), "status.json"), status);
        worker.Resume();
    }

    public static void RequestCancel(Store store, string runId)
    {
        using (var held = store.AcquireLock("run-" + runId))
        {
            if (!Active(store.ReadStatus(runId))) return;
            var path = Path.Combine(store.RunDirectory(runId), "cancel.json");
            if (!File.Exists(path))
            {
                store.WriteJson(path, new JsonObject { ["requestedAt"] = Now() });
                store.AppendEvent(runId, "cancel.requested", "Cancellation requested. Stopping the CLI and its child processes; existing file changes will be retained.");
            }
        }
        Reconcile(store, runId);
    }

    public static void Reconcile(Store store, string runId)
    {
        using var held = store.AcquireLock("run-" + runId);
        if (store.RecoverCompletionLocked(runId)) return;
        var status = store.ReadStatus(runId);
        if (!Active(status)) return;
        var directory = store.RunDirectory(runId);
        var workerAlive = IsSameProcess(status["worker"] as JsonObject);
        if (workerAlive is null) return; // Unverifiable: never infer a terminal state.
        var startupOverdue = CliAdapter.String(status, "state") == "accepted" &&
            DateTimeOffset.TryParse(CliAdapter.String(status, "createdAt"), out var accepted) &&
            DateTimeOffset.UtcNow - accepted > TimeSpan.FromSeconds(30);
        if (workerAlive == true && !startupOverdue) return;
        if (status["worker"] is null && DateTimeOffset.TryParse(CliAdapter.String(status, "createdAt"), out var created)
            && DateTimeOffset.UtcNow - created < TimeSpan.FromSeconds(30)) return;
        var saved = store.ReadTask(runId);
        var config = saved["config"]!.AsObject();
        IDisposable repositoryLock;
        try { repositoryLock = store.AcquireLock("repo-" + Required(config, "repositoryKey"), TimeSpan.Zero); }
        catch (ProtocolException exception) when (exception.Code == "RESOURCE_BUSY") { return; }
        using (repositoryLock)
        {
            if (workerAlive == true && !KillVerifiedProcess(status["worker"]!.AsObject())) return;
            var cli = status["cli"] as JsonObject;
            var cliAlive = IsSameProcess(cli);
            if (cliAlive is null) return;
            if (cliAlive == true && !KillVerifiedProcess(cli!)) return;
            var cancelled = File.Exists(Path.Combine(directory, "cancel.json"));
            var state = cancelled ? "cancelled" : "interrupted";
            var code = cancelled ? "CANCELLED" : startupOverdue ? "WORKER_START_INTERRUPTED" : "WORKER_INTERRUPTED";
            var message = cancelled ? "The task was cancelled. Existing file changes are retained." : "The background worker ended without a complete final status. The task was not restarted. Inspect existing artifacts and incomplete validation.";
            var task = saved["task"]!.AsObject();
            var retained = store.ReadResult(runId);
            var result = retained is null ? WorkflowResult.Failure(state, code, message, task, expectedSchemaVersion: ExpectedResultSchemaVersion(saved))
                : WorkflowResult.RecoverExisting(retained, state, code, message, task);
            store.Complete(runId, state, null, result, Error(code, message));
        }
    }

    public static async Task<int> RunWorkerAsync(Store store, string runId)
    {
        var directory = store.RunDirectory(runId);
        var saved = store.ReadTask(runId);
        var config = saved["config"]!.AsObject();
        // Starting without CREATE_SUSPENDED closes the orphaned-suspended-process crash window.
        // Until the launching host commits this exact PID/start time, this worker cannot run a CLI.
        using var current = Process.GetCurrentProcess();
        var startupDeadline = Environment.TickCount64 + 30_000;
        while (true)
        {
            var pending = store.ReadStatus(runId);
            if (!Active(pending)) return 0;
            if (pending["worker"] is JsonObject recorded)
            {
                if (recorded["pid"]?.GetValue<int>() != current.Id || IsSameProcess(recorded) != true) return 1;
                break;
            }
            if (Environment.TickCount64 >= startupDeadline) return 1;
            await Task.Delay(50);
        }
        using var repositoryLock = store.AcquireLock("repo-" + Required(config, "repositoryKey"));
        using (var held = store.AcquireLock("run-" + runId))
        {
            var status = store.ReadStatus(runId);
            if (!Active(status)) return 0;
            var recorded = status["worker"] as JsonObject;
            if (recorded is null || recorded["pid"]?.GetValue<int>() != current.Id || IsSameProcess(recorded) != true)
                throw new InvalidOperationException("WORKER_IDENTITY_MISMATCH: Only the persisted worker can execute this task.");
        }
        var agent = Required(config, "agent");
        var expectedSchemaVersion = ExpectedResultSchemaVersion(saved);
        // The immutable accepted template selects the transport as well as final validation.
        // Legacy records without a version keep v2 transport and their original final contract.
        var resultSchemaVersion = expectedSchemaVersion == 3 ? 3 : 2;
        var adapter = new CliAdapter(agent, resultSchemaVersion: expectedSchemaVersion);
        var executionMetadata = new RunExecutionMetadata(agent, DateTimeOffset.Now);
        JsonObject? lastExecutionMetadata = null;
        long lastMetadataRead = 0;
        WindowsProcess? cli = null;
        Task? stdoutTask = null, stderrTask = null, inputTask = null;
        CancellationTokenSource? draining = null;
        string state = "failed";
        string? code = null, message = null;
        int? exitCode = null;
        var cliStarted = false;
        JsonObject? candidateSnapshot = null;
        JsonObject? reviewSourceStart = null;
        var captureReviewSource = CliAdapter.String(saved["task"] as JsonObject, "actionKind")
            is "pr-review" or "pr-verify" or "feature-research" or "bug-investigation" or "feature-implement" or "issue-verify" or "issue-fix" or "reproduction-setup";
        var outputTail = new StringBuilder();
        try
        {
            if (File.Exists(Path.Combine(directory, "cancel.json"))) throw new WorkerStop("cancelled", "CANCELLED", "The task was cancelled before the CLI started.");
            if (config["mainRepoFolder"] is not null && config["worktreeRoot"] is not null)
                await Configuration.PrepareWorktreeAsync(store, runId);
            await WithCandidateCancellation(store, runId, async token =>
            {
                await CandidateSnapshots.ApplyAsync(store, runId, config, token);
                return true;
            });
            if (File.Exists(Path.Combine(directory, "cancel.json"))) throw new WorkerStop("cancelled", "CANCELLED", "The task was cancelled before the CLI started.");
            if (captureReviewSource)
            {
                reviewSourceStart = await ReviewProvenance.CaptureAsync(Required(config, "repoFolder"));
                PublishReviewProvenance(reviewSourceStart, new JsonObject { ["workingTree"] = "unknown" });
            }
            if (File.Exists(Path.Combine(directory, "cancel.json"))) throw new WorkerStop("cancelled", "CANCELLED", "The task was cancelled before the CLI started.");
            var probe = await ProbeAgentAsync(agent, Required(config, "cliPath"));
            if (probe["available"]?.GetValue<bool>() != true)
                throw new WorkerStop("failed", probe["error"]?["code"]?.GetValue<string>() ?? "CLI_NOT_FOUND",
                    probe["error"]?["message"]?.GetValue<string>() ?? "The CLI is unavailable.");
            var schemaPath = Path.Combine(directory, "result-schema.json");
            store.WriteJson(schemaPath, CliAdapter.ResultSchema(resultSchemaVersion));
            var permission = Required(config, "permission");
            var path = Required(probe, "path");
            cli = WindowsProcess.Cli(path, CliAdapter.Arguments(agent, permission, schemaPath,
                CliAdapter.String(config, "model"), CliAdapter.String(config, "reasoningEffort")), Required(config, "repoFolder"));
            using (var held = store.AcquireLock("run-" + runId))
            {
                var status = store.ReadStatus(runId);
                status["state"] = "running"; status["startedAt"] = Now(); status["updatedAt"] = Now();
                status["cli"] = Identity(cli); status["cliVersion"] = probe["version"]?.DeepClone();
                store.WriteJson(Path.Combine(directory, "status.json"), status);
                store.AppendEvent(runId, "running", "The CLI has started. Its output is being saved to the local task directory.");
            }
            cli.Resume();
            cliStarted = true;
            draining = new CancellationTokenSource();
            stdoutTask = DrainOutputAsync(store, runId, cli.Output!, "stdout.jsonl", true, adapter, outputTail, draining.Token, executionMetadata.ObserveOutput,
                task: saved["task"]!.AsObject());
            stderrTask = DrainOutputAsync(store, runId, cli.Error!, "stderr.log", false, adapter, outputTail, draining.Token,
                task: saved["task"]!.AsObject());
            inputTask = WritePrompt(cli.Input!, CliAdapter.Prompt(saved["task"]!.AsObject(), permission, agent,
                saved["promptTemplate"]?["body"]?.GetValue<string>(), resultSchemaVersion: resultSchemaVersion));
            while (!cli.HasExited)
            {
                // Await the failing pump so its stream, stage, and original exception survive.
                if (stdoutTask.IsFaulted) await stdoutTask;
                if (stderrTask.IsFaulted) await stderrTask;
                if (Environment.TickCount64 - lastMetadataRead >= 1000) PublishExecutionMetadata();
                if (File.Exists(Path.Combine(directory, "cancel.json"))) { state = "cancelled"; code = "CANCELLED"; message = "The task was cancelled. CLI child processes were stopped; existing file changes are retained."; await cli.StopAsync(); break; }
                await Task.Delay(200);
            }
            await cli.WaitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            exitCode = cli.ExitCode;
            // Terminate any lingering descendants holding inherited pipes after the CLI itself exits.
            cli.Kill();
            try { await Task.WhenAll(stdoutTask, stderrTask, inputTask).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException) { draining.Cancel(); throw new IOException("CLI output pipes did not close correctly; the result may be incomplete."); }
            PublishExecutionMetadata();
            if (code is null)
            {
                state = exitCode == 0 && adapter.Failure is null ? "succeeded" : "failed";
                if (state == "failed")
                {
                    message = adapter.Failure ?? "CLI execution failed.";
                    code = ClassifyFailure(message + "\n" + outputTail);
                    if (code == "AUTH_REQUIRED") message = "The CLI login is unavailable. Sign in to the selected CLI in a terminal, then start a new run.";
                    else if (code == "PERMISSION_DENIED") message = "The CLI needs permissions beyond the local policy or approval unavailable in this non-interactive session. Inspect diagnostics and update trusted settings before starting a new run.";
                }
            }
        }
        catch (WorkerStop stop) { state = stop.State; code = stop.Code; message = stop.Message; }
        catch (CliOutputException exception)
        {
            state = "failed"; code = "CLI_OUTPUT_FAILED"; message = exception.Message;
            try { cli?.Kill(); if (cli is not null) await cli.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
        catch (ProtocolException exception) { state = exception.Code == "CANCELLED" ? "cancelled" : "failed"; code = exception.Code; message = OutputRedactor.Redact(exception.Message); }
        catch (Exception exception)
        {
            state = "failed"; code = "CLI_EXECUTION_FAILED";
            message = OutputRedactor.Redact(exception.Message);
            try { cli?.Kill(); if (cli is not null) await cli.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
        finally
        {
            // Do not let a failed output pump race a terminal write or leave an unobserved reader.
            if (stdoutTask is not null || stderrTask is not null || inputTask is not null)
            {
                try
                {
                    var pending = new[] { stdoutTask, stderrTask, inputTask }.OfType<Task>().ToArray();
                    await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch { draining?.Cancel(); }
            }
            cli?.Dispose();
            draining?.Dispose();
        }
        PublishExecutionMetadata();
        if (cliStarted && expectedSchemaVersion == 3 && (CliAdapter.String(saved["task"] as JsonObject, "actionKind") is "issue-fix" or "feature-implement") &&
            !File.Exists(Path.Combine(directory, "cancel.json")))
        {
            try
            {
                store.AppendEvent(runId, "candidate.snapshot", "Saving the local implementation candidate for linked verification.");
                candidateSnapshot = await WithCandidateCancellation(store, runId,
                    token => CandidateSnapshots.CaptureAsync(store, runId, Required(config, "repoFolder"), token));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or ProtocolException or OperationCanceledException)
            {
                candidateSnapshot = new JsonObject { ["status"] = "unavailable", ["diagnostic"] = new JsonObject
                { ["code"] = "CANDIDATE_SNAPSHOT_UNAVAILABLE", ["message"] = OutputRedactor.Redact(exception.Message) } };
            }
        }
        if (captureReviewSource)
            PublishReviewProvenance(reviewSourceStart ?? new JsonObject { ["workingTree"] = "unknown" },
                await ReviewProvenance.CaptureAsync(Required(config, "repoFolder")));
        using (var held = store.AcquireLock("run-" + runId))
        {
            // A cancel accepted while the CLI is closing or its candidate is being retained
            // must not be overwritten by a later successful completion projection.
            if (File.Exists(Path.Combine(directory, "cancel.json")))
            { state = "cancelled"; code = "CANCELLED"; message = "The task was cancelled. Existing changes and retained candidate artifacts are preserved."; }
            var result = cliStarted ? adapter.Result(state, exitCode, code, message, saved["task"]!.AsObject(), expectedSchemaVersion)
                : WorkflowResult.Failure(state, code, message, saved["task"]!.AsObject(), exitCode, "setup", expectedSchemaVersion);
            if (candidateSnapshot is not null)
            {
                result["candidateSnapshot"] = candidateSnapshot.DeepClone();
                if (CliAdapter.String(candidateSnapshot, "status") == "unavailable")
                {
                    var diagnostic = CliAdapter.String(candidateSnapshot["diagnostic"] as JsonObject, "message") ?? "The local candidate could not be retained for linked verification.";
                    result["diagnostics"]!.AsArray().Add(new JsonObject { ["code"] = "CANDIDATE_SNAPSHOT_UNAVAILABLE", ["severity"] = "warning",
                        ["message"] = (diagnostic.Length > 2048 ? diagnostic[..2048] : diagnostic) + " Linked verification will not substitute the older source revision.", ["recovery"] = "inspectResult" });
                    result["needsReview"] = true;
                }
            }
            store.Complete(runId, state, exitCode, result,
                code is null ? null : Error(code, message ?? code));
        }
        return state == "succeeded" ? 0 : 1;

        void PublishReviewProvenance(JsonObject start, JsonObject end)
        {
            try
            {
                store.WriteJson(Path.Combine(directory, "provenance.json"), ReviewProvenance.Compose(
                    CliAdapter.String(saved["task"] as JsonObject, "expectedHeadSha"), start, end));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ProtocolException or InvalidOperationException or ArgumentException)
            {
                // Missing provenance suppresses cross-run evidence reuse; it must not erase
                // the result or turn a completed CLI run into an output-persistence failure.
            }
        }

        void PublishExecutionMetadata()
        {
            lastMetadataRead = Environment.TickCount64;
            try
            {
                var observed = executionMetadata.ReadSnapshot();
                if (JsonNode.DeepEquals(lastExecutionMetadata, observed)) return;
                using var held = store.AcquireLock("run-" + runId, TimeSpan.Zero);
                var status = store.ReadStatus(runId);
                if (!Active(status)) return;
                if (observed is null) status.Remove("observedExecution"); else status["observedExecution"] = observed.DeepClone();
                status["updatedAt"] = Now();
                store.WriteJson(Path.Combine(directory, "status.json"), status);
                lastExecutionMetadata = observed;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ProtocolException or InvalidOperationException or ArgumentException)
            {
                // Runtime metadata is optional. It must never stop execution, stdout capture, or final result persistence.
            }
        }
    }

    // Raw output is the durable source. Parser and display-snapshot failures must not stop a
    // running CLI while that output can still be saved and its final result can still be read.
    internal static async Task DrainOutputAsync(Store store, string runId, StreamReader reader, string fileName,
        bool structured, CliAdapter adapter, StringBuilder outputTail, CancellationToken token, Action<string>? observeOutput = null,
        JsonObject? task = null)
    {
        var directory = store.RunDirectory(runId);
        var stream = structured ? "stdout" : "stderr";
        var phasesOnly = UsesPhaseOnlyProgress(task);
        var maximumLogBytes = adapter.MaximumLogBytes;
        var maximumEventCharacters = adapter.MaximumEventCharacters;
        var stage = "opening saved log";
        var reportedWarnings = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            await using var log = new FileStream(Path.Combine(directory, fileName), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            long written = log.Length;
            long lastStatusUpdate = 0;
            string? lastSession = null;
            string? lastPublishedPhase = null;
            var buffer = new char[8192];
            var line = new StringBuilder();
            bool overlong = false, announced = false;
            while (true)
            {
                stage = "reading CLI pipe";
                var count = await reader.ReadAsync(buffer.AsMemory(), token);
                if (count == 0) break;
                for (var index = 0; index < count; index++)
                {
                    if (buffer[index] != '\n')
                    {
                        if (line.Length < maximumEventCharacters) line.Append(buffer[index]);
                        else overlong = true;
                        continue;
                    }
                    await Emit();
                }
            }
            // A closed process pipe makes its final non-newline-terminated line complete.
            if (line.Length > 0 || overlong) await Emit();
            stage = "closing saved log";

            async Task Emit()
            {
                stage = "redacting CLI output";
                var text = OutputRedactor.Redact(line.ToString().TrimEnd('\r'));
                line.Clear();
                if (overlong)
                {
                    text = $"[OUTPUT_LINE_TOO_LONG: CLI output line exceeded the {maximumEventCharacters}-character transport limit and was discarded.]";
                    adapter.MarkOutputIncomplete(discardFinal: structured);
                    overlong = false;
                }
                if (text.Length == 0) return;
                var persisted = false;
                if (written < maximumLogBytes)
                {
                    var bytes = Encoding.UTF8.GetBytes(text + "\n");
                    if (written + bytes.Length <= maximumLogBytes)
                    {
                        stage = "writing saved log";
                        await log.WriteAsync(bytes, token);
                        await log.FlushAsync(token);
                        written += bytes.Length;
                        persisted = true;
                    }
                    else written = maximumLogBytes;
                }
                if (!persisted) adapter.MarkOutputIncomplete();
                lock (outputTail)
                {
                    outputTail.AppendLine(text);
                    if (outputTail.Length > 32768) outputTail.Remove(0, outputTail.Length - 32768);
                }
                var progress = text;
                if (structured)
                {
                    stage = "interpreting CLI output";
                    try { progress = adapter.Consume(text); }
                    catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException or ArgumentException or FormatException)
                    {
                        adapter.MarkOutputIncomplete(discardFinal: true);
                        progress = "A CLI event could not be interpreted. Its redacted raw output is retained in the task log.";
                        Warn(stage, exception);
                    }
                    observeOutput?.Invoke(text);
                }
                // Findings and final JSON are delivered through the saved result, once complete.
                // Neither free-form assistant text nor tool/diagnostic output may become an
                // interim review verdict. The complete redacted transport remains in its log.
                if (phasesOnly) progress = structured ? ProgressPhase(text) ?? "" : "";
                long? sequence = null;
                if (persisted && progress.Length > 0 && (!phasesOnly || progress != lastPublishedPhase))
                {
                    stage = "publishing progress event";
                    try
                    {
                        sequence = store.AppendEvent(runId, structured ? "cli.progress" : "cli.diagnostic", progress);
                        if (phasesOnly) lastPublishedPhase = progress;
                    }
                    catch (Exception exception) when (RecoverableSnapshotFailure(exception)) { Warn(stage, exception); }
                }
                if (written >= maximumLogBytes && !announced)
                {
                    announced = true;
                    stage = "recording log limit";
                    File.WriteAllText(Path.Combine(directory, fileName + ".truncated"), Now());
                    try { store.AppendEvent(runId, "log.truncated", $"{fileName} reached the {maximumLogBytes / (1024 * 1024)} MiB log limit. Output is still being drained, and the final summary is saved separately."); }
                    catch (Exception exception) when (RecoverableSnapshotFailure(exception)) { Warn("publishing progress event", exception); }
                }
                var session = structured ? adapter.SessionId : null;
                if (Environment.TickCount64 - lastStatusUpdate >= 1000 || session is not null && session != lastSession || phasesOnly && sequence is not null)
                {
                    stage = "updating task status";
                    try
                    {
                        using var held = store.AcquireLock("run-" + runId);
                        var status = store.ReadStatus(runId);
                        if (Active(status))
                        {
                            if (session is not null) status["cliSessionId"] = session;
                            status["updatedAt"] = Now();
                            if (!phasesOnly || progress.Length > 0)
                                status["latestProgress"] = progress.Length > 1000 ? progress[..1000] + "…" : progress;
                            if (sequence is not null) status["sequence"] = sequence.Value;
                            store.WriteJson(Path.Combine(directory, "status.json"), status);
                        }
                    }
                    catch (Exception exception) when (RecoverableSnapshotFailure(exception)) { Warn(stage, exception); }
                    // Avoid spinning on a temporarily locked snapshot for every event in a burst.
                    lastStatusUpdate = Environment.TickCount64;
                    lastSession = session;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            var details = OutputIssue(stream, stage, exception, fatal: true);
            SaveOutputIssue(store, runId, details, fatal: true);
            throw new CliOutputException(details, exception);
        }

        void Warn(string warningStage, Exception exception)
        {
            if (!reportedWarnings.Add(warningStage)) return;
            SaveOutputIssue(store, runId, OutputIssue(stream, warningStage, exception, fatal: false), fatal: false);
        }
    }

    internal static int? ExpectedResultSchemaVersion(JsonObject saved)
    {
        if (saved["promptTemplate"] is not JsonObject template || !template.ContainsKey("schemaVersion")) return null;
        return template["schemaVersion"] is JsonValue version && version.TryGetValue<int>(out var expected) ? expected : -1;
    }

    private static async Task<T> WithCandidateCancellation<T>(Store store, string runId, Func<CancellationToken, Task<T>> operation)
    {
        using var cancellation = new CancellationTokenSource();
        var watching = Watch();
        try { return await operation(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { throw new ProtocolException("CANCELLED", "Candidate preparation was cancelled. Existing task workspaces and artifacts are preserved."); }
        finally { cancellation.Cancel(); await watching; }

        async Task Watch()
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    if (File.Exists(Path.Combine(store.RunDirectory(runId), "cancel.json"))) { cancellation.Cancel(); return; }
                    await Task.Delay(100, cancellation.Token);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    internal static bool UsesPhaseOnlyProgress(JsonObject? task) => CliAdapter.String(task, "actionKind")
        is "pr-review" or "pr-verify" or "bug-investigation" or "feature-research" or "reproduction-setup";

    // This allowlist reads only transport metadata, never task content or a claimed model
    // finding/phase. Unknown events remain diagnostic evidence without becoming UI messages.
    private static string? ProgressPhase(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 48 });
            var envelope = document.RootElement;
            var type = Text(envelope, "type");
            return type switch
            {
                "thread.started" or "turn.started" or "session.start" or "system" or "assistant.turn_start"
                    => "Preparing task analysis.",
                "assistant" or "assistant.message" or "assistant.message_start" or "assistant.message_delta"
                    => "Reviewing the collected evidence.",
                "tool.execution_start" or "tool.execution_complete" => "Collecting evidence within the selected scope.",
                "turn.completed" or "session.idle" or "result" => "Preparing the final task report.",
                "turn.failed" or "session.error" or "error" => "The agent reported an execution problem. See the execution logs.",
                "item.started" or "item.updated" or "item.completed" => Text(Field(envelope, "item"), "type") switch
                {
                    "agent_message" or "reasoning" => "Reviewing the collected evidence.",
                    "command_execution" or "web_search" or "mcp_tool_call" => "Collecting evidence within the selected scope.",
                    "todo_list" => "Planning and tracking the selected work.",
                    "file_change" => "Checking task workspace changes.",
                    _ => null
                },
                _ => null
            };
        }
        catch (JsonException) { return null; }

        static JsonElement Field(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) ? field : default;
        static string? Text(JsonElement value, string key) => Field(value, key) is { ValueKind: JsonValueKind.String } field ? field.GetString() : null;
    }

    private static bool RecoverableSnapshotFailure(Exception exception) => exception is IOException or UnauthorizedAccessException ||
        exception is ProtocolException { Code: "RECORD_UNREADABLE" or "RESOURCE_BUSY" };

    private static JsonObject OutputIssue(string stream, string stage, Exception exception, bool fatal)
    {
        var cause = exception is AggregateException aggregate ? aggregate.Flatten().InnerExceptions.FirstOrDefault() ?? exception : exception;
        var detail = OutputRedactor.Redact(cause.Message);
        if (detail.Length > 2000) detail = detail[..2000] + "…";
        return new JsonObject
        {
            ["stream"] = stream, ["stage"] = stage, ["fatal"] = fatal, ["recordedAt"] = Now(),
            ["exceptionType"] = cause.GetType().FullName, ["hresult"] = cause.HResult,
            ["causeCode"] = (cause as ProtocolException)?.Code,
            ["message"] = $"CLI {stream} output failed while {stage} ({cause.GetType().Name}): {detail}"
        };
    }

    private static void SaveOutputIssue(Store store, string runId, JsonObject details, bool fatal)
    {
        try { store.WriteJson(Path.Combine(store.RunDirectory(runId), fatal ? "output-error.json" : "output-warning.json"), details); }
        catch (Exception exception) when (RecoverableSnapshotFailure(exception)) { /* The original cause remains in the thrown error if storage is unavailable. */ }
    }

    internal sealed class CliOutputException(JsonObject details, Exception cause) : IOException(details["message"]!.GetValue<string>(), cause)
    {
        public JsonObject Details { get; } = details;
    }

    private static async Task WritePrompt(StreamWriter writer, string prompt)
    {
        try { await writer.WriteAsync(prompt); await writer.FlushAsync(); }
        catch (IOException) { /* A CLI startup/auth failure can close stdin; stderr and exit code identify it. */ }
        finally { writer.Close(); }
    }

    private static Task<string> ProbeCommand(string path, IReadOnlyList<string> arguments) => ProbeCommandWithTimeout(path, arguments, TimeSpan.FromSeconds(10));

    private static async Task<string> ProbeCommandWithTimeout(string path, IReadOnlyList<string> arguments, TimeSpan maximumTime)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Remove("COPILOT_ALLOW_ALL");
        using var process = Process.Start(start) ?? throw new IOException("Cannot start CLI probe.");
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(maximumTime);
        var stdout = ReadBounded(process.StandardOutput, timeout.Token);
        var stderr = ReadBounded(process.StandardError, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var text = await stdout + "\n" + await stderr;
            if (process.ExitCode != 0) throw new IOException(OutputRedactor.Redact(text));
            return OutputRedactor.Redact(text);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    private static async Task<string> ReadBounded(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[4096]; var text = new StringBuilder(); int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
            if (text.Length < 65536) text.Append(buffer, 0, Math.Min(count, 65536 - text.Length));
        return text.ToString();
    }

    internal static string? ResolveExecutable(string value) => ResolveExecutableCandidates(value).FirstOrDefault();

    private static IReadOnlyList<string> ResolveExecutableCandidates(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        if (Path.IsPathFullyQualified(value))
            return File.Exists(value) && Path.GetExtension(value).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? [Path.GetFullPath(value)] : [];
        if (value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar)) return [];
        var name = value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value : value + ".exe";
        var candidates = new List<string>();
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var directory = entry.Trim().Trim('"');
                if (!Path.IsPathFullyQualified(directory)) continue;
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) candidates.Add(Path.GetFullPath(candidate));
            }
            catch (ArgumentException) { }
        }
        // Native Messaging inherits the browser's PATH, which can predate an installed CLI.
        // Only native .exe files from standard installation locations are considered; scripts
        // and shell shims are never executed as a way to discover or launch an agent.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (name.Equals("codex.exe", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(Path.Combine(local, "Programs", "OpenAI", "Codex", "bin", name));
            candidates.Add(Path.Combine(local, "Microsoft", "WinGet", "Links", name));
            AddMatchingDirectories(Path.Combine(local, "Microsoft", "WinGet", "Packages"), "OpenAI.Codex*", name);
            AddMatchingDirectories(Path.Combine(local, "OpenAI", "Codex", "bin"), "*", name);
            AddNpmExecutables("@openai", "codex", "vendor", "x86_64-pc-windows-msvc", "codex", name);
        }
        else if (name.Equals("copilot.exe", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(Path.Combine(local, "Microsoft", "WinGet", "Links", name));
            candidates.Add(Path.Combine(local, "Programs", "GitHub Copilot", name));
            candidates.Add(Path.Combine(local, "Programs", "GitHub", "Copilot", name));
            AddMatchingDirectories(Path.Combine(local, "Microsoft", "WinGet", "Packages"), "GitHub.Copilot*", name);
            AddNpmExecutables("@github", "copilot", name);
            AddNpmExecutables("@github", "copilot-win32-x64", name);
            AddNpmExecutables("@github", "copilot", "node_modules", "@github", "copilot-win32-x64", name);
        }
        return candidates.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        void AddMatchingDirectories(string parent, string pattern, string executableName)
        {
            try
            {
                if (Directory.Exists(parent)) candidates.AddRange(Directory.EnumerateDirectories(parent, pattern)
                    .OrderByDescending(Directory.GetLastWriteTimeUtc).Select(folder => Path.Combine(folder, executableName)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        void AddNpmExecutables(params string[] parts)
        {
            var modules = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules");
            candidates.Add(Path.Combine(new[] { modules }.Concat(parts).ToArray()));
        }
    }

    private static bool? IsSameProcess(JsonObject? identity)
    {
        if (identity is null) return false;
        if (identity["pid"] is not JsonValue pidNode || !pidNode.TryGetValue<int>(out var pid) ||
            !DateTimeOffset.TryParse(CliAdapter.String(identity, "startTimeUtc"), out var started)) return null;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == started.UtcTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return null; }
    }

    private static bool KillVerifiedProcess(JsonObject identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity["pid"]!.GetValue<int>());
            if (!DateTimeOffset.TryParse(CliAdapter.String(identity, "startTimeUtc"), out var started) ||
                process.StartTime.ToUniversalTime().Ticks != started.UtcTicks) return true;
            process.Kill(entireProcessTree: true);
            return process.WaitForExit(5000);
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
        catch (Win32Exception) { return false; }
    }

    private static string ClassifyFailure(string text)
    {
        if (Regex.IsMatch(text, "not logged in|not authenticated|authentication|unauthorized|login required|401", RegexOptions.IgnoreCase)) return "AUTH_REQUIRED";
        if (Regex.IsMatch(text, "permission|approval|denied|not allowed|interactive|allow.all.tools", RegexOptions.IgnoreCase)) return "PERMISSION_DENIED";
        return "CLI_EXIT_FAILED";
    }
    private static JsonObject Identity(WindowsProcess process) => new() { ["pid"] = process.Id, ["startTimeUtc"] = process.StartTimeUtc };
    private static string Required(JsonObject value, string key) => CliAdapter.String(value, key) ?? throw new InvalidOperationException("Missing saved setting: " + key);
    private static bool Active(JsonObject status) => CliAdapter.String(status, "state") is "accepted" or "running";
    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    private static JsonObject Error(string code, string message) => new() { ["code"] = code, ["message"] = message, ["guidance"] = "Inspect task diagnostics, current settings, and incomplete validation. Correct the issue before starting a new run." };
    private static JsonObject Unavailable(string? path, string code, string message) => new() { ["available"] = false, ["path"] = path, ["version"] = null, ["error"] = Error(code, message) };
    private sealed class WorkerStop(string state, string code, string message) : Exception(message) { public string State { get; } = state; public string Code { get; } = code; }
}
