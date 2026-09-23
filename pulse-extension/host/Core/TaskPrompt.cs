using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pulse.Host;

public static partial class TaskPrompt
{
    public const string DefaultPrPrompt = "powertoys-pr-loop-review.prompt.md";
    public const string DefaultIssuePrompt = "powertoys-issue-local-fix.prompt.md";
    public const string DefaultE2ePrompt = "powertoys-pr-e2e-test.prompt.md";
    public const string DefaultReproductionPrompt = "powertoys-issue-reproduction-setup.prompt.md";
    public const string DefaultFeatureResearchPrompt = "powertoys-issue-feature-research.prompt.md";
    public const string DefaultBugInvestigationPrompt = "powertoys-issue-bug-investigation.prompt.md";
    public const string DefaultFeatureImplementPrompt = "powertoys-issue-feature-implement.prompt.md";
    public const string DefaultIssueVerifyPrompt = "powertoys-issue-verify.prompt.md";
    public const string BundledReproductionPrompt = "bundled-reproduction-setup";

    public static (string Field, string TargetType) SelectionForAction(string actionKind) => actionKind switch
    {
        "pr-review" => ("prPrompt", "pr"),
        "issue-fix" => ("issuePrompt", "issue"),
        "e2e" or "pr-verify" => ("e2ePrompt", "pr"),
        "reproduction-setup" => ("reproductionPrompt", "issue"),
        "feature-research" => ("featureResearchPrompt", "issue"),
        "bug-investigation" => ("bugInvestigationPrompt", "issue"),
        "feature-implement" => ("featureImplementPrompt", "issue"),
        "issue-verify" => ("issueVerifyPrompt", "issue"),
        _ => throw new ProtocolException("INVALID_REQUEST", "This action type is not supported.")
    };

    public static void ValidateActionSelection(PromptCatalog catalog, JsonObject config, string actionKind)
        => Select(catalog, config, actionKind);

    public static void ValidateSelection(PromptCatalog catalog, string name, string actionKind)
    {
        var (_, targetType) = SelectionForAction(actionKind);
        if (name.Length == 0) return; // Permit saving other settings before choosing a prompt.
        if (name == BundledReproductionPrompt) name = DefaultReproductionPrompt;
        ValidateTemplate(catalog.Get(name), targetType, actionKind);
    }

    public static JsonObject? Snapshot(PromptCatalog catalog, JsonObject task, JsonObject config)
    {
        if (task["target"] is not JsonObject target) return null; // Legacy targetless local tasks.
        var type = Protocol.RequiredString(target, "type");
        var actionKind = Protocol.RequiredString(task, "actionKind", 40);
        if (SelectionForAction(actionKind).TargetType != type)
            throw new ProtocolException("PROMPT_TARGET_MISMATCH", "The selected action does not match the issue or pull request target.");
        var local = Select(catalog, config, actionKind);
        var number = target["number"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture);
        var provided = (task["context"] as JsonObject)?["title"];
        var title = provided is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? new string(text.Where(c => !char.IsControl(c)).Take(256).ToArray())
            : "Title not supplied; read it from the linked GitHub target.";
        // Titles originate from web context: quote them as data rather than inserting control lines.
        var quotedTitle = JsonSerializer.Serialize(title);
        var content = local["content"]!.GetValue<string>();
        var mode = ReviewModes.Mode(task);
        var fragments = new List<JsonObject>();
        if (actionKind is "pr-verify" or "e2e" || actionKind == "pr-review" && mode != "static")
            fragments.Add(catalog.VerificationInstructions());
        // Issue plans and historical reviews do not encode execution steps as typed scope.
        // Their fragment is conditional on actually running PowerToys within the accepted work.
        var mayRunPowerToys = actionKind switch
        {
            "pr-review" => mode is null or "ui-e2e",
            "pr-verify" or "e2e" => mode == "ui-e2e",
            "issue-fix" or "reproduction-setup" or "bug-investigation" or "feature-implement" or "issue-verify" => true,
            _ => false
        };
        if (mayRunPowerToys) fragments.Add(catalog.RuntimeSkillInstructions());
        foreach (var fragment in fragments) content += "\n\n" + fragment["content"]!.GetValue<string>();
        var body = Variables().Replace(content, match => match.Value switch
        {
            "<PRNumber>" or "<IssueNumber>" => number,
            "<PRTitle>" or "<IssueTitle>" => quotedTitle,
            _ => throw new ProtocolException("UNSUPPORTED_PROMPT_VARIABLE", $"The prompt uses an unsupported variable: {match.Value}", "Choose a compatible prompt or update its template support before running it.")
        });
        if (Encoding.UTF8.GetByteCount(body) > PromptCatalog.MaximumFileBytes)
            throw new ProtocolException("PROMPT_TOO_LARGE", "The rendered prompt exceeds the local template size limit.");
        return new JsonObject
        {
            ["name"] = local["name"]!.DeepClone(), ["title"] = local["title"]?.DeepClone(), ["body"] = body,
            ["sha"] = local["sha"]!.DeepClone(), ["revision"] = local["revision"]?.DeepClone(),
            ["hashAlgorithm"] = local["hashAlgorithm"]?.DeepClone(), ["schemaVersion"] = local["schemaVersion"]?.DeepClone(),
            ["requiredChecks"] = new JsonArray(ReviewModes.RequiredChecks(task).Select(check => (JsonNode?)JsonValue.Create(check)).ToArray()),
            ["reviewOptions"] = task["reviewOptions"]?.DeepClone(), ["renderedSha"] = Protocol.Hash(body),
            ["fragments"] = new JsonArray(fragments.Select(fragment => (JsonNode?)new JsonObject
            {
                ["name"] = fragment["name"]!.DeepClone(), ["sha"] = fragment["sha"]!.DeepClone(), ["hashAlgorithm"] = "sha256"
            }).ToArray()),
            ["sourceUrl"] = local["sourceUrl"]?.DeepClone(), ["source"] = local["source"]?.DeepClone() ?? JsonValue.Create("catalog"),
            ["actionKind"] = actionKind, ["appliesTo"] = type, ["capturedAt"] = Protocol.Now()
        };
    }

