using System.Text.Json.Nodes;

namespace Pulse.Host;

/// <summary>Executable choices are local settings selected from Host discovery, never webpage input.</summary>
public static class CliSelections
{
    private static readonly string[] Agents = ["codex", "copilot"];

    public static JsonObject Read(JsonNode? input)
    {
        if (input is not null && input is not JsonObject) throw Invalid();
        var value = input as JsonObject ?? new JsonObject();
        Protocol.OnlyKeys(value, Agents);
        var result = new JsonObject();
        foreach (var agent in Agents)
        {
            if (!value.ContainsKey(agent)) { result[agent] = ""; continue; }
            if (value[agent] is not JsonValue node || !node.TryGetValue<string>(out var path)) throw Invalid();
            result[agent] = ValidatePath(path);
        }
        return result;
    }

    public static async Task<JsonObject> ValidateSaveAsync(JsonNode? proposed, JsonObject previous, Func<string, Task<JsonArray>> inventory)
    {
        if (proposed is not JsonObject) throw Invalid();
        var selections = Read(proposed);
        var old = Read(previous);
        foreach (var agent in Agents)
        {
            var path = selections[agent]!.GetValue<string>();
            if (path.Length == 0 || string.Equals(path, old[agent]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase)) continue;
            selections[agent] = await ValidateChoiceAsync(agent, path, inventory);
        }
        return selections;
    }

    public static async Task<string> ValidateChoiceAsync(string agent, string path, Func<string, Task<JsonArray>>? inventory = null)
    {
        RequireAgent(agent);
        path = ValidatePath(path);
        if (path.Length == 0) throw SelectionRequired();
        var rows = await (inventory ?? RuntimeService.ListAgentInstallationsAsync)(agent);
        var selected = rows.OfType<JsonObject>().FirstOrDefault(row => Matches(row, path));
        if (selected?["available"]?.GetValue<bool>() != true) throw SelectionUnavailable();
        return ValidatePath(selected["resolvedPath"]?.GetValue<string>() ?? selected["path"]!.GetValue<string>());
    }

    public static string RequirePath(JsonObject config, string agent)
    {
        RequireAgent(agent);
        var path = Read(config["cliSelections"])[agent]!.GetValue<string>();
        return path.Length == 0 ? throw SelectionRequired() : path;
    }

    public static async Task<JsonObject> ProbeSelectedAsync(JsonObject config, string agent, Func<string, string?, Task<JsonObject>>? probe = null)
    {
        string path;
        try { path = RequirePath(config, agent); }
        catch (ProtocolException selectionError) { return Unavailable(null, selectionError); }
        var result = await (probe ?? RuntimeService.ProbeAgentAsync)(agent, path);
        if (result["available"]?.GetValue<bool>() == true) return result;
        var error = new ProtocolException("CLI_SELECTION_UNAVAILABLE", "The selected CLI executable is no longer available.",
            "Open extension Settings, refresh the detected installations, and choose an existing CLI. Existing tasks keep their original executable path.");
        return Unavailable(path, error);
    }

    public static void RequireAvailable(JsonObject probe)
    {
        if (probe["available"]?.GetValue<bool>() == true) return;
        var error = probe["error"] as JsonObject;
        throw new ProtocolException(error?["code"]?.GetValue<string>() ?? "CLI_SELECTION_UNAVAILABLE",
            error?["message"]?.GetValue<string>() ?? "The selected CLI installation is unavailable.",
            error?["guidance"]?.GetValue<string>() ?? "Select an available CLI in extension Settings.");
    }

    public static async Task<JsonObject> ListAsync(JsonObject config, Func<string, Task<JsonArray>>? inventory = null)
    {
        var selections = Read(config["cliSelections"]);
        var installations = new JsonObject();
        var list = inventory ?? RuntimeService.ListAgentInstallationsAsync;
        var codex = list("codex"); var copilot = list("copilot");
        await Task.WhenAll(codex, copilot);
        foreach (var agent in Agents)
        {
            var rows = (JsonArray)(agent == "codex" ? await codex : await copilot).DeepClone();
            var selected = selections[agent]!.GetValue<string>();
            if (selected.Length > 0 && !rows.OfType<JsonObject>().Any(row => Matches(row, selected)))
                rows.Add(Unavailable(selected, SelectionUnavailable()));
            installations[agent] = rows;
        }
        return new JsonObject { ["installations"] = installations, ["selections"] = selections };
    }

    private static bool Matches(JsonObject row, string path) =>
        string.Equals(row["path"]?.GetValue<string>(), path, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(row["resolvedPath"]?.GetValue<string>(), path, StringComparison.OrdinalIgnoreCase) ||
        row["aliases"] is JsonArray aliases && aliases.OfType<JsonValue>().Any(value => value.TryGetValue<string>(out var alias) && string.Equals(alias, path, StringComparison.OrdinalIgnoreCase));
    private static string ValidatePath(string path)
    {
        if (path.Length == 0) return path;
        if (path.Length > 4096 || path.Any(char.IsControl) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
            !Path.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) throw Invalid();
        return Path.GetFullPath(path);
    }
    private static void RequireAgent(string agent) { if (!Agents.Contains(agent, StringComparer.Ordinal)) throw Invalid(); }
    private static ProtocolException Invalid() => new("INVALID_CLI_SELECTION", "CLI selections must contain only Codex and Copilot absolute executable paths or empty strings.", "Choose an installation detected by extension Settings.");
    private static ProtocolException SelectionRequired() => new("CLI_SELECTION_REQUIRED", "Choose a CLI installation before starting this agent.", "Open extension Settings, refresh the detected installations, and explicitly select one for this agent.");
    private static ProtocolException SelectionUnavailable() => new("CLI_SELECTION_UNAVAILABLE", "The chosen CLI is not an available detected installation.", "Refresh the CLI installation list and choose an available entry in extension Settings.");
    private static JsonObject Unavailable(string? path, ProtocolException error) => new() { ["path"] = path, ["available"] = false, ["version"] = null, ["error"] = error.ToJson() };
}
