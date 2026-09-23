using System.Text.Json.Nodes;

namespace Pulse.Host;

/// <summary>Human PR actions use live GitHub prerequisites and the narrow confirmed-P0 rule.</summary>
internal static class PrActionPolicy
{
    public static bool HasConfirmedP0(Store store, JsonObject task)
    {
        var target = task["target"] as JsonObject;
        if (Text(target, "type") != "pr") return false;
        var repository = Text(task, "repository");
        var sha = Text(task, "expectedHeadSha");
        foreach (var id in store.RunIds())
        {
            try
            {
                var saved = store.ReadTask(id)["task"] as JsonObject;
                var other = saved?["target"] as JsonObject;
                if (saved is null || !repository.Equals(Text(saved, "repository"), StringComparison.OrdinalIgnoreCase) ||
                    Text(other, "type") != "pr" || !JsonNode.DeepEquals(other?["number"], target?["number"]) ||
                    !sha.Equals(Text(saved, "expectedHeadSha"), StringComparison.OrdinalIgnoreCase)) continue;
                if (Text(saved, "actionKind") is "e2e" or "pr-verify")
                {
                    // Verification findings concern the code actually executed. A model's
                    // original-pr label cannot override candidate, unknown, or absent Host evidence.
                    var provenance = store.ReadJson(Path.Combine(store.RunDirectory(id), "provenance.json"));
                    if (!ReviewProvenance.MatchesOriginal(provenance, Text(saved, "expectedHeadSha"))) continue;
                }
                if (WorkflowResult.HasConfirmedP0(store.ReadResult(id), saved)) return true;
            }
            catch (ProtocolException)
            {
                // Missing or unreadable historical data is not proof of a confirmed P0.
            }
        }
        return false;
    }

    private static string Text(JsonObject? value, string field) => value?[field] is JsonValue node && node.TryGetValue<string>(out var text) ? text : "";
}
