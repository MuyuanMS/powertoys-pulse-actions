using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pulse.Host.Tests;

internal static class ExecutionScenarios
{
    internal static async Task RunAllAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PulseExecutionTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new Store(root);
            await DefaultsAndOverridesAsync(store);
            await ReadinessUsesOverridesAsync(store);
            RejectsUnsafeSelections();
            ArgumentsKeepBoundaries();
            Console.WriteLine("PASS execution: per-agent defaults, task overrides, CLI default reset, provenance and model/effort argument boundaries (offline)");
        }
        finally
        {
            var absolute = Path.GetFullPath(root);
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (absolute.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(absolute).StartsWith("PulseExecutionTests-", StringComparison.Ordinal) && Directory.Exists(absolute))
                Directory.Delete(absolute, recursive: true);
        }
    }

    private static async Task DefaultsAndOverridesAsync(Store store)
    {
        var configuration = new Configuration(store);
        var config = configuration.Read();
        Check(config["agentDefaults"]!["codex"]!["model"]!.GetValue<string>() == "" && config["agentDefaults"]!["copilot"]!["reasoningEffort"]!.GetValue<string>() == "", "Legacy settings must resolve to each CLI's own defaults.");
        config["prPrompt"] = ""; config["issuePrompt"] = ""; config["e2ePrompt"] = "";
        config["agentDefaults"] = DefaultChoices();
        await configuration.SaveAsync(config);
        var olderClient = (JsonObject)config.DeepClone();
        olderClient.Remove("agentDefaults");
        await configuration.SaveAsync(olderClient);
        Check(JsonNode.DeepEquals(configuration.Read()["agentDefaults"], DefaultChoices()), "An old settings client must preserve defaults it cannot edit.");

        var selected = ExecutionOptions.Resolve(config);
        Check(selected["agent"]!.GetValue<string>() == "codex" && selected["model"]!.GetValue<string>() == "gpt-6-astra" && selected["reasoningEffort"]!.GetValue<string>() == "ultra", "A normal task inherits the selected CLI's configured model and reasoning effort.");
        Check(selected["executionSource"]!["model"]!.GetValue<string>() == "default", "Inherited values retain their provenance in the execution snapshot.");
        var other = ExecutionOptions.Resolve(config, new JsonObject { ["agent"] = "copilot" });
        Check(other["agent"]!.GetValue<string>() == "copilot" && other["model"]!.GetValue<string>() == "claude-opus-4.6" && other["reasoningEffort"]!.GetValue<string>() == "high", "Changing the task CLI inherits that CLI's defaults instead of the global default CLI's choices.");
        var reset = ExecutionOptions.Resolve(config, new JsonObject { ["model"] = "", ["reasoningEffort"] = "" });
        Check(reset["model"]!.GetValue<string>() == "" && reset["reasoningEffort"]!.GetValue<string>() == "" && reset["executionSource"]!["model"]!.GetValue<string>() == "cli", "Explicit empty overrides select the CLI default without inheriting extension settings.");
        var overrides = new JsonObject { ["agent"] = "codex", ["model"] = "gpt-5.5", ["reasoningEffort"] = "low" };
        var task = TaskRequest(overrides);
        var fingerprint = Protocol.Fingerprint(task);
        var snapshot = ExecutionOptions.Resolve(config, task["execution"]);
        var id = Guid.NewGuid().ToString("D");
        store.CreateRun(id, task, snapshot, Protocol.ProductionOrigin);
        snapshot["model"] = "caller-mutation";
        config["agentDefaults"]!["codex"]!["model"] = "gpt-5.2";
        await configuration.SaveAsync(config);
        var accepted = store.ReadTask(id);
        Check(accepted["config"]!["model"]!.GetValue<string>() == "gpt-5.5" && accepted["config"]!["reasoningEffort"]!.GetValue<string>() == "low" && accepted["config"]!["executionSource"]!["model"]!.GetValue<string>() == "task", "Accepted execution choices and provenance remain frozen after caller and settings mutations.");
        Check(Protocol.Fingerprint(task) == fingerprint && store.FindRequest(task, Protocol.ProductionOrigin) == id, "Resolving defaults must preserve the submitted task and its idempotency identity.");
        var changed = (JsonObject)task.DeepClone();
        changed["execution"]!["model"] = "gpt-5.2";
        Expect("REQUEST_CONFLICT", () => store.FindRequest(changed, Protocol.ProductionOrigin));

        var publicDefaults = ExecutionOptions.PublicDefaults(config);
        Check(publicDefaults.Count == 3 && publicDefaults["defaultAgent"]!.GetValue<string>() == "codex" && !publicDefaults.ToJsonString().Contains("permission", StringComparison.Ordinal) && !publicDefaults.ToJsonString().Contains("githubAccount", StringComparison.Ordinal), "Public defaults expose only agent choices and supported effort values.");
        var dispatcher = new Dispatcher(store);
        var response = await dispatcher.HandleAsync(new JsonObject { ["id"] = "defaults", ["protocolVersion"] = 1, ["type"] = "agents.defaults", ["payload"] = new JsonObject() });
        Check(response["ok"]!.GetValue<bool>() && response["data"]!["defaults"]!["codex"]!["model"]!.GetValue<string>() == "gpt-5.2", "The public defaults protocol must read the latest locally saved selections.");
        var invalid = (JsonObject)config.DeepClone();
        invalid["agentDefaults"]!["copilot"]!["reasoningEffort"] = "ultra";
        await ExpectAsync("INVALID_EXECUTION", () => configuration.SaveAsync(invalid));
        Check(JsonNode.DeepEquals(configuration.Read(), config), "Invalid per-agent defaults must not partially change saved settings.");
    }

    private static async Task ReadinessUsesOverridesAsync(Store store)
    {
        var config = new Configuration(store).Read();
        config["cliSelections"] = new JsonObject { ["codex"] = "C:\\fixture-codex\\codex.exe", ["copilot"] = "C:\\fixture-copilot\\copilot.exe" };
        store.WriteJson(Path.Combine(store.Root, "config.json"), config);
        var agents = new List<string>();
        var readiness = new ActionReadiness(store, agent =>
        {
            agents.Add(agent);
            return Task.FromResult(new JsonObject { ["available"] = true, ["capabilities"] = new JsonObject { ["model"] = true, ["reasoningEffort"] = true } });
        }, checkFolders: _ => Task.CompletedTask);
        var response = await readiness.CheckAsync(new JsonObject { ["actionKind"] = "reproduction-setup", ["execution"] = new JsonObject { ["agent"] = "copilot" } });
        Check(response["ready"]!.GetValue<bool>() && agents.SequenceEqual(new[] { "copilot" }), "Readiness must probe the effective task CLI instead of only the settings default.");
        var unsupported = new ActionReadiness(store, _ => Task.FromResult(new JsonObject { ["available"] = true, ["capabilities"] = new JsonObject { ["model"] = true, ["reasoningEffort"] = false } }), checkFolders: _ => Task.CompletedTask);
        var failure = await unsupported.CheckAsync(new JsonObject { ["actionKind"] = "reproduction-setup", ["execution"] = new JsonObject { ["agent"] = "copilot" } });
        Check(failure["ready"]!.GetValue<bool>(), "Readiness must not impose CLI feature checks; the selected CLI interprets the explicit model and effort options when the user runs it.");
        var cleared = await unsupported.CheckAsync(new JsonObject { ["actionKind"] = "reproduction-setup", ["execution"] = new JsonObject { ["agent"] = "copilot", ["reasoningEffort"] = "" } });
        Check(cleared["ready"]!.GetValue<bool>(), "Explicit CLI-default effort requires no reasoning option capability.");
    }

    private static void RejectsUnsafeSelections()
    {
        foreach (var model in new[] { "--model=evil", "gpt model", "../model", "C:\\model", "https://example.invalid", "gpt\";exec", "gpt$(command)", "gpt\n", "gpt\0", new string('a', 129) })
            Expect("INVALID_EXECUTION", () => Protocol.ValidateTask(TaskRequest(new JsonObject { ["model"] = model }, validate: false)));
        foreach (var extra in new[] { "cliPath", "command", "arguments", "permission", "repoFolder" })
            Expect("INVALID_REQUEST", () => Protocol.ValidateTask(TaskRequest(new JsonObject { [extra] = "override" }, validate: false)));
        foreach (var selection in new JsonNode?[] { null, new JsonArray(), JsonValue.Create("codex"), new JsonObject { ["model"] = 42 }, new JsonObject { ["model"] = null }, new JsonObject { ["agent"] = "other" }, new JsonObject { ["agent"] = "codex", ["reasoningEffort"] = "none" }, new JsonObject { ["agent"] = "copilot", ["reasoningEffort"] = "ultra" } })
            Expect("INVALID_EXECUTION", () => ExecutionOptions.ValidateOverride(selection));
        Expect("INVALID_EXECUTION", () => ExecutionOptions.Resolve(new JsonObject { ["agent"] = "codex" }, new JsonObject { ["reasoningEffort"] = "max" }));
        ExecutionOptions.ValidateValues("codex", "gpt-6-astra", "ultra");
        ExecutionOptions.ValidateValues("copilot", "auto", "max");
    }

    private static void ArgumentsKeepBoundaries()
    {
        var codex = CliAdapter.Arguments("codex", "workspace-write", "C:\\schema path\\schema.json", "gpt-6-astra", "ultra").ToArray();
        Check(codex[Array.IndexOf(codex, "--model") + 1] == "gpt-6-astra" && codex.Contains("model_reasoning_effort=\"ultra\""), "Codex model is one argument and effort uses a JSON-quoted TOML string.");
        Check(codex.Contains("approval_policy=\"never\"") && codex.Contains("workspace-write"), "Choosing a model must not change saved permissions.");
        foreach (var permission in new[] { "workspace-write", "yolo" })
        {
            var copilot = CliAdapter.Arguments("copilot", permission, "unused", "claude-opus-4.6", "max").ToArray();
            Check(copilot[Array.IndexOf(copilot, "--model") + 1] == "claude-opus-4.6" && copilot[Array.IndexOf(copilot, "--reasoning-effort") + 1] == "max", "Copilot receives exact separate model and reasoning arguments in every permission mode.");
        }
        var defaults = CliAdapter.Arguments("codex", "read-only", "schema");
        Check(!defaults.Contains("--model") && !defaults.Any(arg => arg.StartsWith("model_reasoning_effort=", StringComparison.Ordinal)), "CLI-default choices must not emit overriding arguments.");
        Expect("INVALID_EXECUTION", () => CliAdapter.Arguments("codex", "read-only", "schema", "bad model", "high"));
    }

    // Called with the existing offline worker fixtures; no installed model CLI is used.
    internal static async Task VerifyWorkerAsync(Store store, string root, string agent, string cli)
    {
        var id = Guid.NewGuid().ToString("D");
        var folder = Path.Combine(root, "execution fixture " + id);
        Directory.CreateDirectory(folder);
        var task = TaskRequest(new JsonObject { ["agent"] = agent, ["model"] = agent == "codex" ? "gpt-5.5" : "auto", ["reasoningEffort"] = agent == "codex" ? "ultra" : "max" });
        var config = ExecutionOptions.Resolve(new JsonObject { ["agent"] = "codex", ["agentDefaults"] = DefaultChoices() }, task["execution"]);
        config["cliPath"] = cli; config["repoFolder"] = folder; config["repositoryKey"] = id; config["permission"] = "workspace-write";
        store.CreateRun(id, task, config, Protocol.ProductionOrigin,
            new JsonObject { ["schemaVersion"] = 2, ["body"] = "Complete the isolated offline workflow fixture and report its structured evidence." });
        config["model"] = "mutated-after-acceptance";
        RuntimeScenarios.StartTestWorker(store, id);
        var deadline = Environment.TickCount64 + 20_000;
        while (Protocol.IsActive(store.ReadStatus(id)["state"]?.GetValue<string>()))
        {
            if (Environment.TickCount64 >= deadline) throw new TimeoutException("Offline execution-option worker did not finish.");
            await Task.Delay(100);
        }
        Check(store.ReadStatus(id)["state"]!.GetValue<string>() == "succeeded", "The controlled worker must complete with the saved model/effort selection.");
        var workflow = store.ReadResult(id)!;
        Check(workflow["schemaVersion"]!.GetValue<int>() == 2 && workflow["outcome"]!.GetValue<string>() == "completed" &&
            workflow["validation"]!.AsArray().OfType<JsonObject>().Count(row => row["required"]!.GetValue<bool>() && row["status"]!.GetValue<string>() == "passed") == 3,
            "The real worker boundary normalizes the v2 fixture with all Host-required workflow evidence.");
        var arguments = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(Path.Combine(folder, "received-arguments.json")))!;
        Check(arguments[Array.IndexOf(arguments, "--model") + 1] == (agent == "codex" ? "gpt-5.5" : "auto"), "The worker must deliver the accepted model rather than mutable current settings or caller state.");
        Check(agent == "codex" ? arguments.Contains("model_reasoning_effort=\"ultra\"") : arguments[Array.IndexOf(arguments, "--reasoning-effort") + 1] == "max", "The worker must deliver the accepted reasoning effort to the real process argument boundary.");
        Check(store.ReadTask(id)["config"]!["executionSource"]!["reasoningEffort"]!.GetValue<string>() == "task", "The accepted source of execution choices remains visible after completion.");
    }

    private static JsonObject DefaultChoices() => new()
    {
        ["codex"] = new JsonObject { ["model"] = "gpt-6-astra", ["reasoningEffort"] = "ultra" },
        ["copilot"] = new JsonObject { ["model"] = "claude-opus-4.6", ["reasoningEffort"] = "high" }
    };
    private static JsonObject TaskRequest(JsonObject execution, bool validate = true)
    {
        var task = new JsonObject { ["requestId"] = Guid.NewGuid().ToString("D"), ["actionId"] = "execution-fixture", ["actionKind"] = "issue-fix", ["repository"] = Configuration.PowerToysRepository, ["prompt"] = "Inspect this offline fixture.", ["execution"] = execution.DeepClone() };
        return validate ? Protocol.ValidateTask(task) : task;
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Expect(string code, Action action)
    {
        try { action(); }
        catch (ProtocolException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static async Task ExpectAsync(string code, Func<Task<JsonObject>> action)
    {
        try { await action(); }
        catch (ProtocolException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected " + code);
    }
}
