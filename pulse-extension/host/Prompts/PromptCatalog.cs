using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pulse.Host;

/// <summary>Repository-owned prompt assets embedded in the Host; independent of accounts and old download caches.</summary>
public sealed class PromptCatalog
{
    public const string SourceUrl = "bundled://Pulse.Host/prompts";
    public const int MaximumFileBytes = 256 * 1024;
    public const int MaximumTotalBytes = 4 * 1024 * 1024;
    public const int MaximumFiles = 100;
    public const string VerificationFragmentName = "pr-verification.shared.prompt.md";
    public const string RuntimeSkillFragmentName = "runtime-skill.shared.prompt.md";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Lazy<Bundle> Assets = new(ReadBundle);
    private static readonly (string Name, string Action, string Target, string[] Checks)[] Definitions =
    [
        (TaskPrompt.DefaultIssuePrompt, "issue-fix", "issue", ["reproduction", "implementation", "verification"]),
        (TaskPrompt.DefaultReproductionPrompt, "reproduction-setup", "issue", ["reproduction", "instructions"]),
        (TaskPrompt.DefaultE2ePrompt, "e2e", "pr", ["setup", "e2e"]),
        (TaskPrompt.DefaultPrPrompt, "pr-review", "pr", ["context", "local-review", "verification"]),
        (TaskPrompt.DefaultFeatureResearchPrompt, "feature-research", "issue", ["requirements", "existing-capabilities", "feasibility", "next-step"]),
        (TaskPrompt.DefaultBugInvestigationPrompt, "bug-investigation", "issue", ["context", "investigation", "local-review"]),
        (TaskPrompt.DefaultFeatureImplementPrompt, "feature-implement", "issue", ["requirements", "implementation", "verification"]),
        (TaskPrompt.DefaultIssueVerifyPrompt, "issue-verify", "issue", ["setup", "verification"])
    ];

    // Keep the old constructor shape for callers compiled against the local-sync catalog.
    // Neither the Store nor the optional GitHub session factory is used by bundled assets.
    public PromptCatalog(Store store, Func<JsonObject, Task<GitHubSession>>? createSession = null) { }

    public JsonObject List() => Assets.Value.Catalog.DeepClone().AsObject();
    public JsonObject VerificationInstructions() => Assets.Value.Verification.DeepClone().AsObject();
    public JsonObject RuntimeSkillInstructions() => Assets.Value.RuntimeSkill.DeepClone().AsObject();

    public JsonObject Get(string name)
    {
        ValidateName(name);
        if (!Assets.Value.Prompts.TryGetValue(name, out var prompt))
            throw new ProtocolException("PROMPT_NOT_FOUND", "The selected prompt is not included in this Host version.", "Choose one of the bundled action prompts in extension Settings. Accepted tasks keep their original prompt snapshot.");
        return prompt.DeepClone().AsObject();
    }

    /// <summary>Compatibility for older extensions: reload the bundled catalog without network or storage writes.</summary>
    public Task<JsonObject> SyncAsync(JsonObject config) => Task.FromResult(List());

