using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Pulse.Host;

public static partial class RuntimeService
{
    internal const string AgentTestPrompt = "what's your model";

    public static async Task<JsonObject> StartAgentTestAsync(Store store, string agent, string? cliPath = null)
    {
        if (!SupportedAgents.Contains(agent)) throw new ProtocolException("CLI_NOT_SUPPORTED", "Only Codex and Copilot are supported.");
        var selectedPath = await DiagnosticPathAsync(store, agent, cliPath);
        using var admission = store.AcquireLock("accept");
        store.EnsureAvailable();
        var id = Guid.NewGuid().ToString("D");
        var directory = AgentTestDirectory(store, id);
        using var held = store.AcquireLock("agent-test-" + id);
        var status = new JsonObject { ["testId"] = id, ["agent"] = agent, ["cliPath"] = selectedPath, ["path"] = selectedPath, ["state"] = "running", ["phase"] = "starting", ["createdAt"] = Now(), ["updatedAt"] = Now() };
        store.WriteJson(Path.Combine(directory, "status.json"), status);
        try
        {
            var executable = Environment.ProcessPath ?? throw new IOException("Cannot locate Pulse Host executable.");
            var arguments = new List<string>();
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) arguments.Add(typeof(RuntimeService).Assembly.Location);
            arguments.AddRange(["--agent-test", id, "--data-root", store.Root]);
            using var worker = WindowsProcess.DiagnosticWorker(executable, arguments);
            status["worker"] = Identity(worker);
            store.WriteJson(Path.Combine(directory, "status.json"), status);
            worker.Resume();
        }
        catch (Exception exception) when (exception is ProtocolException or IOException or Win32Exception or InvalidOperationException)
        {
            status["state"] = "failed"; status["success"] = false;
            status["error"] = Error(exception is ProtocolException protocol ? protocol.Code : "AGENT_TEST_START_FAILED", OutputRedactor.Redact(exception.Message));
            status["updatedAt"] = Now();
            store.WriteJson(Path.Combine(directory, "status.json"), status);
        }
        return PublicAgentTest(status);
    }

    public static JsonObject GetAgentTest(Store store, string testId)
    {
        var directory = AgentTestDirectory(store, testId);
        using var held = store.AcquireLock("agent-test-" + testId);
        var status = store.ReadJson(Path.Combine(directory, "status.json")) ?? throw new ProtocolException("AGENT_TEST_NOT_FOUND", "This agent test could not be found.");
        if (CliAdapter.String(status, "state") == "running")
        {
            var workerAlive = IsSameProcess(status["worker"] as JsonObject);
            var recentStartup = status["worker"] is null && DateTimeOffset.TryParse(CliAdapter.String(status, "createdAt"), out var created) && DateTimeOffset.UtcNow - created < TimeSpan.FromSeconds(30);
            if (workerAlive == false && !recentStartup)
            {
                var cli = status["cli"] as JsonObject;
                var cliAlive = IsSameProcess(cli);
                if (cliAlive == false || cliAlive == true && KillVerifiedProcess(cli!))
                {
                    var cancelled = File.Exists(Path.Combine(directory, "cancel.json"));
                    status["state"] = cancelled ? "cancelled" : "failed";
                    status["success"] = false;
                    status["error"] = Error(cancelled ? "CANCELLED" : "AGENT_TEST_INTERRUPTED", cancelled ? "The agent test was cancelled." : "The test process ended without a complete model response. Start another test.");
                    status["updatedAt"] = Now();
                    store.WriteJson(Path.Combine(directory, "status.json"), status);
                }
            }
        }
        return PublicAgentTest(status);
    }

    public static JsonObject CancelAgentTest(Store store, string testId)
    {
        var directory = AgentTestDirectory(store, testId);
        using (var held = store.AcquireLock("agent-test-" + testId))
        {
            var status = store.ReadJson(Path.Combine(directory, "status.json")) ?? throw new ProtocolException("AGENT_TEST_NOT_FOUND", "This agent test could not be found.");
            if (CliAdapter.String(status, "state") == "running")
                store.WriteJson(Path.Combine(directory, "cancel.json"), new JsonObject { ["requestedAt"] = Now() });
        }
        return GetAgentTest(store, testId);
    }

    public static bool HasActiveAgentTests(Store store)
    {
        var root = Path.Combine(store.Root, "agent-tests");
        if (!Directory.Exists(root)) return false;
        return Directory.EnumerateDirectories(root).Select(Path.GetFileName)
            .Where(name => Guid.TryParseExact(name, "D", out _))
            .Any(id => CliAdapter.String(GetAgentTest(store, id!), "state") == "running");
    }

    public static async Task<int> RunAgentTestWorkerAsync(Store store, string testId)
    {
        var directory = AgentTestDirectory(store, testId);
        using var current = Process.GetCurrentProcess();
        var startupDeadline = Environment.TickCount64 + 30_000;
        JsonObject status;
        while (true)
        {
            status = store.ReadJson(Path.Combine(directory, "status.json")) ?? throw new ProtocolException("AGENT_TEST_NOT_FOUND", "This agent test could not be found.");
            if (CliAdapter.String(status, "state") != "running") return 1;
            if (status["worker"] is JsonObject recorded)
            {
                if (recorded["pid"]?.GetValue<int>() != current.Id || IsSameProcess(recorded) != true) return 1;
                break;
            }
            // This is only the launcher's identity handoff, never a model execution deadline.
            if (Environment.TickCount64 >= startupDeadline) return 1;
            await Task.Delay(50);
        }
        var result = await ExecuteAgentTestAsync(Required(status, "agent"), CliAdapter.String(status, "cliPath"), CancellationToken.None,
            () => File.Exists(Path.Combine(directory, "cancel.json")),
            (cli, probe) =>
            {
                using var held = store.AcquireLock("agent-test-" + testId);
                var progress = store.ReadJson(Path.Combine(directory, "status.json"))!;
                progress["cli"] = Identity(cli); progress["path"] = probe["path"]?.DeepClone(); progress["version"] = probe["version"]?.DeepClone();
                progress["phase"] = "waiting_for_response"; progress["updatedAt"] = Now();
                store.WriteJson(Path.Combine(directory, "status.json"), progress);
            });
        using (var held = store.AcquireLock("agent-test-" + testId))
        {
            status = store.ReadJson(Path.Combine(directory, "status.json"))!;
            foreach (var pair in result) status[pair.Key] = pair.Value?.DeepClone();
            status["updatedAt"] = Now(); status["endedAt"] = Now(); status.Remove("phase");
            store.WriteJson(Path.Combine(directory, "status.json"), status);
        }
        return result["success"]?.GetValue<bool>() == true ? 0 : 1;
    }

    // Only an explicit UI test or command-line acceptance invocation calls this method.
    // Discovery and configuration saves never send a model request.
    public static async Task<JsonObject> TestAgentAsync(Store store, string agent, CancellationToken cancellationToken = default, string? cliPath = null)
    {
        try
        {
            var selectedPath = await DiagnosticPathAsync(store, agent, cliPath);
            return await ExecuteAgentTestAsync(agent, selectedPath, cancellationToken, () => false, null);
        }
        catch (ProtocolException error)
        {
            return new JsonObject { ["agent"] = agent, ["success"] = false, ["state"] = "failed", ["reply"] = "", ["error"] = error.ToJson() };
        }
    }

    private static async Task<string> DiagnosticPathAsync(Store store, string agent, string? path)
    {
        if (path is not null) return await CliSelections.ValidateChoiceAsync(agent, path);
        var probe = await CliSelections.ProbeSelectedAsync(new Configuration(store).Read(), agent);
        CliSelections.RequireAvailable(probe);
        return Protocol.RequiredString(probe, "path", 4096);
    }

    internal static IReadOnlyList<string> AgentTestArguments(string agent) => agent switch
    {
        "codex" => ["exec", "--json", "--color", "never", "--sandbox", "read-only", "-c", "approval_policy=\"never\"", "--ephemeral", "--skip-git-repo-check", "-"],
        "copilot" => ["--output-format=json", "--stream=on", "--no-ask-user", "--no-color", "--no-auto-update", "--no-remote-export", "--disable-builtin-mcps", "--no-custom-instructions", "--available-tools=", "--deny-tool=shell", "--deny-tool=write", "--deny-tool=url", "--prompt", AgentTestPrompt],
        _ => throw new ProtocolException("CLI_NOT_SUPPORTED", "Only Codex and Copilot are supported.")
    };

    private static async Task<JsonObject> ExecuteAgentTestAsync(string agent, string? selectedPath, CancellationToken token, Func<bool> cancelled, Action<WindowsProcess, JsonObject>? onStarted)
    {
        var result = new JsonObject { ["agent"] = agent, ["success"] = false, ["state"] = "failed", ["reply"] = "" };
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var directory = Path.Combine(tempRoot, "PulseAgentTest-" + Guid.NewGuid().ToString("N"));
        WindowsProcess? cli = null;
        Task? stdout = null, stderr = null, input = null;
        using var drainCancellation = new CancellationTokenSource();
        var adapter = new CliAdapter(agent);
        var diagnostics = new StringBuilder();
        try
        {
            ThrowIfCancelled();
            if (string.IsNullOrWhiteSpace(selectedPath))
                throw new ProtocolException("CLI_SELECTION_REQUIRED", "This diagnostic has no saved CLI installation.", "Select an available CLI in extension Settings and start another test.");
            var probe = await ProbeAgentAsync(agent, selectedPath);
            result["path"] = probe["path"]?.DeepClone(); result["version"] = probe["version"]?.DeepClone();
            if (probe["available"]?.GetValue<bool>() != true) { result["error"] = probe["error"]?.DeepClone(); return result; }
            ThrowIfCancelled();
            Directory.CreateDirectory(directory);
            cli = WindowsProcess.Cli(Required(probe, "path"), AgentTestArguments(agent), directory);
            onStarted?.Invoke(cli, probe);
            cli.Resume();
            stdout = Drain(cli.Output!, true); stderr = Drain(cli.Error!, false);
            input = WritePrompt(cli.Input!, agent == "codex" ? AgentTestPrompt : "");
            while (!cli.HasExited)
            {
                ThrowIfCancelled();
                if (stdout.IsFaulted || stderr.IsFaulted) throw new IOException("The agent test response could not be read.", stdout.Exception ?? stderr.Exception);
                await Task.Delay(100, token);
            }
            // The model has exited. Close lingering descendants before draining inherited pipes.
            cli.Kill();
            await Task.WhenAll(stdout, stderr, input).WaitAsync(TimeSpan.FromSeconds(10));
            result["exitCode"] = cli.ExitCode;
            result["reply"] = OutputRedactor.Redact(adapter.AssistantReply ?? "");
            if (cli.ExitCode == 0 && adapter.Failure is null && adapter.OutputIsComplete && !string.IsNullOrWhiteSpace(adapter.AssistantReply))
            {
                result["success"] = true; result["state"] = "succeeded";
            }
            else
            {
                var detail = adapter.Failure ?? diagnostics.ToString().Trim();
                var code = cli.ExitCode == 0 && adapter.Failure is null ? "AGENT_NO_RESPONSE" : ClassifyFailure(detail);
                result["error"] = Error(code, code == "AGENT_NO_RESPONSE" ? "The CLI exited without returning a model text response." : "Agent test failed: " + OutputRedactor.Redact(detail.Length > 2000 ? detail[^2000..] : detail));
            }
        }
        catch (OperationCanceledException)
        {
            result["state"] = "cancelled"; result["error"] = Error("CANCELLED", "The agent test was cancelled.");
            if (cli is not null) try { await cli.StopAsync(); } catch { cli.Kill(); }
        }
        catch (Exception exception)
        {
            result["error"] = Error(exception is ProtocolException protocol ? protocol.Code : "AGENT_TEST_FAILED", OutputRedactor.Redact(exception.Message));
        }
        finally
        {
            cli?.Kill();
            var pumps = new[] { stdout, stderr, input }.OfType<Task>().ToArray();
            try { await Task.WhenAll(pumps).WaitAsync(TimeSpan.FromSeconds(3)); } catch { drainCancellation.Cancel(); }
            cli?.Dispose();
            // This uniquely-created diagnostic folder is always confined to the system temp root.
            var fullDirectory = Path.GetFullPath(directory);
            if (Path.GetDirectoryName(fullDirectory)?.Equals(Path.TrimEndingDirectorySeparator(tempRoot), StringComparison.OrdinalIgnoreCase) == true &&
                Path.GetFileName(fullDirectory).StartsWith("PulseAgentTest-", StringComparison.Ordinal))
                try { if (Directory.Exists(fullDirectory)) Directory.Delete(fullDirectory, recursive: true); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return result;

        void ThrowIfCancelled() { token.ThrowIfCancellationRequested(); if (cancelled()) throw new OperationCanceledException(); }
        async Task Drain(StreamReader reader, bool structured)
        {
            var buffer = new char[4096]; var line = new StringBuilder(); bool overlong = false; int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), drainCancellation.Token)) > 0)
            {
                for (var index = 0; index < count; index++)
                {
                    var character = buffer[index];
                    if (character == '\n') Emit();
                    else if (line.Length < CliAdapter.MaximumLineCharacters) line.Append(character);
                    else overlong = true;
                }
            }
            if (line.Length > 0 || overlong) Emit();
            void Emit()
            {
                var text = OutputRedactor.Redact(line.ToString().TrimEnd('\r')); line.Clear();
                if (overlong) { text = "[CLI output line exceeded the size limit.]"; overlong = false; if (structured) adapter.MarkOutputIncomplete(); }
                if (structured) adapter.Consume(text);
                else lock (diagnostics)
                {
                    diagnostics.AppendLine(text);
                    if (diagnostics.Length > 8192) diagnostics.Remove(0, diagnostics.Length - 8192);
                }
            }
        }
    }

    private static string AgentTestDirectory(Store store, string id) => Path.Combine(store.Root, "agent-tests", Protocol.RunId(id));
    private static JsonObject PublicAgentTest(JsonObject status)
    {
        var result = status.DeepClone().AsObject(); result.Remove("worker"); result.Remove("cli");
        return result;
    }
}
