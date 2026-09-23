using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pulse.Host.Tests;

/// <summary>Generated schema constraints checked against unchanged Host validators; no files, CLI or network.</summary>
internal static class ResultSchemaParityScenarios
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string RunId = "9097b8ba-379b-40eb-a729-dd72e7b8d4de";
    private static readonly JsonObject V2 = WorkflowResult.Schema();
    private static readonly JsonObject V3 = WorkflowResult.SchemaV3();

    internal static Task RunAllAsync()
    {
        SupportedSchemaShapes();
        VerificationSourceAndOutcome();
        FindingConfirmationAndText();
        E2eRequirements();
        InvestigationRequirements();
        PlansBranchesAndIntegerBounds();
        EvidenceCompatibilityAndDynamicDescriptions();
        Console.WriteLine("PASS result schema parity: source/run identity, finite outcome variants, evidence requirements, findings, investigation plans, text/scalar bounds and structured-output subset (in memory)");
        return Task.CompletedTask;
    }

    private static void SupportedSchemaShapes()
    {
        foreach (var schema in new[] { V2, V3 })
        {
            Check(schema["type"]?.GetValue<string>() == "object" && schema["anyOf"] is null, "The schema root remains an object rather than a root union.");
            Walk(schema, node =>
            {
                Check(!node.ContainsKey("if") && !node.ContainsKey("then") && !node.ContainsKey("else") && !node.ContainsKey("allOf") && !node.ContainsKey("$data") && !node.ContainsKey("contains"), "Schemas use only supported finite constraints.");
                if (node["type"]?.GetValue<string>() == "object")
                {
                    Check(node["additionalProperties"]?.GetValue<bool>() == false, "Every object variant is closed.");
                    var names = node["properties"]!.AsObject().Select(pair => pair.Key).Order(StringComparer.Ordinal);
                    var required = node["required"]!.AsArray().Select(value => value!.GetValue<string>()).Order(StringComparer.Ordinal);
                    Check(names.SequenceEqual(required), "Every variant requires all declared fields.");
                }
                if (node["pattern"] is JsonValue value)
                {
                    var pattern = value.GetValue<string>();
                    Check(!pattern.Contains("(?", StringComparison.Ordinal) && !pattern.Contains("\\z", StringComparison.Ordinal), "Patterns avoid lookarounds and runtime-specific absolute anchors.");
                }
            });
            var limits = new SchemaLimits(); Measure(schema, 0, limits);
            Check(limits.Properties <= 5000 && limits.Depth <= 10 && limits.Text <= 120000 && limits.Enums <= 1000,
                $"The generated schema fits documented structured-output limits: properties={limits.Properties}, depth={limits.Depth}, text={limits.Text}, enum values={limits.Enums}.");
            Console.WriteLine($"Schema v{schema["properties"]!["schemaVersion"]!["const"]}: {limits.Properties} properties, depth {limits.Depth}, {limits.Text} constrained-name characters, {limits.Enums} enum values.");
        }
        Check(V3["properties"]!["findings"]!["maxItems"] is null && V3["properties"]!["nextActions"]!["maxItems"] is null, "Schema changes do not reintroduce Top-N result limits.");
    }

    private static void VerificationSourceAndOutcome()
    {
        foreach (var version in new[] { V2, V3 })
        {
            var schema = version["properties"]!["verificationEvidence"]!["items"]!.AsObject();
            foreach (var source in new[] { "current-run", "ci", "author", "prior-run" })
            {
                var row = Verification(); row["source"] = source;
                Pair(schema, row, "ValidVerificationEvidence", true, source + " with null runId");
                row["runId"] = RunId;
                Pair(schema, row, "ValidVerificationEvidence", source == "prior-run", source + " with an external-looking run ID");
                row["runId"] = "test-job-42";
                Pair(schema, row, "ValidVerificationEvidence", false, "runId must not contain a TRX label or CI job ID");
            }
            foreach (var status in new[] { "passed", "failed", "not_run" })
            {
                var row = Verification(); row["status"] = status; row["evidence"] = new JsonArray();
                Pair(schema, row, "ValidVerificationEvidence", status == "not_run", status + " evidence requirement");
            }
            var nul = Verification(); nul["evidence"]![0] = "log\0path";
            Pair(schema, nul, "ValidVerificationEvidence", false, "Evidence must not contain NUL");
        }
        var recommendation = new JsonObject { ["mode"] = "build-tests", ["reason"] = "Verify a source change", ["question"] = "Does it pass?", ["scenarios"] = Strings("Build"), ["prerequisites"] = new JsonArray(), ["evidence"] = new JsonArray(), ["readiness"] = "ready" };
        var recommendedSchema = V2["properties"]!["verificationRecommendation"]!.AsObject();
        Pair(recommendedSchema, recommendation, "ValidVerificationRecommendation", true, "An actionable recommendation may have no optional evidence");
        var missing = recommendation.DeepClone().AsObject(); missing["readiness"] = "missing-prerequisites";
        Pair(recommendedSchema, missing, "ValidVerificationRecommendation", false, "Missing prerequisites must be enumerated");
        missing["prerequisites"] = Strings("Install the required SDK");
        Pair(recommendedSchema, missing, "ValidVerificationRecommendation", true, "Named missing prerequisites remain valid");
        recommendation["scenarios"] = new JsonArray();
        Pair(recommendedSchema, recommendation, "ValidVerificationRecommendation", false, "A recommendation needs at least one scenario");
    }

    private static void FindingConfirmationAndText()
    {
        var schema = V3["properties"]!["findings"]!["items"]!.AsObject();
        var row = Finding(); Pair(schema, row, "ValidFindingV3", true, "Confirmed supported finding");
        row["status"] = "unverified";
        Pair(schema, row, "ValidFindingV3", false, "Confirmed and unverified cannot describe the same finding");
        row["confirmed"] = false;
        Pair(schema, row, "ValidFindingV3", true, "Retained unverified observations remain representable");
        row["evidence"] = new JsonArray();
        Pair(schema, row, "ValidFindingV3", true, "An unconfirmed observation may lack proof");
        row["confirmed"] = true; row["status"] = "open";
        Pair(schema, row, "ValidFindingV3", false, "Confirmed findings require evidence");
        foreach (var invalid in new[] { "", " \t\r\n", "\u0085\u00a0\u2003", "title\0value" })
        {
            var text = Finding(); text["title"] = invalid;
            Pair(schema, text, "ValidFindingV3", false, "Required text rejects blank and NUL values");
        }
        var bom = Finding(); bom["title"] = "\ufeff";
        Pair(schema, bom, "ValidFindingV3", true, "The pattern follows .NET whitespace semantics without an ECMAScript-only whitespace ban");
        var optional = Finding(); optional["path"] = ""; optional["feedback"]!["body"] = "";
        Pair(schema, optional, "ValidFindingV3", true, "Optional text remains allowed to be empty");
        optional["feedback"]!["body"] = "\0";
        Pair(schema, optional, "ValidFindingV3", false, "Optional text still rejects NUL");
    }

    private static void E2eRequirements()
    {
        var schema = V3["properties"]!["e2eAssessment"]!.AsObject();
        foreach (var level in new[] { "not_needed", "recommended", "required" })
        {
            var row = E2e(); row["level"] = level;
            Pair(schema, row, "ValidE2eAssessmentV3", true, level + " supported scenario");
            var noQuestion = row.DeepClone().AsObject(); noQuestion["question"] = "";
            Pair(schema, noQuestion, "ValidE2eAssessmentV3", level == "not_needed", level + " question requirement");
            var noScenarios = row.DeepClone().AsObject(); noScenarios["scenarios"] = new JsonArray(); noScenarios["expectedResults"] = new JsonArray();
            Pair(schema, noScenarios, "ValidE2eAssessmentV3", level == "not_needed", level + " scenario requirement");
            var noEvidence = row.DeepClone().AsObject(); noEvidence["evidence"] = new JsonArray();
            Pair(schema, noEvidence, "ValidE2eAssessmentV3", false, "Every E2E assessment needs a supporting reason's evidence");
            row["readiness"] = "missing-prerequisites";
            Pair(schema, row, "ValidE2eAssessmentV3", false, "Missing E2E readiness needs named prerequisites");
            row["prerequisites"] = Strings("An interactive desktop");
            Pair(schema, row, "ValidE2eAssessmentV3", true, "Named prerequisites are retained");
        }
    }

    private static void InvestigationRequirements()
    {
        var featureSchema = V3["properties"]!["featureAssessment"]!.AsObject();
        var bugSchema = V3["properties"]!["bugAssessment"]!.AsObject();
        foreach (var status in new[] { "ready", "needs_information", "needs_decision", "already_supported", "duplicate", "not_feasible" })
        {
            var row = Feature(status); Pair(featureSchema, row, "ValidFeatureV3", true, "Feature " + status);
            foreach (var field in new[] { "reasons", "evidence" })
            {
                var empty = row.DeepClone().AsObject(); empty[field] = new JsonArray();
                Pair(featureSchema, empty, "ValidFeatureV3", false, "Feature " + field + " cannot be empty");
            }
            foreach (var field in new[] { "acceptanceCriteria", "questions", "alternatives" })
            {
                var empty = row.DeepClone().AsObject(); empty[field] = new JsonArray();
                var required = field == "acceptanceCriteria" && status == "ready" || field == "questions" && status == "needs_information" || field == "alternatives" && status == "needs_decision";
                Pair(featureSchema, empty, "ValidFeatureV3", !required, status + " / " + field);
            }
            foreach (var field in new[] { "planId", "relatedIssue" })
            {
                var empty = row.DeepClone().AsObject(); empty[field] = null;
                Pair(featureSchema, empty, "ValidFeatureV3", !(field == "planId" && status == "ready" || field == "relatedIssue" && status == "duplicate"), status + " / " + field);
            }
        }
        foreach (var status in new[] { "confirmed", "needs_information", "needs_verification", "already_fixed", "duplicate", "not_a_bug" })
        {
            var row = Bug(status); Pair(bugSchema, row, "ValidBugV3", true, "Bug " + status);
            foreach (var field in new[] { "reasons", "evidence", "questions" })
            {
                var empty = row.DeepClone().AsObject(); empty[field] = new JsonArray();
                Pair(bugSchema, empty, "ValidBugV3", field == "questions" && status != "needs_information", status + " / " + field);
            }
            foreach (var field in new[] { "planId", "relatedIssue" })
            {
                var empty = row.DeepClone().AsObject(); empty[field] = null;
                Pair(bugSchema, empty, "ValidBugV3", !(field == "planId" && status == "needs_verification" || field == "relatedIssue" && status == "duplicate"), status + " / " + field);
            }
        }
        foreach (var status in new[] { "reproduced", "not_reproduced", "not_run", "blocked" })
        {
            var row = Bug("already_fixed"); row["reproduction"]!["status"] = status;
            Pair(bugSchema, row, "ValidBugV3", true, "Recorded reproduction evidence");
            row["reproduction"]!["evidence"] = new JsonArray();
            Pair(bugSchema, row, "ValidBugV3", status is "not_run" or "blocked", "Reproduction " + status + " evidence requirement");
        }
        foreach (var badUrl in new[] { "http://github.com/example/repo/issues/1", "https://user@github.com/example/repo/issues/1", "https://github.com/example/repo/issues/1?q=x", "https://github.com/example/repo/pull/1" })
        {
            var row = Bug("duplicate"); row["relatedIssue"]!["url"] = badUrl;
            Pair(bugSchema, row, "ValidBugV3", false, "Related Issues use canonical GitHub issue URLs");
        }
    }

    private static void PlansBranchesAndIntegerBounds()
    {
        var planSchema = V3["properties"]!["plans"]!["items"]!.AsObject();
        var plan = new JsonObject { ["id"] = "plan", ["kind"] = "issue-fix", ["summary"] = "Fix the issue", ["steps"] = Strings("Change and verify"), ["acceptanceCriteria"] = Strings("No regression"), ["prerequisites"] = new JsonArray(), ["evidence"] = Strings("Recorded investigation") };
        Pair(planSchema, plan, "ValidPlanV3", true, "An actionable plan");
        foreach (var field in new[] { "steps", "acceptanceCriteria", "evidence" })
        {
            var empty = plan.DeepClone().AsObject(); empty[field] = new JsonArray();
            Pair(planSchema, empty, "ValidPlanV3", false, "Plan " + field + " is required");
        }
        var actionSchema = V3["properties"]!["nextActions"]!["items"]!.AsObject();
        var action = new JsonObject { ["kind"] = "create-pr", ["reason"] = "Publish the verified candidate", ["body"] = "", ["recommended"] = true,
            ["pullRequest"] = new JsonObject { ["head"] = "owner:fix-focus", ["sourceHeadSha"] = Sha, ["base"] = "main", ["title"] = "Fix focus", ["body"] = "Refs #1", ["draft"] = true } };
        Pair(actionSchema, action, "ValidNextActionV3", true, "A saved source branch");
        foreach (var head in new[] { "fix-focus", ":fix-focus", "-owner:fix", "owner:bad branch", "owner:bad\0branch" })
        {
            var invalid = action.DeepClone().AsObject(); invalid["pullRequest"]!["head"] = head;
            Pair(actionSchema, invalid, "ValidNextActionV3", false, "Invalid PR head " + head.Replace('\0', '?'));
        }
        var findingSchema = V3["properties"]!["findings"]!["items"]!.AsObject();
        foreach (var line in new long[] { 0, -1, (long)int.MaxValue + 1 })
        {
            var invalid = Finding(); invalid["line"] = line;
            Pair(findingSchema, invalid, "ValidFindingV3", false, "Line must fit a positive Int32");
        }
    }

    private static void EvidenceCompatibilityAndDynamicDescriptions()
    {
        var historical = Verification(); historical["evidence"] = Strings(" \t");
        Pair(V3["properties"]!["verificationEvidence"]!["items"]!.AsObject(), historical, "ValidVerificationEvidence", true, "Historical evidence item semantics are unchanged");
        var report = Report();
        report["validation"] = new JsonArray(new JsonObject { ["id"] = "check", ["name"] = "Recorded check", ["status"] = "passed", ["required"] = false, ["details"] = "", ["evidence"] = new JsonArray(Enumerable.Range(0, 51).Select(index => (JsonNode?)JsonValue.Create("Evidence " + index)).ToArray()) });
        Check(Matches(V3, report) && WorkflowResult.IsValidStoredV3(report), "V3 validation evidence is not truncated or capped at the legacy 50-item limit.");
        var description = V3["description"]!.GetValue<string>();
        foreach (var text in new[] { "UTF-16", "unique", "startLine", "featureAssessment", "bugAssessment", "planId", "suggestionId", "equal lengths", "24576", "duplicateOf" })
            Check(description.Contains(text, StringComparison.Ordinal), "The generation schema describes the dynamic Host constraint: " + text);
    }

    private static JsonObject Verification() => new() { ["id"] = "tests", ["source"] = "current-run", ["kind"] = "automated-tests", ["status"] = "passed", ["subject"] = "original-pr", ["revisionSha"] = Sha, ["summary"] = "Focused tests passed", ["evidence"] = Strings("test-results.trx; TestRun=" + RunId), ["runId"] = null };
    private static JsonObject Finding() => new() { ["id"] = "finding", ["title"] = "Confirmed defect", ["priority"] = "P2", ["status"] = "open", ["confirmed"] = true, ["path"] = "src/file.cs", ["line"] = 1, ["details"] = "Details", ["impact"] = "Impact", ["trigger"] = "Trigger", ["rootCause"] = "Cause", ["fixSuggestion"] = "Fix", ["evidence"] = Strings("Recorded defect"), ["feedback"] = new JsonObject { ["body"] = "Please fix", ["suggestionId"] = null } };
    private static JsonObject E2e() => new() { ["level"] = "required", ["reason"] = "Runtime check needed", ["question"] = "Does focus return?", ["scenarios"] = Strings("Close the dialog"), ["expectedResults"] = Strings("Focus returns"), ["prerequisites"] = new JsonArray(), ["evidence"] = Strings("Changed window lifetime"), ["readiness"] = "ready" };
    private static JsonObject RelatedIssue() => new() { ["repository"] = "example/repo", ["number"] = 1, ["url"] = "https://github.com/example/repo/issues/1" };
    private static JsonObject Feature(string status) => new() { ["status"] = status, ["summary"] = "Investigation complete", ["reasons"] = Strings("Reason"), ["evidence"] = Strings("Evidence"), ["acceptanceCriteria"] = Strings("Acceptance"), ["questions"] = Strings("Question"), ["alternatives"] = Strings("Alternative"), ["relatedIssue"] = RelatedIssue(), ["planId"] = "plan" };
    private static JsonObject Bug(string status) => new() { ["status"] = status, ["summary"] = "Investigation complete", ["reasons"] = Strings("Reason"), ["evidence"] = Strings("Evidence"), ["questions"] = Strings("Question"), ["relatedIssue"] = RelatedIssue(), ["planId"] = "plan", ["reproduction"] = new JsonObject { ["status"] = "not_run", ["revisionSha"] = null, ["environment"] = "", ["steps"] = new JsonArray(), ["expected"] = "", ["observed"] = "", ["evidence"] = Strings("Recorded reproduction context") } };
    private static JsonObject Report() => new() { ["schemaVersion"] = 3, ["outcome"] = "completed", ["phase"] = "reporting", ["summary"] = "Report", ["assessment"] = null, ["reviewConclusion"] = null, ["verificationEvidence"] = new JsonArray(), ["report"] = new JsonObject { ["complete"] = true, ["rechecked"] = true, ["coverage"] = Strings("Reviewed"), ["limitations"] = new JsonArray() }, ["findings"] = new JsonArray(), ["artifacts"] = new JsonArray(), ["validation"] = new JsonArray(), ["diagnostics"] = new JsonArray(), ["nextActions"] = new JsonArray(), ["review"] = null, ["needsReview"] = false, ["e2eAssessment"] = null, ["featureAssessment"] = null, ["bugAssessment"] = null, ["plans"] = new JsonArray() };
    private static JsonArray Strings(params string[] values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    private static void Pair(JsonObject schema, JsonNode value, string validator, bool expected, string name)
    {
        var method = typeof(WorkflowResult).GetMethod(validator, BindingFlags.NonPublic | BindingFlags.Static)!;
        Check((bool)method.Invoke(null, [value])! == expected, "Host fixture expectation: " + name);
        Check(Matches(schema, value) == expected, "Generated schema expectation: " + name);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Walk(JsonNode? node, Action<JsonObject> visit)
    {
        if (node is JsonObject row) { visit(row); foreach (var pair in row) Walk(pair.Value, visit); }
        else if (node is JsonArray rows) foreach (var child in rows) Walk(child, visit);
    }

    private sealed class SchemaLimits { public int Properties; public int Depth; public int Text; public int Enums; }
    private static void Measure(JsonObject schema, int depth, SchemaLimits limits)
    {
        if (schema["type"]?.GetValue<string>() is "object" or "array") depth++;
        limits.Depth = Math.Max(limits.Depth, depth);
        if (schema["properties"] is JsonObject properties)
        {
            limits.Properties += properties.Count;
            foreach (var pair in properties) { limits.Text += pair.Key.Length; Measure(pair.Value!.AsObject(), depth, limits); }
        }
        if (schema["$defs"] is JsonObject definitions)
            foreach (var pair in definitions) { limits.Text += pair.Key.Length; Measure(pair.Value!.AsObject(), depth, limits); }
        if (schema["enum"] is JsonArray values)
        {
            limits.Enums += values.Count;
            foreach (var value in values.OfType<JsonValue>()) if (value.TryGetValue<string>(out var text)) limits.Text += text.Length;
        }
        if (schema["const"] is JsonValue constant && constant.TryGetValue<string>(out var constText)) limits.Text += constText.Length;
        if (schema["items"] is JsonObject items) Measure(items, depth, limits);
        if (schema["anyOf"] is JsonArray alternatives) foreach (var choice in alternatives.OfType<JsonObject>()) Measure(choice, depth, limits);
    }

    // Independent evaluator for the supported JSON Schema keywords emitted above. It checks each
    // actual generated variant instead of asserting an implementation-specific object path.
    private static bool Matches(JsonObject schema, JsonNode? value)
    {
        if (schema["anyOf"] is JsonArray variants && !variants.OfType<JsonObject>().Any(variant => Matches(variant, value))) return false;
        if (schema["enum"] is JsonArray choices && !choices.Any(choice => JsonNode.DeepEquals(choice, value))) return false;
        if (schema.ContainsKey("const") && !JsonNode.DeepEquals(schema["const"], value)) return false;
        using var json = JsonDocument.Parse(value?.ToJsonString() ?? "null"); var item = json.RootElement;
        var type = schema["type"]?.GetValue<string>();
        if (type is not null && !(type switch { "null" => item.ValueKind == JsonValueKind.Null, "object" => item.ValueKind == JsonValueKind.Object,
            "array" => item.ValueKind == JsonValueKind.Array, "string" => item.ValueKind == JsonValueKind.String, "boolean" => item.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" => item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out _), _ => throw new InvalidOperationException("Unexpected schema type " + type) })) return false;
        if (item.ValueKind == JsonValueKind.String)
        {
            var text = item.GetString()!; var length = text.EnumerateRunes().Count();
            if (schema["minLength"] is JsonValue min && length < min.GetValue<int>() || schema["maxLength"] is JsonValue max && length > max.GetValue<int>()) return false;
            if (schema["pattern"] is JsonValue pattern && !Regex.IsMatch(text, pattern.GetValue<string>(), RegexOptions.CultureInvariant)) return false;
        }
        if (item.ValueKind == JsonValueKind.Number)
        {
            var number = item.GetDecimal();
            if (schema["minimum"] is JsonValue min && number < min.GetValue<int>() || schema["maximum"] is JsonValue max && number > max.GetValue<int>()) return false;
        }
        if (value is JsonArray array)
        {
            if (schema["minItems"] is JsonValue min && array.Count < min.GetValue<int>() || schema["maxItems"] is JsonValue max && array.Count > max.GetValue<int>()) return false;
            if (schema["items"] is JsonObject child && array.Any(row => !Matches(child, row))) return false;
        }
        if (value is JsonObject obj && schema["properties"] is JsonObject properties)
        {
            if (schema["required"] is JsonArray required && required.Any(key => !obj.ContainsKey(key!.GetValue<string>()))) return false;
            if (schema["additionalProperties"]?.GetValue<bool>() == false && obj.Any(pair => !properties.ContainsKey(pair.Key))) return false;
            if (obj.Any(pair => properties[pair.Key] is JsonObject child && !Matches(child, pair.Value))) return false;
        }
        return true;
    }
}
