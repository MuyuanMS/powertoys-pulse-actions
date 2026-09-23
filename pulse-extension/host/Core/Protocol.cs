using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pulse.Host;

public sealed class ProtocolException(string code, string message, string guidance = "Open Settings or the task details, correct the issue, and retry.") : Exception(message)
{
    public string Code { get; } = code;
    public string Guidance { get; } = guidance;
    public JsonObject ToJson() => new() { ["code"] = Code, ["message"] = Message, ["guidance"] = Guidance };
}

public static partial class Protocol
{
    public const int Version = 1;
    public const int MaxInputBytes = 256 * 1024;
    public const int MaxOutputBytes = 900 * 1024;
    public const string ProductionOrigin = "https://cautious-memory-r38ze9j.pages.github.io";
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false, MaxDepth = 32 };

    public static string RequiredString(JsonObject value, string name, int maxLength = 256)
    {
        if (value[name] is not JsonValue node || !node.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text) || text.Length > maxLength || text.Contains('\0'))
            throw new ProtocolException("INVALID_REQUEST", $"Field {name} must be nonempty text of at most {maxLength} characters.");
        return text;
    }

    public static void OnlyKeys(JsonObject value, params string[] keys)
    {
        foreach (var key in value.Select(pair => pair.Key))
            if (!keys.Contains(key, StringComparer.Ordinal))
                throw new ProtocolException("INVALID_REQUEST", $"Field {key} is not allowed.", "Web pages may supply task context only. Programs, folders, and permissions are controlled by extension settings.");
    }

    public static string RunId(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var id)) throw new ProtocolException("INVALID_REQUEST", "The run or operation ID is invalid.");
        return id.ToString("D");
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static string Fingerprint(JsonNode value) => Hash(Canonical(value));
    private static string Canonical(JsonNode? value) => value switch
    {
        JsonObject obj => "{" + string.Join(",", obj.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => JsonSerializer.Serialize(x.Key) + ":" + Canonical(x.Value))) + "}",
        JsonArray arr => "[" + string.Join(",", arr.Select(Canonical)) + "]",
        _ => value?.ToJsonString(JsonOptions) ?? "null"
    };

    public static bool IsActive(string? state) => state is "accepted" or "running";
    public static string Now() => DateTimeOffset.UtcNow.ToString("O");

    /// <summary>Validate and normalize request identity, including readable targetless historical requests.</summary>
    /// <remarks>Acceptance must also use ValidateNewTask after checking for an existing request.</remarks>
    public static JsonObject ValidateTask(JsonObject input, bool allowFollowUp = false)
    {
        OnlyKeys(input, "requestId", "actionId", "actionKind", "repository", "target", "expectedHeadSha", "context", "prompt", "execution", "reviewOptions", "followUp", "planSource");
        if (input.ContainsKey("execution")) ExecutionOptions.ValidateOverride(input["execution"]);
        RequiredString(input, "requestId", 128);
        RequiredString(input, "actionId", 256);
        var kind = RequiredString(input, "actionKind", 40);
        if (kind is not ("issue-fix" or "pr-review" or "reproduction-setup" or "e2e" or "feature-research" or "bug-investigation") &&
            !(allowFollowUp && kind is ("pr-verify" or "feature-implement" or "issue-verify")))
            throw new ProtocolException("INVALID_REQUEST", "This action type is not supported.");
        if (input.ContainsKey("planSource") || kind is ("feature-implement" or "issue-verify"))
        {
            if (!allowFollowUp || kind is not ("feature-implement" or "issue-fix" or "reproduction-setup" or "issue-verify"))
                throw new ProtocolException("INVALID_REQUEST", "Plan links can only be prepared by the Host from a saved result.");
            ValidatePlanSource(RequireObject(input["planSource"]), input);
        }
        ReviewModes.Validate(input);
        if (input.ContainsKey("followUp") || kind == "pr-verify")
        {
            if (!allowFollowUp || kind != "pr-verify") throw new ProtocolException("INVALID_REQUEST", "Verification links can only be prepared by the Host from a saved review.");
            var followUp = RequireObject(input["followUp"]);
            OnlyKeys(followUp, "parentRunId", "recommendationId", "parentResultFingerprint", "subject", "revisionSha");
            RunId(RequiredString(followUp, "parentRunId", 36));
            foreach (var field in new[] { "recommendationId", "parentResultFingerprint" })
                if (!Regex.IsMatch(RequiredString(followUp, field, 64), @"\A[a-f0-9]{64}\z")) throw new ProtocolException("INVALID_REQUEST", "The saved verification recommendation identity is invalid.");
            if (RequiredString(followUp, "subject", 20) != "original-pr" || !ShaPattern().IsMatch(RequiredString(followUp, "revisionSha", 40)) ||
                !string.Equals(followUp["revisionSha"]!.GetValue<string>(), input["expectedHeadSha"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                throw new ProtocolException("INVALID_REQUEST", "Verification must use the parent review's original PR revision.");
        }
        var repository = RequiredString(input, "repository");
        if (!RepositoryPattern().IsMatch(repository)) throw new ProtocolException("INVALID_REQUEST", "The repository must use the GitHub owner/repository format.");
        Configuration.RequirePowerToys(repository);
        RequiredString(input, "prompt", 128 * 1024);
        if (Encoding.UTF8.GetByteCount(input["prompt"]!.GetValue<string>()) > 160 * 1024)
            throw new ProtocolException("INPUT_TOO_LARGE", "The task prompt exceeds 160 KiB.");
        var contextLimit = allowFollowUp && input["planSource"] is JsonObject ? 256 * 1024 : 32 * 1024;
        if (input["context"] is JsonNode context && Encoding.UTF8.GetByteCount(context.ToJsonString()) > contextLimit)
            throw new ProtocolException("INPUT_TOO_LARGE", "The task context exceeds its allowed size.");
        if (input["target"] is not null)
        {
            if (input["target"] is not JsonObject target) throw new ProtocolException("INVALID_REQUEST", "target must be an object.");
            OnlyKeys(target, "type", "number");
            if (RequiredString(target, "type") is not ("issue" or "pr") || target["number"] is not JsonValue number || !number.TryGetValue<int>(out var n) || n <= 0)
                throw new ProtocolException("INVALID_REQUEST", "target requires an issue/pr type and a positive integer number.");
        }
        var isPr = input["target"]?["type"]?.GetValue<string>() == "pr";
        if (kind is ("feature-research" or "bug-investigation" or "feature-implement" or "issue-verify") && input["target"] is not JsonObject)
            throw new ProtocolException("INVALID_REQUEST", "This Issue workflow requires an Issue target.");
        if (input["target"] is JsonObject suppliedTarget && suppliedTarget["type"]!.GetValue<string>() != TaskPrompt.SelectionForAction(kind).TargetType)
            throw new ProtocolException("INVALID_REQUEST", "This action type does not match the issue or pull request target.");
        if (kind is ("pr-review" or "pr-verify") && !isPr) throw new ProtocolException("INVALID_REQUEST", "PR review and verification require a pull request target.");
        if (isPr && kind is ("pr-review" or "pr-verify" or "e2e") && input["expectedHeadSha"] is null)
            throw new ProtocolException("STALE_CONTEXT", "The PR review/E2E request is missing expectedHeadSha.", "Refresh the current PR's full HEAD SHA in Pulse.");
        if (input["expectedHeadSha"] is not null && (!isPr || !ShaPattern().IsMatch(RequiredString(input, "expectedHeadSha", 40))))
            throw new ProtocolException("INVALID_REQUEST", "expectedHeadSha requires a PR target and a complete 40-character SHA.");
        var result = (JsonObject)input.DeepClone();
        result["repository"] = repository.ToLowerInvariant();
        if (result["expectedHeadSha"] is not null) result["expectedHeadSha"] = result["expectedHeadSha"]!.GetValue<string>().ToLowerInvariant();
        return result;
    }

    /// <summary>Admission rules for a request that has not already been accepted.</summary>
    public static JsonObject ValidateNewTask(JsonObject input, bool allowFollowUp = false)
    {
        var task = ValidateTask(input, allowFollowUp);
        if (task["target"] is not JsonObject)
        {
            var targetType = TaskPrompt.SelectionForAction(task["actionKind"]!.GetValue<string>()).TargetType;
            var label = targetType == "pr" ? "pull request" : "Issue";
            throw new ProtocolException("INVALID_REQUEST", $"This task requires a {label} target.",
                $"Your saved request is unchanged. Open the matching {label} in Pulse to start a new task. Use tasks.lookup with the original request to recover an unconfirmed submission.");
        }
        return task;
    }

    private static void ValidatePlanSource(JsonObject source, JsonObject task)
    {
        OnlyKeys(source, "parentRunId", "parentResultFingerprint", "proposalId", "planId", "repository", "target", "revisionSha", "sourceKind", "candidateSnapshotHash");
        RunId(RequiredString(source, "parentRunId", 36));
        if (!Regex.IsMatch(RequiredString(source, "parentResultFingerprint", 64), @"\A[a-f0-9]{64}\z"))
            throw new ProtocolException("INVALID_REQUEST", "The saved plan's result identity is invalid.");
        foreach (var field in new[] { "proposalId", "planId" })
            if (RequiredString(source, field, 128).Any(char.IsControl)) throw new ProtocolException("INVALID_REQUEST", "Saved plan identifiers must be bounded text.");
        var repository = RequiredString(source, "repository");
        Configuration.RequirePowerToys(repository);
        if (!string.Equals(repository, RequiredString(task, "repository"), StringComparison.OrdinalIgnoreCase))
            throw new ProtocolException("INVALID_REQUEST", "A saved plan cannot change its repository.");
        var target = RequireObject(source["target"]);
        OnlyKeys(target, "type", "number");
        var type = RequiredString(target, "type", 5);
        if (type is not ("pr" or "issue") || target["number"] is not JsonValue number || !number.TryGetValue<int>(out var n) || n <= 0 ||
            task["target"] is not JsonObject taskTarget || RequiredString(taskTarget, "type", 5) != type ||
            taskTarget["number"] is not JsonValue taskNumber || !taskNumber.TryGetValue<int>(out var tn) || tn != n)
            throw new ProtocolException("INVALID_REQUEST", "A saved plan cannot change its Issue or PR target.");
        if (!source.ContainsKey("revisionSha") || source["revisionSha"] is not null && !ShaPattern().IsMatch(RequiredString(source, "revisionSha", 40)))
            throw new ProtocolException("INVALID_REQUEST", "The saved plan revision must be a complete SHA or null.");
        var sourceKind = source.ContainsKey("sourceKind") ? RequiredString(source, "sourceKind", 20) : null;
        if (sourceKind is not (null or "original" or "local-candidate"))
            throw new ProtocolException("INVALID_REQUEST", "The saved plan source kind is invalid.");
        if (sourceKind == "local-candidate")
        {
            if (source["revisionSha"] is null || !Regex.IsMatch(RequiredString(source, "candidateSnapshotHash", 64), @"\A[a-fA-F0-9]{64}\z"))
                throw new ProtocolException("INVALID_REQUEST", "A local candidate plan requires its base revision and immutable snapshot hash.");
        }
        else if (source.ContainsKey("candidateSnapshotHash"))
            throw new ProtocolException("INVALID_REQUEST", "Candidate snapshot hashes apply only to a saved local-candidate source.");
        var taskSha = task["expectedHeadSha"] is JsonValue sha && sha.TryGetValue<string>(out var expectedSha) ? expectedSha : null;
        if (type == "pr" && (source["revisionSha"] is null || !string.Equals(source["revisionSha"]!.GetValue<string>(), taskSha, StringComparison.OrdinalIgnoreCase)))
            throw new ProtocolException("INVALID_REQUEST", "A PR plan must retain the expected PR revision.");
    }

    public static JsonObject RequireObject(JsonNode? node) => node as JsonObject ?? throw new ProtocolException("INVALID_REQUEST", "The message and payload must be JSON objects.");
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();
    [GeneratedRegex(@"^[a-fA-F0-9]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex ShaPattern();
}

public static class NativeFraming
{
    public static async Task<JsonObject?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        var read = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken);
        if (read == 0) return null;
        try { await stream.ReadExactlyAsync(header.AsMemory(1, 3), cancellationToken); }
        catch (EndOfStreamException) { throw new ProtocolException("INVALID_FRAME", "The Native Messaging length header is incomplete."); }
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > Protocol.MaxInputBytes) throw new ProtocolException("INPUT_TOO_LARGE", "The Native Messaging message exceeds 256 KiB.");
        var body = new byte[(int)length];
        try { await stream.ReadExactlyAsync(body, cancellationToken); }
        catch (EndOfStreamException) { throw new ProtocolException("INVALID_FRAME", "The Native Messaging message was not fully received."); }
        try { return Protocol.RequireObject(JsonNode.Parse(body, documentOptions: new JsonDocumentOptions { MaxDepth = 32 })); }
        catch (JsonException) { throw new ProtocolException("INVALID_REQUEST", "The message is not valid JSON."); }
    }

    public static async Task WriteAsync(Stream stream, JsonObject message, CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, Protocol.JsonOptions);
        if (body.Length > Protocol.MaxOutputBytes)
        {
            message = new JsonObject { ["id"] = message["id"]?.DeepClone(), ["protocolVersion"] = 1, ["ok"] = false,
                ["error"] = new ProtocolException("OUTPUT_TOO_LARGE", "The response is too large. Request fewer records or events.", "Use pagination. Full available records remain stored locally.").ToJson() };
            body = JsonSerializer.SerializeToUtf8Bytes(message, Protocol.JsonOptions);
        }
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)body.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
