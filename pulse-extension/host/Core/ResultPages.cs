using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pulse.Host;

/// <summary>Lossless transport pages for full reports; storage and action decisions always use the complete result.</summary>
public static class ResultPages
{
    private const int FrameBudget = Protocol.MaxOutputBytes - 8192;
    private const int PageBudget = 300 * 1024;
    private static readonly string[] Paths =
    [
        "findings", "nextActions", "validation", "artifacts", "diagnostics", "verificationEvidence", "review.suggestions", "plans",
        "report.coverage", "report.limitations", "reviewConclusion.blockingUncertainties",
        "e2eAssessment.scenarios", "e2eAssessment.expectedResults", "e2eAssessment.prerequisites", "e2eAssessment.evidence",
        "featureAssessment.reasons", "featureAssessment.evidence", "featureAssessment.acceptanceCriteria",
        "featureAssessment.questions", "featureAssessment.alternatives",
        "bugAssessment.reasons", "bugAssessment.evidence", "bugAssessment.questions",
        "bugAssessment.reproduction.steps", "bugAssessment.reproduction.evidence"
    ];

    public static void Apply(JsonObject detail, JsonObject storedResult)
    {
        if (Size(detail) <= FrameBudget || detail["result"] is not JsonObject result || result["schemaVersion"]?.ToString() != "3") return;
        var task = detail["task"] as JsonObject;
        task?.Remove("context"); task?.Remove("prompt"); detail["taskContextOmitted"] = true;
        if (Size(detail) <= FrameBudget) return;

        var full = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        foreach (var path in Paths)
        {
            if (ArrayAt(result, path) is not JsonArray items || items.Count == 0) continue;
            full[path] = items;
            SetArray(result, path, []);
        }
        // Legacy aliases carry the same canonical values; they must not duplicate an unpaged full list.
        if (result["nextActions"] is JsonArray) result["nextSteps"] = new JsonArray();
        if (result["diagnostics"] is JsonArray) result["blockers"] = new JsonArray();
        var sections = new JsonArray();
        detail["resultPaging"] = new JsonObject { ["fingerprint"] = Protocol.Fingerprint(storedResult), ["sections"] = sections };
        foreach (var (path, items) in full)
            sections.Add(new JsonObject { ["path"] = path, ["total"] = items.Count, ["nextOffset"] = 0 });
        var remaining = FrameBudget - Size(detail) - 4096;
        if (remaining <= 0) throw TooLarge();
        var allowance = Math.Min(64 * 1024, remaining / Math.Max(1, full.Count + 1));
        foreach (var section in sections.OfType<JsonObject>())
        {
            var path = section["path"]!.GetValue<string>();
            var items = full[path]; var page = new JsonArray(); var used = 0;
            for (var i = 0; i < items.Count; i++)
            {
                var bytes = Size(items[i]);
                // nextSteps mirrors nextActions, so reserve its encoded bytes as well.
                var cost = bytes * (path is "nextActions" or "diagnostics" ? 2 : 1) + 1;
                if (used + cost > allowance) break;
                page.Add(items[i]?.DeepClone()); used += cost;
            }
            SetArray(result, path, page);
            section["nextOffset"] = page.Count < items.Count ? JsonValue.Create(page.Count) : null;
        }
        if (result["nextActions"] is JsonArray actions) result["nextSteps"] = actions.DeepClone();
        if (result["diagnostics"] is JsonArray diagnostics)
            result["blockers"] = new JsonArray(diagnostics.OfType<JsonObject>().Where(row => row["severity"]?.GetValue<string>() == "error").Select(row => row["message"]?.DeepClone()).ToArray());
        if (Size(detail) > FrameBudget) throw TooLarge();
    }

    public static JsonObject Read(Store store, JsonObject payload)
    {
        Protocol.OnlyKeys(payload, "runId", "path", "offset", "limit", "fingerprint");
        var runId = Protocol.RunId(Protocol.RequiredString(payload, "runId"));
        var path = Protocol.RequiredString(payload, "path", 80);
        if (!Paths.Contains(path, StringComparer.Ordinal)) throw new ProtocolException("INVALID_REQUEST", "This result section cannot be paged.");
        var offset = Integer(payload, "offset", 0, 0, int.MaxValue);
        var limit = Integer(payload, "limit", 100, 1, 200);
        var fingerprint = Protocol.RequiredString(payload, "fingerprint", 64);
        var raw = store.ReadResult(runId) ?? throw new ProtocolException("RESULT_NOT_READY", "The final report has not been saved yet.");
        if (raw["schemaVersion"]?.ToString() != "3") throw new ProtocolException("INVALID_REQUEST", "This report does not use paged version 3 results.");
        if (Protocol.Fingerprint(raw) != fingerprint) throw new ProtocolException("RESULT_CHANGED", "The saved report changed while its pages were being read.", "Refresh the report from its first page; no previous page was replaced.");
        var saved = store.ReadTask(runId); var task = Protocol.RequireObject(saved["task"]);
        var projected = store.ProjectResult(runId, task, store.ReadStatus(runId), raw);
        var all = ArrayAt(projected, path) ?? throw new ProtocolException("INVALID_REQUEST", "The requested report section is not present.");
        if (offset > all.Count) throw new ProtocolException("INVALID_REQUEST", "The report cursor is outside this section.");
        var items = new JsonArray(); var bytes = 0; var next = offset;
        while (next < all.Count && items.Count < limit)
        {
            var size = Size(all[next]) + 1;
            if (size > FrameBudget - 1024) throw TooLarge();
            if (items.Count > 0 && bytes + size > PageBudget) break;
            items.Add(all[next]?.DeepClone()); bytes += size; next++;
        }
        var response = new JsonObject
        {
            ["items"] = items, ["nextOffset"] = next < all.Count ? JsonValue.Create(next) : null,
            ["total"] = all.Count, ["fingerprint"] = fingerprint
        };
        if (Size(response) > FrameBudget) throw TooLarge();
        return response;
    }

    private static JsonArray? ArrayAt(JsonObject result, string path)
    {
        JsonNode? current = result;
        foreach (var part in path.Split('.')) current = (current as JsonObject)?[part];
        return current as JsonArray;
    }
    private static void SetArray(JsonObject result, string path, JsonArray value)
    {
        var parts = path.Split('.'); var current = result;
        foreach (var part in parts[..^1]) current = current[part]!.AsObject();
        current[parts[^1]] = value;
    }
    private static int Integer(JsonObject input, string field, int fallback, int min, int max)
    {
        if (!input.ContainsKey(field)) return fallback;
        if (input[field] is not JsonValue value || !value.TryGetValue<int>(out var parsed) || parsed < min || parsed > max)
            throw new ProtocolException("INVALID_REQUEST", "The report page cursor or limit is invalid.");
        return parsed;
    }
    private static int Size(JsonNode? value) => JsonSerializer.SerializeToUtf8Bytes(value, Protocol.JsonOptions).Length;
    private static ProtocolException TooLarge() => new("RESULT_SECTION_TOO_LARGE", "A report section exceeds one supported display frame.", "The full report remains saved. No findings were dropped; inspect the saved result and report this transport limit.");
}
