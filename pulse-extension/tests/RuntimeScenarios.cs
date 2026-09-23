using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pulse.Host.Tests;

internal static class RuntimeScenarios
{
    // Explicit acceptance entrypoint only: invokes one real model and retains its evidence.
    // The ordinary fake harness below never calls this method.
    public static async Task<int> LiveLogAsync(string agent, bool validateCommand = false)
    {
        var id = Guid.NewGuid().ToString("D");
        Store? store = null;
        var created = false;
        var cancelled = false;
        var passed = false;
        var stdoutCursor = 0L;
        var stderrCursor = 0L;
        var liveStdoutBytes = 0L;
        var liveStderrBytes = 0L;
        var structuredEvents = 0;
        var logTruncated = false;
        var commandCount = 0;
        var successfulCommandCount = 0;
        var commandCapturedBeforeCompletion = false;
        var executionMetadataCapturedBeforeCompletion = false;
        var unexpectedTools = new HashSet<string>(StringComparer.Ordinal);
        var commands = new JsonArray();
        string? nonce = null;
        const string nonceFile = "pulse-command-nonce.txt";
        string? firstLiveOutputAt = null;
        string? failure = null;
        var response = new JsonObject
        {
            ["agent"] = agent, ["state"] = "failed", ["exitCode"] = null,
            ["stdoutBytes"] = 0, ["stderrBytes"] = 0, ["stdoutPath"] = null, ["stderrPath"] = null, ["runId"] = id
        };
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            if (agent is not ("codex" or "copilot")) throw new InvalidOperationException("Choose codex or copilot for the live log test.");
            if (validateCommand && agent != "codex") throw new InvalidOperationException("The live command diagnostic currently supports Codex only.");
            if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("The live log test requires Windows.");
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") throw new InvalidOperationException("Run the generated Windows test apphost.");
            var workspace = new DirectoryInfo(AppContext.BaseDirectory);
            while (!File.Exists(Path.Combine(workspace.FullName, "host", "Pulse.Host.csproj")) || !File.Exists(Path.Combine(workspace.FullName, "tests", "Pulse.Host.Tests.csproj")))
                workspace = workspace.Parent ?? throw new InvalidOperationException("The Pulse Extension source workspace could not be located.");
            store = new Store(Path.Combine(workspace.FullName, ".tmp", validateCommand ? "cli-command-check" : "cli-log-check", agent));
            var directory = store.RunDirectory(id);
            response["stdoutPath"] = Path.Combine(directory, "stdout.jsonl");
            response["stderrPath"] = Path.Combine(directory, "stderr.log");
            var repository = Path.Combine(store.Root, "repository-" + id);
            // This new nested repository has no remote, linked worktree, copied AGENTS.md, or user files.
            Directory.CreateDirectory(repository);
            if (validateCommand)
            {
                nonce = "pulse-command-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
                await File.WriteAllTextAsync(Path.Combine(repository, nonceFile), nonce + "\n", new UTF8Encoding(false), cancellation.Token);
            }
            var probe = await RuntimeService.ProbeAgentAsync(agent, null);
            var selected = validateCommand
                ? "This is a bounded read-only command diagnostic in a newly created empty repository. Run exactly one shell command: Get-Content -LiteralPath .\\pulse-command-nonce.txt -Raw. Do not read any other file or directory, run any other shell command, use planning or other tools, access the network or GitHub, edit files, or inspect configuration. Put only the trimmed exact text returned by that command in the required result JSON summary. Do not guess or invent the file contents. If the command cannot run, describe the failure in summary and blockers. On success use empty artifacts, validation, and blockers arrays, review null, needsReview false, and a none next step. Do not perform any other task."
                : "Answer only this question: what's your model. Do not use tools, inspect files, edit files, or contact GitHub. Put your actual answer in the required result JSON summary. If the model identity is unavailable, say so without guessing. Use empty artifacts, validation, and blockers arrays, review null, needsReview false, and a none next step. Do not perform any other task.";
            store.CreateRun(id, new JsonObject
            {
                ["requestId"] = id, ["actionId"] = (validateCommand ? "local-cli-command-check:" : "local-cli-log-check:") + agent, ["actionKind"] = "reproduction-setup",
                ["repository"] = validateCommand ? "local/cli-command-check" : "local/cli-log-check",
                ["prompt"] = validateCommand ? "Read the single diagnostic nonce file with the one specified read-only shell command and return its exact contents." : "what's your model"
            }, new JsonObject
            {
                ["agent"] = agent, ["cliPath"] = probe["path"]?.DeepClone() ?? JsonValue.Create(""),
                ["cliVersion"] = probe["version"]?.DeepClone(), ["repoFolder"] = repository,
                ["repositoryKey"] = Protocol.Hash(repository), ["permission"] = "read-only", ["githubAccount"] = "",
                ["model"] = "", ["reasoningEffort"] = ""
            }, Protocol.ProductionOrigin, new JsonObject
            {
                ["name"] = validateCommand ? "local-read-only-command-check" : "local-model-identity-check", ["body"] = selected,
                ["sha"] = Protocol.Hash(selected), ["revision"] = validateCommand ? "live-command-test" : "live-log-test"
            });
            created = true;
            if (probe["available"]?.GetValue<bool>() != true)
            {
                var error = probe["error"] as JsonObject ?? new JsonObject { ["code"] = "CLI_UNAVAILABLE", ["message"] = "The requested CLI is unavailable." };
                store.Complete(id, "failed", null, CliAdapter.Fallback("failed", error["code"]?.GetValue<string>(), error["message"]?.GetValue<string>()), error);
            }
            else
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var git = RuntimeService.ResolveExecutable("git") ?? throw new InvalidOperationException("Git is required to initialize the diagnostic repository.");
                using var gitProcess = WindowsProcess.Cli(git, ["init", "--quiet", "--template=", "--", repository], repository);
                var gitOutput = gitProcess.Output!.ReadToEndAsync(cancellation.Token);
                var gitError = gitProcess.Error!.ReadToEndAsync(cancellation.Token);
                gitProcess.Input!.Close(); gitProcess.Resume();
                try { await gitProcess.WaitAsync().WaitAsync(cancellation.Token); await Task.WhenAll(gitOutput, gitError); }
                finally { gitProcess.Kill(); }
                if (gitProcess.ExitCode != 0) throw new InvalidOperationException("The diagnostic Git repository could not be initialized.");
                cancellation.Token.ThrowIfCancellationRequested();
                StartTestWorker(store, id); // Test-only supervised fallback; production detachment is unchanged.
                while (Protocol.IsActive(store.ReadStatus(id)["state"]?.GetValue<string>()))
                {
                    var liveStatus = store.ReadStatus(id);
                    if (Protocol.IsActive(liveStatus["state"]?.GetValue<string>()) &&
                        liveStatus["observedExecution"]?["model"] is JsonValue && liveStatus["observedExecution"]?["reasoningEffort"] is JsonValue)
                        executionMetadataCapturedBeforeCompletion = true;
                    if (cancellation.IsCancellationRequested && !cancelled) { cancelled = true; RuntimeService.RequestCancel(store, id); }
                    ReadLogs(active: true);
                    RuntimeService.Reconcile(store, id);
                    await Task.Delay(100);
                }
                ReadLogs(active: false);
            }
        }
        catch (Exception exception)
        {
            failure = OutputRedactor.Redact(exception.Message);
            cancelled |= exception is OperationCanceledException || cancellation.IsCancellationRequested;
            if (store is not null && created)
            {
                var status = store.ReadStatus(id);
                if (Protocol.IsActive(status["state"]?.GetValue<string>()) && status["worker"] is null)
                {
                    var state = cancelled ? "cancelled" : "failed";
                    var code = cancelled ? "CANCELLED" : validateCommand ? "LIVE_COMMAND_SETUP_FAILED" : "LIVE_LOG_SETUP_FAILED";
                    store.Complete(id, state, null, CliAdapter.Fallback(state, code, failure), new JsonObject { ["code"] = code, ["message"] = failure });
                }
            }
        }
        finally
        {
            if (store is not null && created)
            {
                if (Protocol.IsActive(store.ReadStatus(id)["state"]?.GetValue<string>()))
                {
                    RuntimeService.RequestCancel(store, id);
                    while (Protocol.IsActive(store.ReadStatus(id)["state"]?.GetValue<string>())) { RuntimeService.Reconcile(store, id); await Task.Delay(100); }
                }
                try { ReadLogs(active: false); } catch (ProtocolException exception) { failure ??= exception.Message; }
                var status = store.ReadStatus(id);
                var result = store.ReadResult(id);
                response["state"] = status["state"]?.DeepClone(); response["exitCode"] = status["exitCode"]?.DeepClone();
                response["stdoutBytes"] = stdoutCursor; response["stderrBytes"] = stderrCursor;
                passed = failure is null && !logTruncated && status["state"]?.GetValue<string>() == "succeeded" && status["exitCode"]?.GetValue<int>() == 0 &&
                    liveStdoutBytes > 0 && structuredEvents > 0 && result?["structured"]?.GetValue<bool>() == true && !string.IsNullOrWhiteSpace(result?["summary"]?.GetValue<string>());
                if (validateCommand)
                {
                    var summaryMatches = nonce is not null && result?["summary"]?.GetValue<string>().Trim() == nonce;
                    var nonceInPrompt = nonce is not null && store.ReadTask(id).ToJsonString().Contains(nonce, StringComparison.Ordinal);
                    passed &= commandCount == 1 && successfulCommandCount == 1 && commandCapturedBeforeCompletion && unexpectedTools.Count == 0 && summaryMatches && !nonceInPrompt && executionMetadataCapturedBeforeCompletion;
                    if (!passed && failure is null)
                        failure = "Read-only command verification requires exactly one successful shell command returning the undisclosed file nonce, its output and actual model/effort saved before task completion, no other tools, and a matching structured final summary.";
                    response["test"] = "read-only-command";
                    response["verified"] = passed;
                    response["commandCount"] = commandCount;
                    response["commandCapturedBeforeCompletion"] = commandCapturedBeforeCompletion;
                    response["summaryMatchesFile"] = summaryMatches;
                    response["nonceInPrompt"] = nonceInPrompt;
                    response["executionMetadataCapturedBeforeCompletion"] = executionMetadataCapturedBeforeCompletion;
                    response["observedExecution"] = status["observedExecution"]?.DeepClone();
                }
                var verification = new JsonObject
                {
                    ["verified"] = passed, ["capturedBeforeCompletion"] = liveStdoutBytes > 0,
                    ["liveStdoutBytes"] = liveStdoutBytes, ["liveStderrBytes"] = liveStderrBytes,
                    ["stdoutBytes"] = stdoutCursor, ["stderrBytes"] = stderrCursor,
                    ["structuredStdoutEvents"] = structuredEvents, ["firstLiveOutputAt"] = firstLiveOutputAt,
                    ["logTruncated"] = logTruncated,
                    ["summary"] = result?["summary"]?.DeepClone(), ["structuredResult"] = result?["structured"]?.DeepClone(),
                    ["failure"] = failure, ["recordedAt"] = Protocol.Now()
                };
                if (validateCommand)
                {
                    verification["test"] = "read-only-command";
                    verification["commandCount"] = commandCount;
                    verification["successfulCommandCount"] = successfulCommandCount;
                    verification["commandCapturedBeforeCompletion"] = commandCapturedBeforeCompletion;
                    verification["unexpectedToolTypes"] = new JsonArray(unexpectedTools.Select(type => (JsonNode?)JsonValue.Create(type)).ToArray());
                    verification["commands"] = commands;
                    verification["nonceFile"] = nonceFile;
                    verification["nonceInPrompt"] = nonce is not null && store.ReadTask(id).ToJsonString().Contains(nonce, StringComparison.Ordinal);
                    verification["summaryMatchesFile"] = nonce is not null && result?["summary"]?.GetValue<string>().Trim() == nonce;
                    verification["executionMetadataCapturedBeforeCompletion"] = executionMetadataCapturedBeforeCompletion;
                    verification["observedExecution"] = status["observedExecution"]?.DeepClone();
                }
                store.WriteJson(Path.Combine(store.RunDirectory(id), validateCommand ? "command-verification.json" : "log-verification.json"), verification);
            }
            Console.WriteLine(response.ToJsonString());
            Console.CancelKeyPress -= cancel;
        }
        return passed ? 0 : cancelled ? 130 : 1;

        void ReadLogs(bool active)
        {
            if (store is null || !created) return;
            foreach (var stream in new[] { "stdout", "stderr" })
            {
                while (true)
                {
                    var before = stream == "stdout" ? stdoutCursor : stderrCursor;
                    var page = RunLogs.Read(store, id, stream, before);
                    logTruncated |= page["truncated"]!.GetValue<bool>();
                    var next = page["nextCursor"]!.GetValue<long>();
                    var capturedWhileActive = active && next > before && Protocol.IsActive(store.ReadStatus(id)["state"]?.GetValue<string>());
                    if (stream == "stdout")
                    {
                        stdoutCursor = next;
                        foreach (var line in page["text"]!.GetValue<string>().Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        {
                            try
                            {
                                using var document = JsonDocument.Parse(line);
                                var entry = document.RootElement;
                                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) continue;
                                structuredEvents++;
                                if (!validateCommand || !entry.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object ||
                                    !item.TryGetProperty("type", out var itemType) || itemType.ValueKind != JsonValueKind.String) continue;
                                var itemKind = itemType.GetString() ?? "";
                                if (itemKind is not ("command_execution" or "agent_message" or "reasoning")) unexpectedTools.Add(itemKind);
                                if (type.GetString() != "item.completed" || itemKind != "command_execution") continue;
                                commandCount++;
                                var command = item.TryGetProperty("command", out var commandNode) && commandNode.ValueKind == JsonValueKind.String ? commandNode.GetString() ?? "" : "";
                                var output = item.TryGetProperty("aggregated_output", out var outputNode) && outputNode.ValueKind == JsonValueKind.String ? outputNode.GetString() ?? "" : "";
                                var code = item.TryGetProperty("exit_code", out var exitNode) && exitNode.ValueKind == JsonValueKind.Number && exitNode.TryGetInt32(out var value) ? value : (int?)null;
                                var exactOutput = nonce is not null && output.Trim() == nonce;
                                var success = code == 0 && exactOutput && command.Contains(nonceFile, StringComparison.Ordinal);
                                if (success) { successfulCommandCount++; commandCapturedBeforeCompletion |= capturedWhileActive; }
                                commands.Add(new JsonObject { ["command"] = command, ["exitCode"] = code, ["outputMatchesFile"] = exactOutput, ["capturedBeforeCompletion"] = capturedWhileActive });
                            }
                            catch (JsonException) { }
                        }
                    }
                    else stderrCursor = next;
                    if (capturedWhileActive)
                    {
                        if (stream == "stdout") liveStdoutBytes += next - before; else liveStderrBytes += next - before;
                        firstLiveOutputAt ??= Protocol.Now();
                    }
                    if (page["eof"]!.GetValue<bool>() || next == before) break;
                }
            }
        }
    }

    public static async Task RunAllAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new Exception("Windows process acceptance requires Windows.");
        var root = Path.Combine(Path.GetTempPath(), "Pulse runtime 中文 " + Guid.NewGuid().ToString("N"));
        var store = new Store(Path.Combine(root, "data"));
        var executable = Environment.ProcessPath!;
        if (Path.GetFileNameWithoutExtension(executable) == "dotnet") throw new Exception("Run tests with their generated Windows apphost, not dotnet test.dll.");
        var copilotExe = Path.Combine(Path.GetDirectoryName(executable)!, "fake-copilot-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            File.Copy(executable, copilotExe);
            AdapterValidation();
            RawLogContract(store, root, executable);
            await OutputPersistence(store, root, executable);
            await DetachedCompletion(store, root, "codex", executable);
            await DetachedCompletion(store, root, "copilot", copilotExe);
            await ExecutionScenarios.VerifyWorkerAsync(store, root, "codex", executable);
            await ExecutionScenarios.VerifyWorkerAsync(store, root, "copilot", copilotExe);
            await Cancellation(store, root, executable);
            await NoTaskTimeout(store, root, executable);
            await Failure(store, root, executable);
            Recovery(store, root, executable);
        }
        finally
        {
            foreach (var id in store.RunIds())
            {
                var status = store.ReadStatus(id);
                if (!Protocol.IsActive(status["state"]?.GetValue<string>())) continue;
                RuntimeService.RequestCancel(store, id);
                try { await Until(() => !Protocol.IsActive(store.ReadStatus(id)["state"]?.GetValue<string>()), 15); } catch { }
            }
            try { File.Delete(copilotExe); } catch (IOException) { }
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static void AdapterValidation()
    {
        var arguments = CliAdapter.Arguments("codex", "read-only", "C:\\path with 空格\\schema.json");
        CoreScenarios.Check(arguments.Contains("read-only") && !arguments.Contains("--dangerously-bypass-approvals-and-sandbox"), "Codex keeps explicit sandbox policy");
        var copilot = CliAdapter.Arguments("copilot", "workspace-write", "unused");
        CoreScenarios.Check(copilot.Contains("--deny-tool=shell") && !copilot.Contains("--allow-all-tools") && !copilot.Contains("--allow-all-paths"), "Copilot keeps bounded file tool grants");
        CoreScenarios.Check(CliAdapter.Arguments("codex", "yolo", "schema.json").Contains("--dangerously-bypass-approvals-and-sandbox"), "Explicit Codex YOLO bypasses approvals and sandbox");
        var copilotYolo = CliAdapter.Arguments("copilot", "yolo", "unused");
        CoreScenarios.Check(copilotYolo.Contains("--yolo") && !copilotYolo.Contains("--deny-tool=shell"), "Explicit Copilot YOLO enables its official permission mode");
        var adapter = new CliAdapter("codex");
        adapter.Consume("{\"type\":\"turn.failed\",\"error\":{\"message\":\"authorization required\"}}");
        CoreScenarios.Check(adapter.Failure is not null, "CLI failure events are independent of process exit code");
        var fallback = adapter.Result("failed", 7, "AUTH_REQUIRED", "Login required");
        CoreScenarios.Check(fallback["needsReview"]!.GetValue<bool>() && fallback["validation"]!.AsArray().Count == 0, "Fallback never invents validation evidence");
        var malformed = new CliAdapter("copilot");
        malformed.Consume("{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"ordinary readable text\"}");
        CoreScenarios.Check(malformed.Result("succeeded", 0, null, null)["needsReview"]!.GetValue<bool>(), "Unstructured success requires inspection");
        var webSearch = new CliAdapter("codex");
        foreach (var type in new[] { "item.started", "item.completed" })
        {
            var eventJson = "{\"type\":\"" + type + "\",\"item\":{\"id\":\"item_1\",\"type\":\"web_search\",\"id\":\"web_call_1\",\"query\":\"synthetic query\",\"action\":{\"type\":\"search\",\"query\":\"synthetic query\"}}}";
            CoreScenarios.Check(webSearch.Consume(eventJson) == type + ": web_search", "Duplicate web-search transport ids do not crash event interpretation");
        }
        var expectedResult = new JsonObject
        {
            ["summary"] = "The selected task finished.", ["artifacts"] = new JsonArray(), ["validation"] = new JsonArray(),
            ["blockers"] = new JsonArray(), ["nextSteps"] = new JsonArray(new JsonObject { ["kind"] = "none", ["reason"] = "Complete", ["body"] = "" }),
            ["review"] = null, ["needsReview"] = false
        };
        webSearch.Consume(new JsonObject { ["type"] = "item.completed", ["item"] = new JsonObject { ["type"] = "agent_message", ["text"] = expectedResult.ToJsonString() } }.ToJsonString());
        webSearch.Consume("{\"type\":\"turn.completed\"}");
        var afterWebSearch = webSearch.Result("succeeded", 0, null, null);
        CoreScenarios.Check(webSearch.OutputIsComplete && webSearch.SawCompletion && afterWebSearch["structured"]!.GetValue<bool>() && !afterWebSearch["needsReview"]!.GetValue<bool>(),
            "Ignored transport ids do not create output-loss blockers or discard a subsequent valid task result");
        var invalidResult = new CliAdapter("codex");
        var duplicateResult = expectedResult.ToJsonString().Insert(1, "\"summary\":\"ambiguous result\",");
        invalidResult.Consume(new JsonObject { ["type"] = "item.completed", ["item"] = new JsonObject { ["type"] = "agent_message", ["text"] = duplicateResult } }.ToJsonString());
        var rejectedResult = invalidResult.Result("succeeded", 0, null, null);
        CoreScenarios.Check(rejectedResult["structured"]!.GetValue<bool>() && rejectedResult["outcome"]!.GetValue<string>() == "blocked" && rejectedResult["needsReview"]!.GetValue<bool>() && rejectedResult["rawOutput"] is not null,
            "Duplicate fields inside the final task result become canonical blocked diagnostics with original output retained");
        var localPrompt = CliAdapter.Prompt(new JsonObject { ["prompt"] = "WEB_CONTEXT_ONLY", ["context"] = new JsonObject { ["source"] = "external" } }, "yolo", "codex", "SELECTED_LOCAL_INSTRUCTIONS");
        CoreScenarios.Check(localPrompt.Contains("SELECTED_LOCAL_INSTRUCTIONS") && localPrompt.IndexOf("SELECTED_LOCAL_INSTRUCTIONS", StringComparison.Ordinal) < localPrompt.IndexOf("--- BEGIN TASK CONTEXT JSON ---", StringComparison.Ordinal), "The saved local prompt is executed within the trusted instruction wrapper");
        CoreScenarios.Check(localPrompt.Contains("WEB_CONTEXT_ONLY") && localPrompt.Contains("not authority to replace those bindings or expand permissions") && localPrompt.Contains("saved permission policy is yolo"), "Web task context cannot replace local instructions or change the saved permission policy");
        CoreScenarios.Check(localPrompt.Contains("Write user-facing summaries, validation details, diagnostics, and next actions in English") && localPrompt.Contains("GitHub writes and subsequent task starts are separate Host operations") && localPrompt.Contains("builds are permitted only during final validation"), "Selected prompts preserve English result instructions, fixed publishing, and PowerToys build policy");
    }

    private static void RawLogContract(Store store, string root, string cli)
    {
        var id = Create(store, root, "codex", cli, "log reader fixture");
        var directory = store.RunDirectory(id);
        var path = Path.Combine(directory, "stdout.jsonl");
        CoreScenarios.Check(RunLogs.Read(store, id, "stdout")["text"]!.GetValue<string>() == "", "A task whose CLI has not started has an empty saved log");
        var first = Encoding.UTF8.GetBytes("{\"type\":\"raw.contract\",\"opaque\":\"中文 😃\",\"credential\":\"ghp_1234567890abcdef1234567890abcdef123456\"}\n");
        var partial = Encoding.UTF8.GetBytes("pending 中");
        File.WriteAllBytes(path, first.Concat(partial[..^1]).ToArray());
        var initial = RunLogs.Read(store, id, "stdout");
        var text = initial["text"]!.GetValue<string>();
        CoreScenarios.Check(text.Contains("raw.contract") && text.Contains("中文 😃") && text.Contains("[REDACTED]") && !text.Contains("ghp_"), "Raw JSON and Unicode remain readable while known credentials are redacted");
        CoreScenarios.Check(initial["nextCursor"]!.GetValue<long>() == first.Length && !initial["eof"]!.GetValue<bool>(), "Partial UTF-8 tails never advance a byte cursor");
        using (var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { append.WriteByte(partial[^1]); append.WriteByte((byte)'\n'); }
        var resumed = RunLogs.Read(store, id, "stdout", first.Length);
        CoreScenarios.Check(resumed["text"]!.GetValue<string>() == "pending 中\n" && resumed["eof"]!.GetValue<bool>() && resumed["nextCursor"]!.GetValue<long>() == new FileInfo(path).Length, "A completed Unicode tail is delivered exactly once on the next read");
        var complete = RunLogs.Read(store, id, "stdout", resumed["nextCursor"]!.GetValue<long>());
        CoreScenarios.Check(complete["text"]!.GetValue<string>() == "" && complete["eof"]!.GetValue<bool>(), "Polling at a complete cursor does not duplicate saved output");
        ExpectLogError("INVALID_LOG_STREAM", () => RunLogs.Read(store, id, "../task.json"));
        ExpectLogError("INVALID_REQUEST", () => RunLogs.Read(store, "../outside", "stdout"));
        ExpectLogError("INVALID_LOG_CURSOR", () => RunLogs.Read(store, id, "stdout", 1));
        ExpectLogError("INVALID_LOG_CURSOR", () => RunLogs.Read(store, id, "stdout", -1));
        ExpectLogError("INVALID_LOG_CURSOR", () => RunLogs.Read(store, id, "stdout", long.MaxValue));
        ExpectLogError("INVALID_LOG_CURSOR", () => RunLogs.Read(store, id, "stdout", 0, RunLogs.MaximumReadBytes + 1));
        ExpectLogError("TASK_NOT_FOUND", () => RunLogs.Read(store, Guid.NewGuid().ToString("D"), "stdout"));

        File.WriteAllText(path, new string('a', 800) + "\n" + new string('b', 800) + "\n", new UTF8Encoding(false));
        var page = RunLogs.Read(store, id, "stdout", 0, 1024);
        CoreScenarios.Check(page["nextCursor"]!.GetValue<long>() == 801 && !page["eof"]!.GetValue<bool>() && !page["truncated"]!.GetValue<bool>(), "Normal log pagination stops at a complete line without flagging data loss");
        var lastPage = RunLogs.Read(store, id, "stdout", 801, 1024);
        CoreScenarios.Check(lastPage["text"]!.GetValue<string>() == new string('b', 800) + "\n" && lastPage["eof"]!.GetValue<bool>(), "The next page resumes at the next complete line");
        File.WriteAllText(path, new string('<', 60000) + "\n", new UTF8Encoding(false));
        var escaped = RunLogs.Read(store, id, "stdout", 0, RunLogs.MaximumReadBytes);
        CoreScenarios.Check(escaped["truncated"]!.GetValue<bool>() && escaped["nextCursor"]!.GetValue<long>() == 60001 && JsonSerializer.SerializeToUtf8Bytes(escaped).Length < 256 * 1024, "JSON escaping cannot overflow the bounded log response; oversized complete lines are explicitly marked");
        File.WriteAllText(path, string.Concat(Enumerable.Repeat("中😃", 30000)) + "\n", new UTF8Encoding(false));
        var unicode = RunLogs.Read(store, id, "stdout");
        CoreScenarios.Check(unicode["truncated"]!.GetValue<bool>() && !unicode["text"]!.GetValue<string>().Contains('\uFFFD') && Encoding.UTF8.GetByteCount(unicode["text"]!.GetValue<string>()) <= 65536, "A long Unicode line is truncated only at a complete Unicode scalar");
        File.WriteAllText(Path.Combine(directory, "stderr.log"), "retained diagnostic\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "stderr.log.truncated"), Protocol.Now());
        CoreScenarios.Check(RunLogs.Read(store, id, "stderr")["truncated"]!.GetValue<bool>(), "The stream's persisted log-cap marker remains visible after completion");
        File.WriteAllBytes(path, [0xff, 0x0a]);
        ExpectLogError("LOG_UNREADABLE", () => RunLogs.Read(store, id, "stdout"));
        var status = store.ReadStatus(id); status["createdAt"] = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O");
        store.WriteJson(Path.Combine(directory, "status.json"), status);
        RuntimeService.Reconcile(store, id);
    }

    private static void ExpectLogError(string code, Func<JsonObject> action)
    {
        try { action(); } catch (ProtocolException exception) when (exception.Code == code) { return; }
        throw new InvalidOperationException("Expected log error " + code);
    }

    private static async Task OutputPersistence(Store store, string root, string cli)
    {
        const string firstEvent = "{\"type\":\"thread.started\",\"thread_id\":\"fixture-session\"}\n";
        const string nextEvent = "{\"type\":\"item.completed\",\"item\":{\"type\":\"command_execution\",\"aggregated_output\":\"saved output 中文\"}}\n";
        var id = Create(store, root, "codex", cli, "output persistence fixture");
        var directory = store.RunDirectory(id);
        var adapter = new CliAdapter("codex");
        // A third-party reader may omit Delete sharing and temporarily prevent atomic status
        // replacement. This must not destroy the durable raw output or abort the CLI's pump.
        using (var heldSnapshot = new FileStream(Path.Combine(directory, "status.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(firstEvent + nextEvent))))
            await RuntimeService.DrainOutputAsync(store, id, reader, "stdout.jsonl", true, adapter, new StringBuilder(), CancellationToken.None);
        var raw = File.ReadAllText(Path.Combine(directory, "stdout.jsonl"));
        var warning = store.ReadJson(Path.Combine(directory, "output-warning.json"));
        CoreScenarios.Check(raw == firstEvent + nextEvent && adapter.SessionId == "fixture-session", "A locked display snapshot cannot stop raw output persistence or event interpretation");
        CoreScenarios.Check(warning is not null && warning["stage"]?.GetValue<string>() == "updating task status" && warning["fatal"]?.GetValue<bool>() == false && warning["exceptionType"] is not null,
            "A transient snapshot failure retains its precise nonfatal processing stage and exception type");
        CoreScenarios.Check(store.ReadStatus(id)["state"]?.GetValue<string>() == "accepted", "A display snapshot warning cannot invent a terminal task failure");
        using (var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(nextEvent))))
            await RuntimeService.DrainOutputAsync(store, id, reader, "stdout.jsonl", true, adapter, new StringBuilder(), CancellationToken.None);
        CoreScenarios.Check(store.ReadStatus(id)["latestProgress"]?.GetValue<string>() == "saved output 中文", "The next output pump updates status again after the external snapshot handle is released");

        var failingId = Create(store, root, "codex", cli, "failed pipe fixture");
        using (var reader = new FaultAfterReadReader(firstEvent))
        {
            try
            {
                await RuntimeService.DrainOutputAsync(store, failingId, reader, "stdout.jsonl", true, new CliAdapter("codex"), new StringBuilder(), CancellationToken.None);
                throw new InvalidOperationException("The fixture pipe failure must be surfaced.");
            }
            catch (RuntimeService.CliOutputException exception)
            {
                CoreScenarios.Check(exception.Details["stage"]?.GetValue<string>() == "reading CLI pipe" && exception.Details["stream"]?.GetValue<string>() == "stdout" &&
                    exception.InnerException is IOException && exception.Message.Contains("[REDACTED]") && !exception.Message.Contains("ghp_"),
                    "Fatal pump errors distinguish pipe reads from persistence and retain a redacted original cause");
            }
        }
        var diagnostic = store.ReadJson(Path.Combine(store.RunDirectory(failingId), "output-error.json"));
        CoreScenarios.Check(diagnostic is not null && diagnostic["fatal"]?.GetValue<bool>() == true && diagnostic["hresult"] is not null && !diagnostic.ToJsonString().Contains("ghp_"),
            "A fatal pump's stream, stage, HResult, and redacted message survive in the saved diagnostic");
        CoreScenarios.Check(File.ReadAllText(Path.Combine(store.RunDirectory(failingId), "stdout.jsonl")) == firstEvent,
            "Complete output before a later pipe error remains readable");

        var blockedId = Create(store, root, "codex", cli, "raw log failure fixture");
        Directory.CreateDirectory(Path.Combine(store.RunDirectory(blockedId), "stdout.jsonl"));
        using (var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(firstEvent))))
        {
            try
            {
                await RuntimeService.DrainOutputAsync(store, blockedId, reader, "stdout.jsonl", true, new CliAdapter("codex"), new StringBuilder(), CancellationToken.None);
                throw new InvalidOperationException("An unusable raw log path must fail.");
            }
            catch (RuntimeService.CliOutputException exception)
            {
                CoreScenarios.Check(exception.Details["stage"]?.GetValue<string>() == "opening saved log", "An actual log-storage failure remains fatal and is not mislabeled as a parser or pipe error");
            }
        }
        var cappedId = Create(store, root, "codex", cli, "output loss workflow fixture");
        var completeWorkflow = new JsonObject
        {
            ["schemaVersion"] = 2, ["outcome"] = "completed", ["phase"] = "reporting", ["summary"] = "Local checks completed before log loss.",
            ["findings"] = new JsonArray(), ["artifacts"] = new JsonArray(new JsonObject { ["path"] = "retained.txt", ["label"] = "Retained evidence" }),
            ["validation"] = new JsonArray(WorkflowResult.RequiredChecks("issue-fix").Select(check => (JsonNode?)new JsonObject
            {
                ["id"] = check, ["name"] = check, ["status"] = "passed", ["required"] = true, ["details"] = "Offline fixture evidence.", ["evidence"] = new JsonArray("Recorded fixture evidence.")
            }).ToArray()),
            ["diagnostics"] = new JsonArray(), ["nextActions"] = new JsonArray(new JsonObject { ["kind"] = "inspectResult", ["reason"] = "Inspect evidence.", ["body"] = "" }),
            ["review"] = null, ["needsReview"] = false
        };
        var finalEvent = new JsonObject { ["type"] = "item.completed", ["item"] = new JsonObject { ["type"] = "agent_message", ["text"] = completeWorkflow.ToJsonString() } }.ToJsonString() + "\n";
        using (var fullLog = new FileStream(Path.Combine(store.RunDirectory(cappedId), "stdout.jsonl"), FileMode.Create, FileAccess.Write)) fullLog.SetLength(8 * 1024 * 1024);
        var cappedAdapter = new CliAdapter("codex");
        using (var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(finalEvent))))
            await RuntimeService.DrainOutputAsync(store, cappedId, reader, "stdout.jsonl", true, cappedAdapter, new StringBuilder(), CancellationToken.None);
        var cappedResult = cappedAdapter.Result("succeeded", 0, null, null, store.ReadTask(cappedId)["task"]!.AsObject());
        CoreScenarios.Check(!cappedAdapter.OutputIsComplete && cappedResult["outcome"]!.GetValue<string>() == "blocked" && cappedResult["artifacts"]!.AsArray().Count == 1 &&
            cappedResult["diagnostics"]!.AsArray().OfType<JsonObject>().Any(row => row["code"]!.GetValue<string>() == "OUTPUT_INCOMPLETE"),
            "Actual raw-log loss blocks workflow completion after exit zero while retaining structured evidence and artifacts");
        foreach (var fixtureId in new[] { id, failingId, blockedId, cappedId })
            store.Complete(fixtureId, "cancelled", null, CliAdapter.Fallback("cancelled", null, "Synthetic output-pump fixture completed."));
    }

    private sealed class FaultAfterReadReader(string initial) : StreamReader(Stream.Null)
    {
        private bool delivered;
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (delivered) return ValueTask.FromException<int>(new IOException("Fixture pipe read failed with credential ghp_1234567890abcdef1234567890abcdef123456"));
            initial.AsMemory().CopyTo(buffer);
            delivered = true;
            return ValueTask.FromResult(initial.Length);
        }
    }

    private static async Task DetachedCompletion(Store store, string root, string agent, string cli)
    {
        var id = Create(store, root, agent, cli, "Unicode 中文 \"quotes\" $(literal) `ticks` " + new string('x', 20000));
        var saved = store.ReadTask(id);
        saved["promptTemplate"] = new JsonObject { ["name"] = "fixture", ["body"] = "LOCAL_SELECTED_TEMPLATE_MARKER_" + agent, ["sha"] = "fixture-sha", ["schemaVersion"] = 2 };
        store.WriteJson(Path.Combine(store.RunDirectory(id), "task.json"), saved);
        using var launcher = StartLauncher(store, id);
        var ready = await launcher.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
        if (ready is not ("worker-started:detached" or "worker-started:guarded-fixture")) throw new Exception("Worker launcher failed: " + await launcher.StandardError.ReadToEndAsync());
        if (ready == "worker-started:guarded-fixture") Console.WriteLine("NOTE: " + agent + " BACKGROUND_JOB_RESTRICTED guard confirmed. Full job escape is NOT verified; remaining worker assertions use the test-only supervised launcher.");
        else Console.WriteLine("PASS: " + agent + " worker escaped launcher job.");
        CoreScenarios.Check(true, agent + " worker startup handshake");
        var originalWorker = store.ReadStatus(id)["worker"]!.DeepClone();
        launcher.Kill(entireProcessTree: false); // Emulate Chromium terminating only its connection host.
        await launcher.WaitForExitAsync();
        await Until(() => store.ReadStatus(id)["state"]?.GetValue<string>() == "running", 20);
        var sequence = store.LastEventSequence(id);
        await Task.Delay(700);
        CoreScenarios.Check(store.LastEventSequence(id) > sequence, agent + " continues writing output after connection host is killed");
        await Until(() => RunLogs.Read(store, id, "stdout")["text"]!.GetValue<string>().Contains("stdout-marker-") && RunLogs.Read(store, id, "stderr")["text"]!.GetValue<string>().Contains("stderr-marker-"), 10);
        var liveStdout = RunLogs.Read(store, id, "stdout");
        var liveStderr = RunLogs.Read(store, id, "stderr");
        CoreScenarios.Check(liveStdout["text"]!.GetValue<string>().Contains("fixture.raw") && !liveStdout["text"]!.GetValue<string>().Contains("ghp_") && !liveStderr["text"]!.GetValue<string>().Contains("ghp_"), agent + " retains redacted actual stdout JSON and stderr while disconnected");
        CoreScenarios.Check(!store.Events(id, 0, 100).ToJsonString().Contains("stdout-marker-"), agent + " raw-only JSON fields are available separately from normalized activity");
        RuntimeService.Reconcile(store, id);
        CoreScenarios.Check(JsonNode.DeepEquals(originalWorker, store.ReadStatus(id)["worker"]), agent + " reconnect preserves worker identity");
        await Until(() => !Protocol.IsActive(store.ReadStatus(id)["state"]?.GetValue<string>()), 20);
        var status = store.ReadStatus(id);
        if (status["state"]?.GetValue<string>() != "succeeded" || status["exitCode"]?.GetValue<int>() != 0)
            throw new Exception(agent + " worker did not complete successfully: " + status.ToJsonString() + " result: " + store.ReadResult(id)?.ToJsonString());
        CoreScenarios.Check(true, agent + " worker terminal state and exit code");
        CoreScenarios.Check(store.ReadResult(id)?["summary"]?.GetValue<string>().Contains("Fixture completed") == true, agent + " fragmented JSONL result persisted");
        CoreScenarios.Check(store.ReadResult(id)?["outcome"]?.GetValue<string>() == "completed", agent + " detached worker publishes completed workflow only after structured required checks");
        CoreScenarios.Check(store.ReadView(id)["read"]!.GetValue<bool>() == false, agent + " completed while disconnected remains unread");
        var finalStdout = RunLogs.Read(store, id, "stdout", liveStdout["nextCursor"]!.GetValue<long>());
        var finalStderr = RunLogs.Read(store, id, "stderr", liveStderr["nextCursor"]!.GetValue<long>());
        CoreScenarios.Check(finalStdout["text"]!.GetValue<string>().Contains("stdout-marker-11") && finalStderr["text"]!.GetValue<string>().Contains("stderr-marker-11") && finalStdout["eof"]!.GetValue<bool>() && finalStderr["eof"]!.GetValue<bool>(), agent + " incremental raw logs retain output produced through completion without a live connection");
        var folder = store.ReadTask(id)["config"]!["repoFolder"]!.GetValue<string>();
        var received = await File.ReadAllTextAsync(Path.Combine(folder, "received-prompt.txt"));
        CoreScenarios.Check(received.Contains("$(literal)") && received.Contains(new string('x', 20000)), agent + " long prompt reaches stdin without shell interpolation");
        CoreScenarios.Check(received.Contains("LOCAL_SELECTED_TEMPLATE_MARKER_" + agent), agent + " the worker executes the saved local prompt snapshot");
        RuntimeService.Reconcile(store, id);
        CoreScenarios.Check(store.ReadStatus(id)["state"]!.GetValue<string>() == "succeeded", agent + " finished process is not misclassified as interrupted");
    }

    private static async Task Cancellation(Store store, string root, string cli)
    {
        var id = Create(store, root, "codex", cli, "fixture-long fixture-child");
        StartTestWorker(store, id);
        var folder = store.ReadTask(id)["config"]!["repoFolder"]!.GetValue<string>();
        await Until(() => File.Exists(Path.Combine(folder, "child-heartbeat.txt")), 20);
        var child = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "child-identity.json")))!.AsObject();
        RuntimeService.RequestCancel(store, id);
        await Until(() => !Protocol.IsActive(store.ReadStatus(id)["state"]?.GetValue<string>()), 15);
        CoreScenarios.Check(store.ReadStatus(id)["state"]!.GetValue<string>() == "cancelled", "Explicit cancel persists cancelled state");
        CoreScenarios.Check(!SameProcess(child), "Cancellation terminates CLI descendants");
        CoreScenarios.Check(File.Exists(Path.Combine(folder, "received-prompt.txt")), "Cancellation preserves existing file changes");
    }

    private static async Task NoTaskTimeout(Store store, string root, string cli)
    {
        var id = Create(store, root, "codex", cli, "fixture-long", 1);
        StartTestWorker(store, id);
        await Until(() => store.ReadStatus(id)["state"]?.GetValue<string>() == "running", 20);
        await Task.Delay(2200);
        CoreScenarios.Check(store.ReadStatus(id)["state"]?.GetValue<string>() == "running", "Legacy timeout settings cannot stop an active task");
        RuntimeService.RequestCancel(store, id);
        await Until(() => !Protocol.IsActive(store.ReadStatus(id)["state"]?.GetValue<string>()), 15);
        CoreScenarios.Check(store.ReadStatus(id)["state"]?.GetValue<string>() == "cancelled", "Tasks without a deadline still support explicit cancellation");
    }

    private static async Task Failure(Store store, string root, string cli)
    {
        var id = Create(store, root, "codex", cli, "fixture-fail");
        StartTestWorker(store, id);
        await Until(() => !Protocol.IsActive(store.ReadStatus(id)["state"]?.GetValue<string>()), 20);
        var status = store.ReadStatus(id);
        CoreScenarios.Check(status["state"]?.GetValue<string>() == "failed" && status["exitCode"]?.GetValue<int>() == 7, "CLI failure keeps real exit code");
        CoreScenarios.Check(status["error"]?["code"]?.GetValue<string>() == "PERMISSION_DENIED", "CLI permission denial has actionable classification");
    }

    private static void Recovery(Store store, string root, string cli)
    {
        var id = Create(store, root, "codex", cli, "never started");
        var status = store.ReadStatus(id); status["createdAt"] = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O");
        store.WriteJson(Path.Combine(store.RunDirectory(id), "status.json"), status);
        RuntimeService.Reconcile(store, id);
        CoreScenarios.Check(store.ReadStatus(id)["state"]?.GetValue<string>() == "interrupted", "Unstarted accepted task after crash is interrupted without re-execution");
        var uncertain = Create(store, root, "codex", cli, "uncertain identity");
        status = store.ReadStatus(uncertain); status["worker"] = new JsonObject { ["pid"] = 1, ["startTimeUtc"] = "invalid" };
        store.WriteJson(Path.Combine(store.RunDirectory(uncertain), "status.json"), status);
        RuntimeService.Reconcile(store, uncertain);
        CoreScenarios.Check(store.ReadStatus(uncertain)["state"]?.GetValue<string>() == "accepted", "Unverifiable identity is never blindly terminated or restarted");
        status.Remove("worker"); status["createdAt"] = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O");
        store.WriteJson(Path.Combine(store.RunDirectory(uncertain), "status.json"), status);
        RuntimeService.Reconcile(store, uncertain);
    }

    private static string Create(Store store, string root, string agent, string cli, string prompt, int timeout = 60)
    {
        var id = Guid.NewGuid().ToString("D");
        var folder = Path.Combine(root, "repo 空格 " + id); Directory.CreateDirectory(folder);
        store.CreateRun(id, new JsonObject { ["requestId"] = id, ["actionId"] = "fixture", ["actionKind"] = "issue-fix", ["repository"] = "owner/repo", ["prompt"] = prompt },
            new JsonObject { ["agent"] = agent, ["cliPath"] = cli, ["repoFolder"] = folder, ["repositoryKey"] = id, ["permission"] = "workspace-write", ["timeoutSeconds"] = timeout, ["githubAccount"] = "" }, Protocol.ProductionOrigin);
        return id;
    }
    private static Process StartLauncher(Store store, string id)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--fixture-launcher"); start.ArgumentList.Add(store.Root); start.ArgumentList.Add(id);
        return Process.Start(start)!;
    }
    // This fixture-only fallback cannot be used by the product. Codex's command execution Job
    // can forbid breakaway; still exercise worker I/O and cancellation under its supervising Job.
    internal static bool StartTestWorker(Store store, string id)
    {
        try { RuntimeService.StartWorker(store, id); return true; }
        catch (ProtocolException e) when (e.Code == "BACKGROUND_JOB_RESTRICTED")
        {
            using var held = store.AcquireLock("run-" + id);
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--worker"); start.ArgumentList.Add(id); start.ArgumentList.Add("--data-root"); start.ArgumentList.Add(store.Root);
            using var worker = Process.Start(start)!;
            var status = store.ReadStatus(id);
            status["worker"] = new JsonObject { ["pid"] = worker.Id, ["startTimeUtc"] = worker.StartTime.ToUniversalTime().ToString("O") };
            store.WriteJson(Path.Combine(store.RunDirectory(id), "status.json"), status);
            return false;
        }
    }
    private static async Task Until(Func<bool> condition, int seconds)
    {
        var deadline = Environment.TickCount64 + seconds * 1000L;
        while (!condition()) { if (Environment.TickCount64 >= deadline) throw new TimeoutException("Timed out waiting for worker state"); await Task.Delay(100); }
    }
    private static bool SameProcess(JsonObject identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity["pid"]!.GetValue<int>());
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == DateTimeOffset.Parse(identity["startTimeUtc"]!.GetValue<string>()).UtcTicks;
        }
        catch (ArgumentException) { return false; }
    }
}