    public static string ValidateName(string name)
    {
        if (!Regex.IsMatch(name, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,139}\.md\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            name.Contains("..", StringComparison.Ordinal) ||
            Regex.IsMatch(name.Split('.')[0], @"\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new ProtocolException("INVALID_PROMPT_NAME", "The selected prompt filename is invalid.", "Choose a bundled Markdown prompt in extension Settings.");
        return name;
    }

    private static Bundle ReadBundle()
    {
        var prompts = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var manifest = new StringBuilder();
        var total = 0;
        foreach (var definition in Definitions.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            using var stream = typeof(PromptCatalog).Assembly.GetManifestResourceStream("Pulse.Host.Prompts." + definition.Name)
                ?? throw InvalidBundle("The Host is missing the bundled prompt " + definition.Name + ".");
            if (stream.Length is <= 0 or > MaximumFileBytes || total + stream.Length > MaximumTotalBytes)
                throw InvalidBundle("A bundled prompt exceeds its supported size limit.");
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            total += bytes.Length;
            string content;
            try { content = Utf8.GetString(bytes); }
            catch (DecoderFallbackException) { throw InvalidBundle("A bundled prompt is not valid UTF-8."); }
            if (content.Contains('\0')) throw InvalidBundle("A bundled prompt contains binary data.");
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var metadata = Metadata(definition.Name, content);
            metadata["content"] = content;
            metadata["sha"] = sha;
            metadata["hashAlgorithm"] = "sha256";
            metadata["source"] = "bundled";
            metadata["sourceUrl"] = SourceUrl;
            metadata["path"] = "prompts/" + definition.Name;
            metadata["schemaVersion"] = 3;
            metadata["actionKind"] = definition.Action;
            metadata["appliesTo"] = definition.Target;
            metadata["requiredChecks"] = new JsonArray(definition.Checks.Select(check => (JsonNode?)JsonValue.Create(check)).ToArray());
            prompts.Add(definition.Name, metadata);
            manifest.Append(definition.Name).Append('\0').Append(sha).Append('\n');
        }
        var verification = ReadFragment(VerificationFragmentName, ref total);
        var runtimeSkill = ReadFragment(RuntimeSkillFragmentName, ref total);
        var fragments = new JsonArray();
        foreach (var fragment in new[] { verification, runtimeSkill })
        {
            manifest.Append(fragment["name"]!.GetValue<string>()).Append('\0').Append(fragment["sha"]!.GetValue<string>()).Append('\n');
            fragments.Add(new JsonObject { ["name"] = fragment["name"]!.DeepClone(), ["sha"] = fragment["sha"]!.DeepClone(), ["hashAlgorithm"] = "sha256" });
        }
        var revision = Protocol.Hash(manifest.ToString());
        var entries = new JsonArray();
        foreach (var prompt in prompts.Values)
        {
            prompt["revision"] = revision;
            var metadata = prompt.DeepClone().AsObject();
            metadata.Remove("content");
            entries.Add(metadata);
        }
        var catalog = new JsonObject
        {
            ["prompts"] = entries, ["source"] = "bundled", ["sourceUrl"] = SourceUrl,
            ["revision"] = revision, ["hashAlgorithm"] = "sha256", ["schemaVersion"] = 3,
            ["fragments"] = fragments
        };
        return new Bundle(catalog, prompts, verification, runtimeSkill);
    }

    private static JsonObject ReadFragment(string name, ref int total)
    {
        using var stream = typeof(PromptCatalog).Assembly.GetManifestResourceStream("Pulse.Host.Prompts." + name)
            ?? throw InvalidBundle("The Host is missing the shared prompt " + name + ".");
        if (stream.Length is <= 0 or > MaximumFileBytes || total + stream.Length > MaximumTotalBytes)
            throw InvalidBundle("A shared prompt exceeds its supported size limit.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        total += bytes.Length;
        string content;
        try { content = Utf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw InvalidBundle("A shared prompt is not valid UTF-8."); }
        if (content.Contains('\0')) throw InvalidBundle("A shared prompt contains binary data.");
        return new JsonObject { ["name"] = name, ["content"] = content,
            ["sha"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), ["hashAlgorithm"] = "sha256" };
    }

    private static JsonObject Metadata(string name, string content)
    {
        var lines = content.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var title = "";
        var description = "";
        var bodyStart = 0;
        if (lines[0].Trim() == "---")
        {
            var end = Array.FindIndex(lines, 1, line => line.Trim() is "---" or "...");
            if (end > 0)
            {
                bodyStart = end + 1;
                for (var index = 1; index < end; index++)
                {
                    var match = Regex.Match(lines[index], @"\A(title|name|description):\s*(.*)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (!match.Success) continue;
                    var value = match.Groups[2].Value.Trim();
                    if (value.Length >= 2 && value[0] == value[^1] && value[0] is '\'' or '"') value = value[1..^1];
                    if (match.Groups[1].Value.Equals("description", StringComparison.OrdinalIgnoreCase)) description = value;
                    else if (title.Length == 0) title = value;
                }
            }
        }
        if (title.Length == 0) title = lines.Skip(bodyStart).FirstOrDefault(line => line.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim() ?? name;
        var result = new JsonObject { ["name"] = name, ["title"] = title[..Math.Min(title.Length, 180)] };
        if (description.Length > 0) result["description"] = description[..Math.Min(description.Length, 1000)];
        return result;
    }

    private static ProtocolException InvalidBundle(string message) => new("PROMPT_BUNDLE_INVALID", message, "Repair or update the Host installation to restore its bundled prompts. Existing task snapshots are retained.");
    private sealed record Bundle(JsonObject Catalog, IReadOnlyDictionary<string, JsonObject> Prompts, JsonObject Verification, JsonObject RuntimeSkill);
}
