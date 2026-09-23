using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Pulse.Host.Tests;

/// <summary>Offline regressions from a sanitized saved report; never opens or rewrites a real run.</summary>
internal static class ResultContractRegressionScenarios
{
    internal static Task RunAllAsync()
    {
        TrxRunIdentifiersReceiveFieldDiagnostics();
        ExistingInvalidResultIsExplainedWithoutRewriting();
        V2AdmissionKeepsHistoricalOptionalFields();
        V2MixedErrorsDoNotInvalidateAllowedOmissions();
        InvalidEnvelopesAndDuplicateFieldsAreLocated();
        InvalidScalarFieldsAndDiagnosticNamesRemainSafe();
        CorrectedEvidenceKeepsTheOriginalLimits();
        CrossFieldFailuresAreLocatedWithoutRepair();
        ByteBudgetsAreDiagnosedSeparatelyFromArrayLength();
        DiagnosticCountIsBounded();
        NormalizationIsNotStructuralRejection();
        Console.WriteLine("PASS result contract regressions: sanitized TRX identity misuse, exact field diagnostics, references, byte budgets, bounded errors and unchanged verification limits (offline)");
        return Task.CompletedTask;
    }

    private static void TrxRunIdentifiersReceiveFieldDiagnostics()
    {
        var expected = Enumerable.Range(1, 4).Select(index => $"$.verificationEvidence[{index}].runId").ToHashSet(StringComparer.Ordinal);
        foreach (var version in new[] { 2, 3 })
        {
            var model = version == 3 ? RealCase() : AsV2(RealCase()); var original = model.ToJsonString();
            var issues = WorkflowResult.ValidationIssues(model, version);
            Check(issues.Select(issue => issue.Path).ToHashSet(StringComparer.Ordinal).SetEquals(expected), "The sanitized case has exactly four source/runId contract errors, not an absent build or test result.");
            var rejected = WorkflowResult.FromModel(model.ToJsonString(), RealTask(), "succeeded", 0, null, null, expectedSchemaVersion: version);
            Check(HasDiagnostic(rejected, "INVALID_RESULT") && Text(rejected, "outcome") == "blocked" && (version == 2 || rejected["report"]?["complete"]?.GetValue<bool>() == false), "An invalid identity is rejected; no evidence is silently corrected or admitted.");
            var fields = rejected["diagnostics"]!.AsArray().OfType<JsonObject>().Where(row => Text(row, "code") == "INVALID_RESULT_FIELD").Select(row => Text(row, "message")).ToArray();
            foreach (var path in expected) Check(fields.Any(message => message.StartsWith(path + ":", StringComparison.Ordinal)), "Invalid reports identify the exact field " + path);
            Check(Text(rejected, "rawOutput").Contains("9097b8ba-379b-40eb-a729-dd72e7b8d4de", StringComparison.Ordinal), "The original external test-run identifier remains available in diagnostic output.");
            Check(model.ToJsonString() == original, "Validation and normalization must not mutate the source fixture.");
        }
    }

    private static void V2AdmissionKeepsHistoricalOptionalFields()
    {
        var model = AsV2(CorrectedRealCase());
        foreach (var key in new[] { "assessment", "reviewConclusion", "verificationEvidence", "verificationRecommendation" }) model.Remove(key);
        model["review"]!["suggestions"]!.AsArray().Add(new JsonObject { ["path"] = "src/example.cs", ["line"] = 2, ["startLine"] = 2, ["side"] = "RIGHT", ["body"] = "Historical feedback.", ["replacement"] = "Fixed();" });
        model["nextActions"]!.AsArray().Add(new JsonObject { ["kind"] = "approve", ["reason"] = "Review this historical draft.", ["body"] = "Saved review text." });
        Check(WorkflowResult.ValidationIssues(model, 2).Count == 0, "Stricter new-generation schemas cannot remove historical V2 optional fields, suggestion IDs or association-list compatibility.");
        var accepted = WorkflowResult.FromModel(model.ToJsonString(), RealTask(), "succeeded", 0, null, null, expectedSchemaVersion: 2);
        Check(!HasDiagnostic(accepted, "INVALID_RESULT"), "Historical V2 admission stays independent of the stricter generator subset.");
    }