    private static JsonObject Select(PromptCatalog catalog, JsonObject config, string actionKind)
    {
        var (field, targetType) = SelectionForAction(actionKind);
        var name = actionKind switch
        {
            "feature-research" => DefaultFeatureResearchPrompt,
            "bug-investigation" => DefaultBugInvestigationPrompt,
            "feature-implement" => DefaultFeatureImplementPrompt,
            "issue-verify" => DefaultIssueVerifyPrompt,
            _ => config[field]?.GetValue<string>() ?? ""
        };
        if (actionKind == "reproduction-setup" && (name.Length == 0 || name == BundledReproductionPrompt)) name = DefaultReproductionPrompt;
        if (name.Length == 0)
            throw new ProtocolException("PROMPT_NOT_CONFIGURED", "Choose a bundled prompt for this action before running the task.", "Select the action's prompt in extension Settings and save.");
        var local = catalog.Get(name);
        ValidateTemplate(local, targetType, actionKind);
        return local;
    }

    private static void ValidateTemplate(JsonObject local, string targetType, string actionKind)
    {
        if (targetType is not ("pr" or "issue")) throw new ProtocolException("INVALID_TARGET", "A prompt must be assigned to a pull request or issue.");
        var content = local["content"]!.GetValue<string>();
        foreach (Match match in Variables().Matches(content))
        {
            if (match.Value is not ("<PRNumber>" or "<PRTitle>" or "<IssueNumber>" or "<IssueTitle>"))
                throw new ProtocolException("UNSUPPORTED_PROMPT_VARIABLE", $"The prompt uses an unsupported variable: {match.Value}");
            if ((targetType == "pr" && match.Value.StartsWith("<Issue", StringComparison.Ordinal)) ||
                (targetType == "issue" && match.Value.StartsWith("<PR", StringComparison.Ordinal)))
                throw new ProtocolException("PROMPT_TARGET_MISMATCH", "This prompt belongs to a different target type.", "Choose a PR template for pull requests or an issue template for issues.");
        }
        if (local["actionKind"]?.GetValue<string>() != (actionKind == "pr-verify" ? "e2e" : actionKind))
            throw new ProtocolException("PROMPT_ACTION_MISMATCH", "This bundled prompt belongs to a different action workflow.", "Choose the bundled prompt for this action in extension Settings. A shared PR or issue target type does not make the workflows interchangeable.");
    }

    [GeneratedRegex(@"<[A-Z][A-Za-z0-9_-]*>", RegexOptions.CultureInvariant)]
    private static partial Regex Variables();
}
