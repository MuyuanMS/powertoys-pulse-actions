using System.Text.Json.Nodes;

namespace Pulse.Host.Tests;

internal static class CliSelectionScenarios
{
    internal static async Task RunAllAsync()
    {
        var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var root = Path.GetFullPath(Path.Combine(temporary, "PulseCliSelectionTests-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            ReadAndPathValidation(root);
            await ValidatesDiscoveredChoicesAsync(root);
            await PreservesIndependentChoicesAsync(root);
            await ConfigurationCompatibilityAsync(root);
            await ProbesOnlyTheSelectedPathAsync(root);
            await ListsSelectionsAndMissingInstallationsAsync(root);
            PublicDefaultsHideLocalPaths(root);
        }
        finally
        {
            Check(string.Equals(Path.GetDirectoryName(root), temporary, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(root).StartsWith("PulseCliSelectionTests-", StringComparison.Ordinal),
                "CLI selection cleanup must remain inside its dedicated temporary directory.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            Check(!Directory.Exists(root), "CLI selection fixtures must be removed after the offline scenarios.");
        }
        Console.WriteLine("PASS CLI selections: explicit installation choices, canonical aliases, settings compatibility, exact probes and private paths (offline)");
    }

    private static void ReadAndPathValidation(string root)
    {
        foreach (var old in new JsonNode?[] { null, new JsonObject() })
            Check(JsonNode.DeepEquals(CliSelections.Read(old), Choices()), "Missing legacy selections must leave both agents unselected.");
        var input = Choices(Path.Combine(root, "nested", "..", "Codex.EXE"));
        var normalized = CliSelections.Read(input);
        Check(normalized["codex"]!.GetValue<string>() == Path.GetFullPath(input["codex"]!.GetValue<string>()) &&
            normalized["copilot"]!.GetValue<string>() == "", "Selection paths normalize absolute native executable names while preserving an independently blank agent.");
        normalized["codex"] = "";
        Check(input["codex"]!.GetValue<string>().Length > 0, "Reading selections must not return caller-owned mutable nodes.");

        foreach (var invalid in new JsonNode?[] { new JsonArray(), JsonValue.Create("codex"), JsonValue.Create(4), JsonValue.Create(true) })
            Expect("INVALID_CLI_SELECTION", () => CliSelections.Read(invalid));
        foreach (var invalid in new JsonNode?[] { null, JsonValue.Create(4), JsonValue.Create(false), new JsonObject(), new JsonArray() })
            Expect("INVALID_CLI_SELECTION", () => CliSelections.Read(new JsonObject { ["codex"] = invalid?.DeepClone() }));
        foreach (var path in new[]
        {
            " ", "codex.exe", ".\\codex.exe", "C:codex.exe", "https://example.invalid/codex.exe",
            Path.Combine(root, "codex.cmd"), Path.Combine(root, "codex.bat"), Path.Combine(root, "codex.ps1"),
            Path.Combine(root, "codex.exe.lnk"), Path.Combine(root, "codex\n.exe"), Path.Combine(root, "codex\0.exe"),
            Path.Combine(root, new string('x', 4097) + ".exe")
        })
            Expect("INVALID_CLI_SELECTION", () => CliSelections.Read(Choices(path)));
        foreach (var key in new[] { "agent", "unknown", "cliPath", "arguments", "Codex" })
            Expect("INVALID_REQUEST", () => CliSelections.Read(new JsonObject { [key] = "" }));
        Expect("INVALID_CLI_SELECTION", () => CliSelections.RequirePath(new JsonObject(), "other"));
    }

    private static async Task ValidatesDiscoveredChoicesAsync(string root)
    {
        var canonical = Exe(root, "physical-codex");
        var discoveredPath = Exe(root, "discovered-codex");
        var alias = Exe(root, "alias-codex");
        var unavailable = Exe(root, "unavailable-codex");
        var unlisted = Exe(root, "unlisted-codex");
        var calls = new List<string>();
        var rows = new JsonArray(Installation(discoveredPath, canonical, [discoveredPath, canonical, alias]),
            Installation(unavailable, available: false));
        Task<JsonArray> Inventory(string agent)
        {
            calls.Add(agent);
            return Task.FromResult(agent == "codex" ? (JsonArray)rows.DeepClone() : new JsonArray());
        }
        foreach (var selected in new[] { discoveredPath, canonical, alias.ToUpperInvariant() })
        {
            calls.Clear();
            var result = await CliSelections.ValidateSaveAsync(Choices(selected), Choices(), Inventory);
            Check(result["codex"]!.GetValue<string>() == canonical && result["copilot"]!.GetValue<string>() == "" &&
                calls.SequenceEqual(new[] { "codex" }), "Available inventory paths, canonical paths and aliases must save the canonical executable without requiring a choice for the other agent.");
        }
        var withoutCanonical = Exe(root, "inventory-path-only");
        var simple = await CliSelections.ValidateSaveAsync(Choices(withoutCanonical), Choices(),
            _ => Task.FromResult(new JsonArray(Installation(withoutCanonical))));
        Check(simple["codex"]!.GetValue<string>() == withoutCanonical, "Inventory rows without a resolved path must retain their discovered executable path.");
        foreach (var rejected in new[] { unavailable, unlisted })
            await ExpectAsync("CLI_SELECTION_UNAVAILABLE", () => CliSelections.ValidateSaveAsync(Choices(rejected), Choices(), Inventory));
        await ExpectAsync("CLI_SELECTION_UNAVAILABLE", () => CliSelections.ValidateSaveAsync(Choices(copilot: alias), Choices(), Inventory));
        await ExpectAsync("INVALID_CLI_SELECTION", () => CliSelections.ValidateSaveAsync(null, Choices(), Inventory));
        await ExpectAsync("INVALID_CLI_SELECTION", () => CliSelections.ValidateSaveAsync(new JsonArray(), Choices(), Inventory));
    }

    private static async Task PreservesIndependentChoicesAsync(string root)
    {
        var missingCodex = Exe(root, "previous-missing-codex");
        var missingCopilot = Exe(root, "previous-missing-copilot");
        var nextCodex = Exe(root, "next-codex");
        var previous = Choices(missingCodex, missingCopilot);
        var before = previous.ToJsonString();
        var calls = new List<string>();
        Task<JsonArray> Inventory(string agent)
        {
            calls.Add(agent);
            return Task.FromResult(agent == "codex" ? new JsonArray(Installation(nextCodex)) : new JsonArray());
        }
        var unchanged = await CliSelections.ValidateSaveAsync(previous, previous, Inventory);
        Check(JsonNode.DeepEquals(unchanged, previous) && calls.Count == 0,
            "Unchanged saved selections remain available for repair even if their installations have disappeared from inventory.");
        var updated = await CliSelections.ValidateSaveAsync(Choices(nextCodex, missingCopilot), previous, Inventory);
        Check(updated["codex"]!.GetValue<string>() == nextCodex && updated["copilot"]!.GetValue<string>() == missingCopilot &&
            calls.SequenceEqual(new[] { "codex" }), "Updating Codex must preserve an unchanged unavailable Copilot choice without revalidating it.");
        calls.Clear();
        var cleared = await CliSelections.ValidateSaveAsync(Choices(copilot: missingCopilot), previous, Inventory);
        Check(cleared["codex"]!.GetValue<string>() == "" && cleared["copilot"]!.GetValue<string>() == missingCopilot && calls.Count == 0,
            "Clearing one selection must be explicit, independent and require no CLI discovery.");
        Check(previous.ToJsonString() == before, "Validating proposed selections must not mutate previously saved choices.");
    }

    private static async Task ConfigurationCompatibilityAsync(string root)
    {
        var store = new Store(Path.Combine(root, "settings"));
        var codex = Exe(root, "settings-codex");
        var copilot = Exe(root, "settings-copilot");
        var calls = new List<string>();
        var inventoryAvailable = true;
        var configuration = new Configuration(store, listInstallations: agent =>
        {
            calls.Add(agent);
            return Task.FromResult(inventoryAvailable ? new JsonArray(Installation(agent == "codex" ? codex : copilot)) : new JsonArray());
        });
        var input = configuration.Read();
        Check(JsonNode.DeepEquals(input["cliSelections"], Choices()), "A configuration created before CLI selection must not silently choose an installation.");
        await ExpectAsync("CLI_SELECTION_REQUIRED", () => configuration.SnapshotAsync(Configuration.PowerToysRepository, Guid.NewGuid().ToString("D")));
        input["prPrompt"] = ""; input["issuePrompt"] = ""; input["e2ePrompt"] = ""; input["reproductionPrompt"] = "";
        var legacy = (JsonObject)input.DeepClone();
        legacy.Remove("cliSelections");
        store.WriteJson(Path.Combine(store.Root, "config.json"), legacy);
        Check(JsonNode.DeepEquals(configuration.Read()["cliSelections"], Choices()), "Reading persisted legacy settings must keep both CLI selections blank.");
        await configuration.SaveAsync(legacy);
        Check(calls.Count == 0, "Saving legacy settings without selections must not auto-discover or select a CLI.");

        input["cliSelections"] = Choices(codex, copilot);
        var saved = await configuration.SaveAsync(input);
        Check(JsonNode.DeepEquals(saved["cliSelections"], Choices(codex, copilot)) && calls.Order().SequenceEqual(new[] { "codex", "copilot" }),
            "Configuration save must validate and persist independent detected choices for both agents.");
        inventoryAvailable = false;
        calls.Clear();
        var olderClient = (JsonObject)saved.DeepClone();
        olderClient.Remove("cliSelections");
        olderClient["permission"] = "workspace-write";
        await configuration.SaveAsync(olderClient);
        var compatible = configuration.Read();
        Check(JsonNode.DeepEquals(compatible["cliSelections"], Choices(codex, copilot)) && calls.Count == 0 &&
            compatible["permission"]!.GetValue<string>() == "workspace-write", "An older client must preserve saved installation choices even when neither is currently discoverable.");
        await configuration.SaveAsync(compatible);
        Check(calls.Count == 0, "An ordinary settings edit must also preserve explicitly unchanged missing selections.");
        var rejected = (JsonObject)compatible.DeepClone();
        rejected["cliSelections"]!["codex"] = Exe(root, "settings-unlisted");
        await ExpectAsync("CLI_SELECTION_UNAVAILABLE", () => configuration.SaveAsync(rejected));
        Check(JsonNode.DeepEquals(configuration.Read(), compatible), "An unavailable new choice must leave all previously saved configuration intact.");
    }

    private static async Task ProbesOnlyTheSelectedPathAsync(string root)
    {
        var codex = Exe(root, "selected-codex");
        var copilot = Exe(root, "selected-copilot");
        var config = new JsonObject { ["agent"] = "codex", ["cliSelections"] = Choices(codex, copilot) };
        var calls = new List<(string Agent, string? Path)>();
        Task<JsonObject> Probe(string agent, string? path)
        {
            calls.Add((agent, path));
            return Task.FromResult(new JsonObject { ["available"] = true, ["path"] = path, ["version"] = "fixture" });
        }
        foreach (var agent in new[] { "codex", "copilot" })
        {
            var blank = await CliSelections.ProbeSelectedAsync(new JsonObject(), agent, Probe);
            Error(blank, "CLI_SELECTION_REQUIRED", null);
            Expect("CLI_SELECTION_REQUIRED", () => CliSelections.RequirePath(new JsonObject(), agent));
        }
        Check(calls.Count == 0, "Blank selections must return CLI_SELECTION_REQUIRED without calling a probe with null or enabling automatic discovery.");
        foreach (var agent in new[] { "codex", "copilot" })
        {
            var selected = CliSelections.RequirePath(config, agent);
            var result = await CliSelections.ProbeSelectedAsync(config, agent, Probe);
            Check(result["available"]?.GetValue<bool>() == true && result["path"]?.GetValue<string>() == selected,
                "Selected CLI availability must use that agent's exact persisted executable.");
            CliSelections.RequireAvailable(result);
        }
        Check(calls.SequenceEqual(new[] { ("codex", (string?)codex), ("copilot", (string?)copilot) }),
            "Probing both agents must pass each exact selection once and never fall back to automatic discovery.");
        var effective = ExecutionOptions.Resolve(config, new JsonObject { ["agent"] = "copilot" });
        Check(CliSelections.RequirePath(config, effective["agent"]!.GetValue<string>()) == copilot,
            "A per-task agent override must require that agent's selection rather than the settings default agent's executable.");
        var blankOverride = (JsonObject)config.DeepClone();
        blankOverride["cliSelections"]!["copilot"] = "";
        Expect("CLI_SELECTION_REQUIRED", () => CliSelections.RequirePath(blankOverride, effective["agent"]!.GetValue<string>()));

        foreach (var underlying in new[] { "CLI_NOT_FOUND", "CLI_EXECUTION_FAILED" })
        {
            calls.Clear();
            var result = await CliSelections.ProbeSelectedAsync(config, "codex", (agent, path) =>
            {
                calls.Add((agent, path));
                return Task.FromResult(new JsonObject { ["available"] = false, ["path"] = path,
                    ["error"] = new JsonObject { ["code"] = underlying, ["message"] = "Synthetic selected installation failure." } });
            });
            var expected = "CLI_SELECTION_UNAVAILABLE";
            Error(result, expected, codex);
            Expect(expected, () => CliSelections.RequireAvailable(result));
            Check(calls.SequenceEqual(new[] { ("codex", (string?)codex) }),
                "A selected installation failure must remain an explicit failure without probing any alternative installation.");
        }
    }

    private static async Task ListsSelectionsAndMissingInstallationsAsync(string root)
    {
        var missing = Exe(root, "list-missing-selected");
        var available = Exe(root, "list-available");
        var alias = Exe(root, "list-alias");
        var copilot = Exe(root, "list-copilot");
        var codexRows = new JsonArray(Installation(available, available, [available, alias]));
        var copilotRows = new JsonArray(Installation(copilot));
        var config = new JsonObject { ["cliSelections"] = Choices(missing, copilot) };
        Task<JsonArray> Inventory(string agent) => Task.FromResult(agent == "codex" ? codexRows : copilotRows);
        var result = await CliSelections.ListAsync(config, Inventory);
        Check(result.Count == 2 && result["installations"]!.AsObject().Count == 2 && result["selections"]!.AsObject().Count == 2,
            "The settings installation list must contain installations and selections for both supported agents.");
        var listedCodex = result["installations"]!["codex"]!.AsArray();
        Check(listedCodex.Count == 2 && result["installations"]!["copilot"]!.AsArray().Count == 1 &&
            JsonNode.DeepEquals(result["selections"], config["cliSelections"]), "Listing must retain an unavailable selected installation and preserve the user's selected paths.");
        Error(listedCodex.OfType<JsonObject>().Single(row => row["path"]?.GetValue<string>() == missing), "CLI_SELECTION_UNAVAILABLE", missing);
        var aliasConfig = new JsonObject { ["cliSelections"] = Choices(alias.ToUpperInvariant()) };
        var aliases = await CliSelections.ListAsync(aliasConfig, Inventory);
        Check(aliases["installations"]!["codex"]!.AsArray().Count == 1,
            "A saved alias matched case-insensitively must not be duplicated as an unavailable synthetic installation.");
        listedCodex[0]!["path"] = "mutated-result";
        result["selections"]!["codex"] = "";
        Check(codexRows.Count == 1 && codexRows[0]!["path"]!.GetValue<string>() == available && config["cliSelections"]!["codex"]!.GetValue<string>() == missing,
            "Installation listing must not mutate the inventory provider's rows or persisted selections.");
        var empty = await CliSelections.ListAsync(new JsonObject(), _ => Task.FromResult(new JsonArray()));
        Check(empty["installations"]!["codex"]!.AsArray().Count == 0 && empty["installations"]!["copilot"]!.AsArray().Count == 0 &&
            JsonNode.DeepEquals(empty["selections"], Choices()), "An empty inventory with legacy blank settings must not manufacture a selected installation.");
    }

    private static void PublicDefaultsHideLocalPaths(string root)
    {
        var config = new JsonObject { ["agent"] = "codex", ["cliSelections"] = Choices(Exe(root, "private-codex"), Exe(root, "private-copilot")) };
        var exposed = ExecutionOptions.PublicDefaults(config);
        var serialized = exposed.ToJsonString();
        Check(!exposed.ContainsKey("cliSelections") && !serialized.Contains("private-codex", StringComparison.OrdinalIgnoreCase) &&
            !serialized.Contains("private-copilot", StringComparison.OrdinalIgnoreCase), "Webpage execution defaults must not expose either agent's local CLI installation paths.");
    }

    private static string Exe(string root, string name) => Path.Combine(root, name, "agent.exe");
    private static JsonObject Choices(string codex = "", string copilot = "") => new() { ["codex"] = codex, ["copilot"] = copilot };
    private static JsonObject Installation(string path, string? resolvedPath = null, string[]? aliases = null, bool available = true)
    {
        var row = new JsonObject { ["path"] = path, ["available"] = available, ["version"] = "fixture", ["source"] = "fixture" };
        if (resolvedPath is not null) row["resolvedPath"] = resolvedPath;
        if (aliases is not null) row["aliases"] = new JsonArray(aliases.Select(alias => (JsonNode?)JsonValue.Create(alias)).ToArray());
        if (!available) row["error"] = new JsonObject { ["code"] = "CLI_NOT_FOUND", ["message"] = "Synthetic missing executable." };
        return row;
    }
    private static void Error(JsonObject result, string code, string? path) => Check(result["available"]?.GetValue<bool>() == false &&
        result["error"]?["code"]?.GetValue<string>() == code && result["path"]?.GetValue<string>() == path,
        "The selected installation must report " + code + " with its retained path.");
    private static void Check(bool condition, string message) => CoreScenarios.Check(condition, message);
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