    private static void ExistingInvalidResultIsExplainedWithoutRewriting()
    {
        var saved = Normalize(RealCase(), RealTask());
        var diagnostics = saved["diagnostics"]!.AsArray();
        for (var index = diagnostics.Count - 1; index >= 0; index--)
            if (diagnostics[index] is JsonObject row && Text(row, "code") == "INVALID_RESULT_FIELD") diagnostics.RemoveAt(index);
        var original = saved.ToJsonString(); var projected = WorkflowResult.WithProposalIds(saved);
        var paths = projected["diagnostics"]!.AsArray().OfType<JsonObject>().Where(row => Text(row, "code") == "INVALID_RESULT_FIELD").Select(row => Text(row, "message")).ToArray();
        Check(paths.Length == 4 && paths.Any(message => message.StartsWith("$.verificationEvidence[1].runId:", StringComparison.Ordinal)), "A retained complete raw response receives current field explanations in its read projection.");
        Check(Text(projected, "outcome") == "blocked" && !WorkflowResult.IsFinalReportComplete(projected), "Projection cannot promote or repair a rejected historical result.");
        Check(saved.ToJsonString() == original && Text(projected, "rawOutput") == Text(saved, "rawOutput"), "Both the stored object and retained original response stay unchanged.");
        Check(WorkflowResult.WithProposalIds(projected)["diagnostics"]!.AsArray().Count == projected["diagnostics"]!.AsArray().Count, "Repeated projection cannot duplicate field explanations.");
        var partial = saved.DeepClone().AsObject(); partial["rawOutput"] = "{\"schemaVersion\":3,";
        Check(!HasDiagnostic(WorkflowResult.WithProposalIds(partial), "INVALID_RESULT_FIELD"), "A truncated historical excerpt is not enough to infer an additional format diagnosis.");
    }

    private static void CorrectedEvidenceKeepsTheOriginalLimits()
    {
        var model = CorrectedRealCase(); var original = model.ToJsonString(); var task = RealTask();
        Check(WorkflowResult.ValidationIssues(model).Count == 0, "Null Pulse links with TRX IDs preserved as evidence satisfy the canonical contract.");
        var accepted = Normalize(model, task);
        Check(!HasDiagnostic(accepted, "INVALID_RESULT") && WorkflowResult.IsFinalReportComplete(accepted), "The repaired synthetic report retains complete build-tests evidence.");
        Check(Text(accepted["assessment"]!.AsObject(), "status") == "inconclusive" && Text(accepted["e2eAssessment"]!.AsObject(), "level") == "required", "Accepting the result cannot promote inconclusive runtime evidence into a passing product assessment.");
        Check(Text(WorkflowResult.Recommendation(accepted, task), "kind") == "run-e2e", "CJK runtime verification remains the recommendation after the structural error is corrected.");
        Check(accepted["verificationEvidence"]!.AsArray().Count == 6 && accepted["verificationEvidence"]!.AsArray().All(row => row!["runId"] is null), "All build/test/runtime records survive without fake Pulse links.");
        Check(model.ToJsonString() == original, "The repaired fixture is not mutated by acceptance.");
    }

