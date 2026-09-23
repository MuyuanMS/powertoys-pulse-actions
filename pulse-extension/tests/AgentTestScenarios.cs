using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Pulse.Host.Tests;

internal static class AgentTestScenarios
{
    public static async Task RunAllAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PulseAgentFixture-" + Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(root, "bin"); Directory.CreateDirectory(bin);
        var store = new Store(Path.Combine(root, "data"));
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalMode = Environment.GetEnvironmentVariable("PULSE_AGENT_TEST_FIXTURE_MODE");
        var originalChildFile = Environment.GetEnvironmentVariable("PULSE_AGENT_TEST_CHILD_FILE");
        try
        {
            // Apphosts retain their embedded assembly path; keep fixture copies beside the test DLL.
            var source = Environment.ProcessPath!;
            var sourceDirectory = Path.GetDirectoryName(source)!;
            foreach (var file in Directory.EnumerateFiles(sourceDirectory)) File.Copy(file, Path.Combine(bin, Path.GetFileName(file)));
            File.Copy(source, Path.Combine(bin, "codex.exe"), overwrite: true);
            File.Copy(source, Path.Combine(bin, "copilot.exe"), overwrite: true);
            Environment.SetEnvironmentVariable("PATH", bin);
            Environment.SetEnvironmentVariable("PULSE_AGENT_TEST_FIXTURE_MODE", "response");
            var config = new Configuration(store).Read();
            config["cliSelections"] = new JsonObject { ["codex"] = Path.Combine(bin, "codex.exe"), ["copilot"] = Path.Combine(bin, "copilot.exe") };
            store.WriteJson(Path.Combine(store.Root, "config.json"), config);
            CoreScenarios.Check(RuntimeService.ResolveExecutable("codex") == Path.Combine(bin, "codex.exe"), "Agent discovery finds native executables on PATH without configured paths");
            CoreScenarios.Check(RuntimeService.ResolveExecutable(Path.Combine(bin, "codex.cmd")) is null, "Agent discovery rejects shell wrappers");
            foreach (var agent in new[] { "codex", "copilot" })
            {
                var result = await RuntimeService.TestAgentAsync(store, agent);
                CoreScenarios.Check(result["success"]?.GetValue<bool>() == true && result["reply"]?.GetValue<string>() == "Fixture model response.", agent + " test requires a real assistant response to the exact model question");
            }
            foreach (var mode in new[] { "no-response", "nonzero", "error-event" })
            {
                Environment.SetEnvironmentVariable("PULSE_AGENT_TEST_FIXTURE_MODE", mode);
                var result = await RuntimeService.TestAgentAsync(store, "codex");
                CoreScenarios.Check(result["success"]?.GetValue<bool>() == false, "Agent test rejects " + mode + " as a successful configuration");
            }
            Environment.SetEnvironmentVariable("PULSE_AGENT_TEST_FIXTURE_MODE", "unicode-response");
            var originalLanguage = await RuntimeService.TestAgentAsync(store, "codex");
            CoreScenarios.Check(originalLanguage["reply"]?.GetValue<string>() == "Fixture 模型回复.", "English product UI preserves actual model responses in their original language");
            Environment.SetEnvironmentVariable("PULSE_AGENT_TEST_FIXTURE_MODE", "redaction");
            var redacted = await RuntimeService.TestAgentAsync(store, "codex");
            CoreScenarios.Check(!redacted.ToJsonString().Contains("ghp_1234567890abcdef1234567890abcdef123456"), "Agent test redacts credentials before exposing results");
            Environment.SetEnvironmentVariable("PULSE_AGENT_TEST_FIXTURE_MODE", "hold");
            var childFile = Path.Combine(root, "child.json");
            Environment.SetEnvironmentVariable("PULSE_AGENT_TEST_CHILD_FILE", childFile);
            using (var cancellation = new CancellationTokenSource())
            {
                var pending = RuntimeService.TestAgentAsync(store, "codex", cancellation.Token);
                try
                {
                    await Until(() => File.Exists(childFile));
                    var identity = JsonNode.Parse(await File.ReadAllTextAsync(childFile))!.AsObject();
                    cancellation.Cancel();
                    var result = await pending.WaitAsync(TimeSpan.FromSeconds(15));
                    CoreScenarios.Check(result["state"]?.GetValue<string>() == "cancelled" && !SameProcess(identity), "Agent test cancellation stops its CLI descendants");
                }
                finally { cancellation.Cancel(); await pending.WaitAsync(TimeSpan.FromSeconds(15)); }
            }
            Environment.SetEnvironmentVariable("PULSE_AGENT_TEST_FIXTURE_MODE", "response");
            config["cliSelections"]!["copilot"] = "";
            store.WriteJson(Path.Combine(store.Root, "config.json"), config);
            var started = await RuntimeService.StartAgentTestAsync(store, "copilot", Path.Combine(bin, "copilot.exe"));
            var id = started["testId"]!.GetValue<string>();
            var acceptedTest = store.ReadJson(Path.Combine(store.Root, "agent-tests", id, "status.json"))!;
            CoreScenarios.Check(acceptedTest["cliPath"]!.GetValue<string>().Equals(RuntimeService.ResolveAgentExecutablePath(Path.Combine(bin, "copilot.exe")), StringComparison.OrdinalIgnoreCase) && new Configuration(store).Read()["cliSelections"]!["copilot"]!.GetValue<string>() == "",
                "Testing an unsaved listed installation freezes its exact path without changing the saved selection");
            config["cliSelections"]!["copilot"] = Path.Combine(bin, "missing-after-start.exe");
            store.WriteJson(Path.Combine(store.Root, "config.json"), config);
            await Until(() => RuntimeService.GetAgentTest(store, id)["state"]?.GetValue<string>() != "running");
            CoreScenarios.Check(RuntimeService.GetAgentTest(store, id)["state"]?.GetValue<string>() == "succeeded", "Background agent test keeps its frozen installation after saved selection changes");
            Environment.SetEnvironmentVariable("PULSE_AGENT_TEST_FIXTURE_MODE", "hold");
            File.Delete(childFile);
            started = await RuntimeService.StartAgentTestAsync(store, "codex"); id = started["testId"]!.GetValue<string>();
            await Until(() => File.Exists(childFile));
            RuntimeService.CancelAgentTest(store, id);
            await Until(() => RuntimeService.GetAgentTest(store, id)["state"]?.GetValue<string>() != "running");
            CoreScenarios.Check(RuntimeService.GetAgentTest(store, id)["state"]?.GetValue<string>() == "cancelled", "Background agent test supports explicit cancellation");
        }
        finally
        {
            var testsDirectory = Path.Combine(store.Root, "agent-tests");
            if (Directory.Exists(testsDirectory))
                foreach (var testDirectory in Directory.EnumerateDirectories(testsDirectory))
                {
                    var id = Path.GetFileName(testDirectory);
                    RuntimeService.CancelAgentTest(store, id);
                    try { await Until(() => RuntimeService.GetAgentTest(store, id)["state"]?.GetValue<string>() != "running"); } catch (TimeoutException) { }
                }
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("PULSE_AGENT_TEST_FIXTURE_MODE", originalMode);
            Environment.SetEnvironmentVariable("PULSE_AGENT_TEST_CHILD_FILE", originalChildFile);
            // Only remove the fixture directory created directly under the system temp root.
            if (Path.GetDirectoryName(Path.GetFullPath(root)) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())))
                try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    public static async Task<int> FakeModelAsync(string[] args)
    {
        var codex = args.Contains("exec");
        CoreScenarios.Check(codex ? args.Contains("read-only") && args.Contains("--ephemeral") : args.Contains("--available-tools="), "Diagnostic model invocation uses bounded permissions");
        var mode = Environment.GetEnvironmentVariable("PULSE_AGENT_TEST_FIXTURE_MODE");
        if (mode == "hold")
        {
            var childStart = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            childStart.ArgumentList.Add("--fixture-child");
            using var child = Process.Start(childStart)!;
            var childFile = Environment.GetEnvironmentVariable("PULSE_AGENT_TEST_CHILD_FILE")!;
            await File.WriteAllTextAsync(childFile + ".tmp",
                new JsonObject { ["pid"] = child.Id, ["startTimeUtc"] = child.StartTime.ToUniversalTime().ToString("O") }.ToJsonString());
            File.Move(childFile + ".tmp", childFile, overwrite: true);
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        if (mode == "no-response") { Console.WriteLine("CLI installed; --version 0.145.0"); return 0; }
        var reply = mode == "redaction" ? "Fixture ghp_1234567890abcdef1234567890abcdef123456" : mode == "unicode-response" ? "Fixture 模型回复." : "Fixture model response.";
        Console.WriteLine((codex ? new JsonObject { ["type"] = "item.completed", ["item"] = new JsonObject { ["type"] = "agent_message", ["text"] = reply } }
            : new JsonObject { ["type"] = "assistant.message", ["data"] = new JsonObject { ["content"] = reply } }).ToJsonString());
        if (mode == "error-event") Console.WriteLine("{\"type\":\"error\",\"message\":\"authentication failed\"}");
        return mode == "nonzero" ? 7 : 0;
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 20_000;
        while (!condition()) { if (Environment.TickCount64 >= deadline) throw new TimeoutException("Agent test fixture did not reach expected state."); await Task.Delay(100); }
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
