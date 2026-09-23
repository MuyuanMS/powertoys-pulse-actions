using System.Text.Json.Nodes;

namespace Pulse.Host;

public static partial class WorkflowResult
{
    // Finite nested variants keep the model schema within the structured-output subset.
    // Dynamic references and byte budgets remain Host checks and are described at the root.
    private static JsonObject AnyOfSchema(IEnumerable<JsonObject> variants) => new() { ["anyOf"] = new JsonArray(variants.Cast<JsonNode?>().ToArray()) };
    private static JsonObject FixedBooleanSchema(bool value) => new() { ["type"] = "boolean", ["enum"] = new JsonArray(JsonValue.Create(value)) };
    private static JsonObject EvidenceSchema(bool nonempty = false)
    {
        // Historical Evidence() permits whitespace-only items, unlike String(nonempty)/TextList.
        var text = TextSchema(4096); text["minLength"] = 1;
        return ArraySchema(text, 50, nonempty ? 1 : 0);
    }

    private static JsonObject VerificationEvidenceSchema()
    {
        var schema = AnyOfSchema(from prior in new[] { false, true } from completed in new[] { false, true }
            select ObjectSchema(new JsonObject
            {
                ["id"] = IdentifierSchema(), ["source"] = EnumSchema(prior ? ["prior-run"] : ["current-run", "ci", "author"]),
                ["kind"] = EnumSchema(["build", "automated-tests", "runtime"]), ["status"] = EnumSchema(completed ? ["passed", "failed"] : ["not_run"]),
                ["subject"] = EnumSchema(["original-pr", "local-candidate"]), ["revisionSha"] = NullableSchema(TextSchema(40, true, "^[a-fA-F0-9]{40}$")),
                ["summary"] = TextSchema(4096, true), ["evidence"] = EvidenceSchema(completed),
                ["runId"] = prior ? NullableSchema(TextSchema(36, true, "^[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}$")) : new JsonObject { ["type"] = "null" }
            }));
        schema["description"] = "runId identifies a prior Pulse task only: use null for current-run, ci and author evidence. A TRX TestRun ID, CI job ID or external test-run ID belongs in evidence text, never runId. Passed or failed checks require at least one evidence item; not_run may have none.";
        return schema;
    }

    private static JsonObject FindingV3Schema() => AnyOfSchema(new[] { false, true }.Select(confirmed => ObjectSchema(new JsonObject
    {
        ["id"] = IdentifierSchema(), ["title"] = TextSchema(512, true), ["priority"] = EnumSchema(Priorities),
        ["status"] = EnumSchema(confirmed ? ["open", "fixed"] : ["open", "fixed", "unverified"]), ["confirmed"] = FixedBooleanSchema(confirmed), ["path"] = TextSchema(4096),
        ["line"] = NullableSchema(PositiveIntegerSchema()), ["details"] = TextSchema(8192, true), ["impact"] = TextSchema(4096, true),
        ["trigger"] = TextSchema(4096, true), ["rootCause"] = TextSchema(8192, true), ["fixSuggestion"] = TextSchema(8192, true),
        ["evidence"] = TextListSchema(confirmed), ["feedback"] = ObjectSchema(new JsonObject { ["body"] = TextSchema(32768), ["suggestionId"] = NullableSchema(IdentifierSchema()) })
    })));

    private static JsonObject FeatureAssessmentSchema() => AnyOfSchema(new[] { "ready", "needs_information", "needs_decision", "already_supported", "duplicate", "not_feasible" }.Select(status => ObjectSchema(new JsonObject
    {
        ["status"] = EnumSchema([status]), ["summary"] = TextSchema(8192, true), ["reasons"] = TextListSchema(true), ["evidence"] = TextListSchema(true),
        ["acceptanceCriteria"] = TextListSchema(status == "ready"), ["questions"] = TextListSchema(status == "needs_information"), ["alternatives"] = TextListSchema(status == "needs_decision"),
        ["relatedIssue"] = status == "duplicate" ? RelatedIssueSchema() : NullableSchema(RelatedIssueSchema()),
        ["planId"] = status == "ready" ? IdentifierSchema() : NullableSchema(IdentifierSchema())
    })));

    private static JsonObject BugAssessmentSchema() => AnyOfSchema(new[] { "confirmed", "needs_information", "needs_verification", "already_fixed", "duplicate", "not_a_bug" }.Select(status => ObjectSchema(new JsonObject
    {
        ["status"] = EnumSchema([status]), ["summary"] = TextSchema(8192, true), ["reasons"] = TextListSchema(true), ["evidence"] = TextListSchema(true),
        ["questions"] = TextListSchema(status == "needs_information"), ["relatedIssue"] = status == "duplicate" ? RelatedIssueSchema() : NullableSchema(RelatedIssueSchema()),
        ["planId"] = status == "needs_verification" ? IdentifierSchema() : NullableSchema(IdentifierSchema()), ["reproduction"] = ReproductionSchema()
    })));

    private static JsonObject ReproductionSchema()
    {
        var schema = AnyOfSchema(new[] { false, true }.Select(executed => ObjectSchema(new JsonObject
        {
            ["status"] = EnumSchema(executed ? ["reproduced", "not_reproduced"] : ["not_run", "blocked"]), ["revisionSha"] = NullableSchema(TextSchema(40, true, "^[a-fA-F0-9]{40}$")),
            ["environment"] = TextSchema(8192), ["steps"] = TextListSchema(), ["expected"] = TextSchema(8192), ["observed"] = TextSchema(8192), ["evidence"] = TextListSchema(executed)
        })));
        schema["description"] = "Record the actual environment, steps, expected and observed behavior when available. Reproduced/not_reproduced outcomes require evidence. Leave unavailable context empty or revisionSha null; do not invent a reproduction or source revision.";
        return schema;
    }
}