    private static void V2MixedErrorsDoNotInvalidateAllowedOmissions()
    {
        var model = AsV2(CorrectedRealCase());
        foreach (var key in new[] { "assessment", "reviewConclusion", "verificationEvidence", "verificationRecommendation" }) model.Remove(key);
        model["review"]!["suggestions"]!.AsArray().Add(new JsonObject { ["path"] = "src/example.cs", ["line"] = 2, ["startLine"] = 2, ["side"] = "RIGHT", ["body"] = "Historical feedback.", ["replacement"] = "Fixed();" });
        model["nextActions"]!.AsArray().Add(new JsonObject { ["kind"] = "approve", ["reason"] = "Review the historical result.", ["body"] = "Saved review text." });
        model["nextActions"]!.AsArray().Add(new JsonObject
        {
            ["kind"] = "create-pr", ["reason"] = "Inspect the saved historical draft.", ["body"] = "",
            ["pullRequest"] = new JsonObject { ["head"] = "sample:historical-fix", ["base"] = "main", ["title"] = "Historical draft", ["body"] = "Saved candidate description.", ["draft"] = true }
        });
        Check(WorkflowResult.ValidationIssues(model, 2).Count == 0, "All legacy omissions in this diagnostic fixture are accepted by the actual V2 contract.");
        model["summary"] = " \t "; var original = model.ToJsonString();
        var issues = WorkflowResult.ValidationIssues(model, 2);
        Check(issues.Count == 1 && issues[0].Path == "$.summary", "One invalid V2 summary must not turn allowed historical field omissions into extra schema errors.");
        var rejected = WorkflowResult.FromModel(original, RealTask(), "succeeded", 0, null, null, expectedSchemaVersion: 2);
        var fields = FieldMessages(rejected);
        Check(HasDiagnostic(rejected, "INVALID_RESULT") && fields.Count == 1 && fields[0].StartsWith("$.summary:", StringComparison.Ordinal), "The saved invalid result exposes only its actual summary violation.");
        Check(model.ToJsonString() == original, "Compatibility diagnostics must not fill omitted historical metadata or rewrite the model.");
    }

    private static void InvalidEnvelopesAndDuplicateFieldsAreLocated()
    {
        foreach (var output in new[] { "null", "[]", "true", "\"plain final text\"", "{\"schemaVersion\":3,", "" })
        {
            var rejected = WorkflowResult.FromModel(output, RealTask(), "succeeded", 0, null, null, expectedSchemaVersion: 3);
            Check(HasDiagnostic(rejected, "INVALID_RESULT") && FieldMessages(rejected).Any(message => message.StartsWith("$", StringComparison.Ordinal)), "A malformed or non-object final envelope receives a bounded root diagnostic rather than throwing.");
            Check(Text(rejected, "rawOutput") == output, "The original invalid envelope remains available for diagnosis.");
            Check(!WorkflowResult.IsFinalReportComplete(rejected), "An invalid envelope cannot become a completed report.");
        }
        var model = WorkflowV3Scenarios.Model(); model["findings"]!.AsArray().Add(WorkflowV3Scenarios.Finding("duplicate-property"));
        var original = model.ToJsonString();
        var duplicate = original.Replace("\"id\":\"duplicate-property\"", "\"id\":\"duplicate-property\",\"id\":\"second-value\"", StringComparison.Ordinal);
        Check(duplicate != original, "The duplicate-property fixture contains both literal JSON fields.");
        var invalid = WorkflowResult.FromModel(duplicate, WorkflowV3Scenarios.TaskContext(), "succeeded", 0, null, null, expectedSchemaVersion: 3);
        Check(HasDiagnostic(invalid, "INVALID_RESULT") && FieldMessages(invalid).Any(message => message.StartsWith("$.findings[0].id:", StringComparison.Ordinal) && message.Contains("Duplicate JSON property", StringComparison.Ordinal)), "Duplicate field diagnostics retain the exact nested array location without materializing an ambiguous object.");
        Check(Text(invalid, "rawOutput") == duplicate, "Both duplicate property values remain in retained raw output.");
        var deep = string.Concat(Enumerable.Repeat("{\"nested\":", 34)) + "0" + new string('}', 34);
        var tooDeep = WorkflowResult.FromModel(deep, RealTask(), "succeeded", 0, null, null, expectedSchemaVersion: 3);
        Check(HasDiagnostic(tooDeep, "INVALID_RESULT") && FieldMessages(tooDeep).Any(message => message.Contains("32 levels", StringComparison.Ordinal)), "Excess nesting receives a parser budget diagnostic instead of entering recursive model validation.");
    }

