using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pulse.Host;

/// <summary>Bounded model choices only; execution paths and permissions remain local configuration.</summary>
public static partial class ExecutionOptions
{
    private static readonly string[] Agents = ["codex", "copilot"];
    private static readonly string[] CodexEfforts = ["minimal", "low", "medium", "high", "xhigh", "ultra"];
    private static readonly string[] CopilotEfforts = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];

    public static JsonObject Defaults(JsonNode? input)
    {
        if (input is not null && input is not JsonObject) throw Invalid("Agent defaults must be an object.");
        var defaults = input as JsonObject ?? new JsonObject();
        Protocol.OnlyKeys(defaults, Agents);
        var result = new JsonObject();
        foreach (var agent in Agents)
        {
            if (defaults.ContainsKey(agent) && defaults[agent] is not JsonObject)
                throw Invalid("Each agent's defaults must be an object.");
            var selected = defaults[agent] as JsonObject ?? new JsonObject();
            Protocol.OnlyKeys(selected, "model", "reasoningEffort");
            var model = Optional(selected, "model");
            var effort = Optional(selected, "reasoningEffort");
            ValidateValues(agent, model, effort);
            result[agent] = new JsonObject { ["model"] = model, ["reasoningEffort"] = effort };
        }
        return result;
    }

    public static JsonObject ValidateOverride(JsonNode? input)
    {
        if (input is not JsonObject selected) throw Invalid("Task execution overrides must be an object.");
        Protocol.OnlyKeys(selected, "agent", "model", "reasoningEffort");
        var agent = selected.ContainsKey("agent") ? Protocol.RequiredString(selected, "agent", 16) : null;
        if (agent is not null && !Agents.Contains(agent, StringComparer.Ordinal)) throw Invalid("Choose Codex or Copilot.");
        ValidateModel(Optional(selected, "model"));
        var effort = Optional(selected, "reasoningEffort");
        if (effort.Length > 0 && !(agent is null ? CodexEfforts.Concat(CopilotEfforts) : Efforts(agent)).Contains(effort, StringComparer.Ordinal))
            throw Invalid("This reasoning effort is not supported for the selected CLI.");
        // Preserve omitted fields and explicit empty strings for request identity and default resolution.
        return (JsonObject)selected.DeepClone();
    }

    public static JsonObject Resolve(JsonObject config, JsonNode? taskExecution = null)
    {
        var selected = taskExecution is null ? new JsonObject() : ValidateOverride(taskExecution);
        var agent = selected.ContainsKey("agent") ? selected["agent"]!.GetValue<string>() : Protocol.RequiredString(config, "agent", 16);
        if (!Agents.Contains(agent, StringComparer.Ordinal)) throw Invalid("Choose Codex or Copilot.");
        var defaults = Defaults(config["agentDefaults"])[agent]!.AsObject();
        var model = selected.ContainsKey("model") ? Optional(selected, "model") : defaults["model"]!.GetValue<string>();
        var effort = selected.ContainsKey("reasoningEffort") ? Optional(selected, "reasoningEffort") : defaults["reasoningEffort"]!.GetValue<string>();
        ValidateValues(agent, model, effort);
        return new JsonObject
        {
            ["agent"] = agent, ["model"] = model, ["reasoningEffort"] = effort,
            ["executionSource"] = new JsonObject
            {
                ["agent"] = selected.ContainsKey("agent") ? "task" : "default",
                ["model"] = model.Length == 0 ? "cli" : selected.ContainsKey("model") ? "task" : "default",
                ["reasoningEffort"] = effort.Length == 0 ? "cli" : selected.ContainsKey("reasoningEffort") ? "task" : "default"
            }
        };
    }

    public static JsonObject PublicDefaults(JsonObject config)
    {
        var agent = Protocol.RequiredString(config, "agent", 16);
        if (!Agents.Contains(agent, StringComparer.Ordinal)) throw Invalid("Choose Codex or Copilot.");
        return new JsonObject
        {
            ["defaultAgent"] = agent, ["defaults"] = Defaults(config["agentDefaults"]),
            ["reasoningEfforts"] = new JsonObject
            {
                ["codex"] = new JsonArray(CodexEfforts.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["copilot"] = new JsonArray(CopilotEfforts.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())
            }
        };
    }

    public static void ValidateValues(string agent, string model, string reasoningEffort)
    {
        if (!Agents.Contains(agent, StringComparer.Ordinal)) throw Invalid("Choose Codex or Copilot.");
        ValidateModel(model);
        if (reasoningEffort.Length > 0 && !Efforts(agent).Contains(reasoningEffort, StringComparer.Ordinal))
            throw Invalid("This reasoning effort is not supported for the selected CLI.");
    }

    private static string[] Efforts(string agent) => agent == "codex" ? CodexEfforts : CopilotEfforts;
    private static void ValidateModel(string value)
    {
        if (value.Length > 0 && !ModelPattern().IsMatch(value))
            throw Invalid("Model identifiers must contain at most 128 letters, digits, dots, underscores, or hyphens and start with a letter or digit.");
    }
    private static string Optional(JsonObject value, string field)
    {
        if (!value.ContainsKey(field)) return "";
        if (value[field] is not JsonValue node || !node.TryGetValue<string>(out var text))
            throw Invalid("Model and reasoning effort values must be text; use an empty string for the CLI default.");
        return text;
    }
    private static ProtocolException Invalid(string message) => new("INVALID_EXECUTION", message, "Choose a supported CLI, model identifier, and reasoning effort. Paths, commands, and permissions cannot be supplied as task overrides.");
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ModelPattern();
}