    private static void InvalidScalarFieldsAndDiagnosticNamesRemainSafe()
    {
        var nul = WorkflowV3Scenarios.Model(); nul["summary"] = "Recorded\0summary";
        Located(nul, "$.summary", "NUL text must identify its field and remain rejected.");
        var huge = WorkflowV3Scenarios.Model(); var finding = WorkflowV3Scenarios.Finding("oversize-line"); finding["line"] = 2147483648L; huge["findings"]!.AsArray().Add(finding);
        Located(huge, "$.findings[0].line", "An integer outside Int32 remains invalid and retains its finding location.");
        var wrongType = WorkflowV3Scenarios.Model(); wrongType["verificationEvidence"] = new JsonObject { ["unexpected"] = true };
        Located(wrongType, "$.verificationEvidence", "A malformed array field must not cause relationship diagnostics to throw.");
        const string secret = "ghp_1234567890abcdef1234567890abcdef123456";
        var hostile = WorkflowV3Scenarios.Model(); hostile["unexpected field " + secret + new string('x', 700)] = "Untrusted extra field.";
        var rejected = Normalize(hostile); var fields = FieldMessages(rejected);
        Check(HasDiagnostic(rejected, "INVALID_RESULT") && fields.Count > 0 && fields.All(message => message.Length <= 1538), "Untrusted property names keep diagnostic paths and messages within the bounded sink limits.");
        Check(!rejected.ToJsonString().Contains(secret, StringComparison.Ordinal) && rejected.ToJsonString().Contains("[REDACTED]", StringComparison.Ordinal), "Credential-shaped content in diagnostic paths and original output is redacted before storage and display.");
    }

    private static void CrossFieldFailuresAreLocatedWithoutRepair()
    {
        var duplicate = WorkflowV3Scenarios.Model(); duplicate["findings"] = new JsonArray(WorkflowV3Scenarios.Finding("same"), WorkflowV3Scenarios.Finding("same"));
        Located(duplicate, "$.findings", "Duplicate finding identities are a Host cross-item constraint.");
        var link = WorkflowV3Scenarios.Model(); var finding = WorkflowV3Scenarios.Finding("linked"); finding["feedback"]!["suggestionId"] = "missing"; link["findings"]!.AsArray().Add(finding);
        Located(link, "$.findings[0].feedback.suggestionId", "A syntactically valid suggestion ID still must resolve.");
        var range = WorkflowV3Scenarios.Model(); range["review"]!["suggestions"]!.AsArray().Add(new JsonObject { ["id"] = "range", ["path"] = "src/example.cs", ["line"] = 2, ["startLine"] = 3, ["side"] = "RIGHT", ["body"] = "A comment.", ["replacement"] = "Fixed();" });
        Located(range, "$.review.suggestions[0]", "Positive integer bounds do not establish the ordering of a line range.");
        var plan = WorkflowV3Scenarios.Model("feature-research"); plan["nextActions"]![0]!["taskKind"] = "issue-fix";
        Located(plan, "$.nextActions[0]", "A saved plan must match the requested task kind.", WorkflowV3Scenarios.TaskContext("feature-research"));
        var pairs = WorkflowV3Scenarios.Model(); pairs["e2eAssessment"] = WorkflowV3Scenarios.E2e("required"); pairs["e2eAssessment"]!["expectedResults"] = new JsonArray();
        Located(pairs, "$.e2eAssessment", "Scenarios and expectations must have matching cardinality.");
    }

    private static void ByteBudgetsAreDiagnosedSeparatelyFromArrayLength()
    {
        var model = WorkflowV3Scenarios.Model("feature-research");
        model["plans"]![0]!["evidence"] = new JsonArray(Enumerable.Range(0, 8).Select(_ => (JsonNode?)JsonValue.Create(new string('\u754c', 1000))).ToArray());
        Located(model, "$.plans[0]", "Eight valid evidence strings can exceed the escaped UTF-8 aggregate plan budget.", WorkflowV3Scenarios.TaskContext("feature-research"));
        var e2e = WorkflowV3Scenarios.Model(); e2e["e2eAssessment"]!["evidence"] = new JsonArray(Enumerable.Range(0, 8).Select(_ => (JsonNode?)JsonValue.Create(new string('\u754c', 1000))).ToArray());
        Located(e2e, "$.e2eAssessment", "E2E assessment uses its own aggregate byte budget even with a short array.");
    }

    private static void DiagnosticCountIsBounded()
    {
        var model = CorrectedRealCase(); var seed = model["verificationEvidence"]![1]!.AsObject(); var rows = new JsonArray();
        for (var index = 0; index < 40; index++) { var row = seed.DeepClone().AsObject(); row["id"] = "external-" + index; row["runId"] = "9097b8ba-379b-40eb-a729-dd72e7b8d4de"; rows.Add(row); }
        model["verificationEvidence"] = rows; var issues = WorkflowResult.ValidationIssues(model);
        Check(issues.Count <= 33 && issues.Count > 1 && issues.Any(issue => issue.Path == "$"), "A large malformed report receives bounded field issues and an explicit truncation notice.");
        Check(HasDiagnostic(Normalize(model, RealTask()), "INVALID_RESULT"), "Limiting diagnostic output never makes a malformed report acceptable.");
    }

    private static void NormalizationIsNotStructuralRejection()
    {
        var model = CorrectedRealCase(); model["validation"]![2]!["status"] = "not_run"; model["validation"]![2]!["details"] = "The configured test runner was unavailable."; model["validation"]![2]!["evidence"] = new JsonArray();
        Check(WorkflowResult.ValidationIssues(model).Count == 0, "An honest incomplete check is structurally valid.");
        var blocked = Normalize(model, RealTask());
        Check(!HasDiagnostic(blocked, "INVALID_RESULT") && HasDiagnostic(blocked, "WORKFLOW_CHECKS_INCOMPLETE") && Text(blocked, "outcome") == "blocked", "Completion requirements produce workflow diagnostics without calling valid JSON a schema error.");
        var cancelled = WorkflowResult.FromModel(CorrectedRealCase().ToJsonString(), RealTask(), "cancelled", null, null, null, expectedSchemaVersion: 3);
        Check(Text(cancelled, "outcome") == "cancelled" && !WorkflowResult.IsFinalReportComplete(cancelled), "Structural acceptance does not override the saved process outcome.");
    }

    private static JsonObject RealCase([CallerFilePath] string source = "") => JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "result-contract-real-case.fixture.json")))!.AsObject();
    private static JsonObject CorrectedRealCase()
    {
        var model = RealCase();
        foreach (var row in model["verificationEvidence"]!.AsArray().OfType<JsonObject>().Where(row => row["runId"] is not null)) { row["evidence"]!.AsArray().Add("TRX TestRun.id: " + Text(row, "runId")); row["runId"] = null; }
        return model;
    }
    private static JsonObject AsV2(JsonObject model)
    {
        foreach (var field in new[] { "report", "e2eAssessment", "featureAssessment", "bugAssessment", "plans" }) model.Remove(field);
        model["schemaVersion"] = 2; model["verificationRecommendation"] = null; return model;
    }
    private static JsonObject RealTask()
    {
        var task = WorkflowV3Scenarios.TaskContext(); task["expectedHeadSha"] = "595de8d162df8bc0d06571b22838bfe29ac64e89"; task["reviewOptions"]!["mode"] = "build-tests"; return task;
    }
    private static void Located(JsonObject model, string path, string message, JsonObject? task = null)
    {
        var original = model.ToJsonString(); var issues = WorkflowResult.ValidationIssues(model);
        Check(issues.Any(issue => issue.Path.StartsWith(path, StringComparison.Ordinal)), message + " Expected path: " + path);
        Check(HasDiagnostic(Normalize(model, task), "INVALID_RESULT"), message + " Invalid input remains rejected.");
        Check(model.ToJsonString() == original, message + " Diagnosis is read-only.");
    }
    private static JsonObject Normalize(JsonObject model, JsonObject? task = null) => WorkflowResult.FromModel(model.ToJsonString(), task ?? WorkflowV3Scenarios.TaskContext(), "succeeded", 0, null, null, expectedSchemaVersion: 3);
    private static bool HasDiagnostic(JsonObject result, string code) => result["diagnostics"]!.AsArray().OfType<JsonObject>().Any(row => Text(row, "code") == code);
    private static IReadOnlyList<string> FieldMessages(JsonObject result) => result["diagnostics"]!.AsArray().OfType<JsonObject>().Where(row => Text(row, "code") == "INVALID_RESULT_FIELD").Select(row => Text(row, "message")).ToArray();
    private static string Text(JsonObject row, string field) => row[field]?.GetValue<string>() ?? "";
    private static void Check(bool condition, string message) => CoreScenarios.Check(condition, message);
}
