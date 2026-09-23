using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pulse.Host;

public sealed record ResultValidationIssue(string Path, string Message);

public static partial class WorkflowResult
{
    private const int MaximumValidationIssues = 32;

    /// <summary>Explains rejected model input without mutating it or relaxing historical acceptance rules.</summary>
    public static IReadOnlyList<ResultValidationIssue> ValidationIssues(JsonObject model, int schemaVersion = 3)
    {
        // Generator schemas intentionally require fields omitted by older, valid stored v2 reports.
        if (schemaVersion == 2 && IsVersion(model, 2) && ValidV2(model) || schemaVersion == 3 && IsVersion(model, 3) && ValidV3(model)) return [];
        var issues = new ValidationIssueList();
        if (schemaVersion is not (2 or 3)) { issues.Add("$", "Expected result schema version 2 or 3."); return issues.Finish(); }
        ExplainSchema(model, DiagnosticSchema(schemaVersion), "$", issues);
        ExplainRelationships(model, schemaVersion, issues);
        if (issues.Count == 0) issues.Add("$", "The report does not satisfy the saved version's field types, identities or cross-field constraints.");
        return issues.Finish();
    }

    private sealed class ValidationIssueList
    {
        private readonly List<ResultValidationIssue> rows = [];
        internal bool Truncated { get; private set; }
        internal int Count => rows.Count;
        internal IReadOnlyList<ResultValidationIssue> Rows => rows;
        internal void Add(string path, string message)
        {
            path = Limit(OutputRedactor.Redact(path), 512);
            message = Limit(OutputRedactor.Redact(message), 1024);
            var index = rows.FindIndex(row => row.Path == path);
            if (index >= 0) { rows[index] = new(path, message); return; }
            if (rows.Count == MaximumValidationIssues) { Truncated = true; return; }
            rows.Add(new(path, message));
        }
        internal void Merge(ValidationIssueList other)
        {
            foreach (var row in other.rows) Add(row.Path, row.Message);
            Truncated |= other.Truncated;
        }
        internal IReadOnlyList<ResultValidationIssue> Finish() => Truncated
            ? [.. rows, new("$", "Additional invalid fields were omitted from this diagnostic list. The original response remains available.")]
            : rows.ToArray();
    }

    private static string FieldPath(string path, string name) => Regex.IsMatch(name, @"\A[A-Za-z_][A-Za-z0-9_]*\z")
        ? path + "." + name : path + "[" + JsonSerializer.Serialize(Limit(name, 128)) + "]";

    private static JsonObject DiagnosticSchema(int version)
    {
        var schema = version == 3 ? SchemaV3() : Schema();
        if (version == 3) return schema;
        // The generator emits explicit fields for new reports. Omission remains legal in historic v2.
        foreach (var field in OptionalFields) Optional(schema, field);
        var properties = schema["properties"]!.AsObject();
        var review = properties["review"]!["anyOf"]!.AsArray().OfType<JsonObject>().Single(row => Text(row, "type") == "object");
        Optional(review["properties"]!["suggestions"]!["items"]!.AsObject(), "id");
        foreach (var action in properties["nextActions"]!["items"]!["anyOf"]!.AsArray().OfType<JsonObject>())
        {
            Optional(action, "suggestionIds");
            if (action["properties"]?["pullRequest"] is JsonObject pullRequest) Optional(pullRequest, "sourceHeadSha");
        }
        return schema;
        static void Optional(JsonObject value, string name)
        {
            if (value["required"] is not JsonArray required) return;
            for (var index = required.Count - 1; index >= 0; index--) if (required[index]?.GetValue<string>() == name) required.RemoveAt(index);
        }
    }

    private static bool SchemaTypeMatches(JsonNode? node, string type) => type switch
    {
        "object" => node is JsonObject,
        "array" => node is JsonArray,
        "null" => node is null,
        "string" => node is JsonValue text && text.TryGetValue<string>(out _),
        "boolean" => node is JsonValue flag && flag.TryGetValue<bool>(out _),
        "integer" => node is JsonValue integer && integer.TryGetValue<int>(out _),
        "number" => node is JsonValue number && number.TryGetValue<decimal>(out _),
        _ => true
    };

    private static int BranchPenalty(JsonNode? node, JsonObject schema)
    {
        if (schema["anyOf"] is JsonArray variants) return variants.OfType<JsonObject>().Select(row => BranchPenalty(node, row)).DefaultIfEmpty(0).Min();
        if (schema["type"] is JsonValue type && type.TryGetValue<string>(out var name) && !SchemaTypeMatches(node, name) &&
            !(name == "integer" && node is JsonValue numeric && numeric.TryGetValue<decimal>(out _))) return 1_000_000;
        if (node is not JsonObject value || schema["properties"] is not JsonObject properties) return 0;
        var penalty = 0;
        foreach (var field in new[] { "source", "kind", "status", "confirmed", "level", "readiness" })
        {
            if (properties[field] is not JsonObject rule || !value.ContainsKey(field)) continue;
            if (rule.ContainsKey("const") && !JsonNode.DeepEquals(value[field], rule["const"])) penalty += 1000;
            if (rule["enum"] is JsonArray choices && !choices.Any(choice => JsonNode.DeepEquals(value[field], choice))) penalty += 1000;
        }
        return penalty;
    }

    private static void ExplainSchema(JsonNode? node, JsonObject schema, string path, ValidationIssueList issues)
    {
        if (issues.Truncated) return;
        if (schema["anyOf"] is JsonArray variants)
        {
            ValidationIssueList? best = null; var bestScore = int.MaxValue;
            foreach (var variant in variants.OfType<JsonObject>())
            {
                var candidate = new ValidationIssueList(); ExplainSchema(node, variant, path, candidate);
                if (candidate.Count == 0 && !candidate.Truncated) return;
                var score = BranchPenalty(node, variant) + candidate.Count + (candidate.Truncated ? MaximumValidationIssues : 0);
                if (score < bestScore) { best = candidate; bestScore = score; }
            }
            if (best is not null) issues.Merge(best); else issues.Add(path, "Does not match an allowed result shape.");
            return;
        }
        if (schema["type"] is JsonValue type && type.TryGetValue<string>(out var typeName) && !SchemaTypeMatches(node, typeName))
        {
            issues.Add(path, typeName == "integer" ? "Expected a 32-bit integer JSON value." : "Expected " + typeName + "."); return;
        }
        if (schema.ContainsKey("const") && !JsonNode.DeepEquals(node, schema["const"])) issues.Add(path, "The value must match the required discriminator or schema version.");
        if (schema["enum"] is JsonArray allowed && !allowed.Any(choice => JsonNode.DeepEquals(node, choice))) issues.Add(path, "The value is outside the allowed choices for this field.");
        if (node is JsonObject obj && schema["properties"] is JsonObject properties)
        {
            if (schema["required"] is JsonArray required)
                foreach (var field in required.OfType<JsonValue>().Select(value => value.GetValue<string>()))
                {
                    if (issues.Truncated) break;
                    if (!obj.ContainsKey(field)) issues.Add(FieldPath(path, field), "Required field is missing.");
                }
            foreach (var pair in obj)
            {
                if (issues.Truncated) break;
                if (properties[pair.Key] is JsonObject rule) ExplainSchema(pair.Value, rule, FieldPath(path, pair.Key), issues);
                else if (schema["additionalProperties"]?.GetValue<bool>() == false) issues.Add(FieldPath(path, pair.Key), "Unexpected field; use only the model result contract's fields.");
            }
        }
        else if (node is JsonArray array)
        {
            if (schema["minItems"] is JsonValue min && array.Count < min.GetValue<int>()) issues.Add(path, "At least " + min.GetValue<int>() + " item(s) are required for this state.");
            if (schema["maxItems"] is JsonValue max && array.Count > max.GetValue<int>()) issues.Add(path, "The array exceeds its " + max.GetValue<int>() + " item limit.");
            if (schema["items"] is JsonObject itemSchema)
                for (var index = 0; index < array.Count && !issues.Truncated; index++) ExplainSchema(array[index], itemSchema, path + "[" + index + "]", issues);
        }
        else if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
            {
                if (text.Contains('\0')) issues.Add(path, "Text must not contain a NUL character.");
                if (schema["minLength"] is JsonValue min && text.Length < min.GetValue<int>()) issues.Add(path, "Text is shorter than the required minimum.");
                if (schema["maxLength"] is JsonValue max && text.Length > max.GetValue<int>()) issues.Add(path, "Text exceeds the Host limit of " + max.GetValue<int>() + " UTF-16 code units.");
                if (schema["pattern"] is JsonValue pattern)
                {
                    var expression = pattern.GetValue<string>();
                    // Host identifiers and references match the whole value, including any trailing newline.
                    if (expression.StartsWith('^') && expression.EndsWith('$')) expression = expression[..^1] + @"\z";
                    try
                    {
                        if (!Regex.IsMatch(text, expression, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) issues.Add(path, "Text must satisfy the complete field format and any nonblank requirement.");
                    }
                    catch (RegexMatchTimeoutException) { issues.Add(path, "Text could not be matched within the field-format validation budget."); }
                }
            }
            else if (value.TryGetValue<decimal>(out var number))
            {
                if (schema["minimum"] is JsonValue min && number < min.GetValue<int>()) issues.Add(path, "Number is below the allowed minimum.");
                if (schema["maximum"] is JsonValue max && number > max.GetValue<int>()) issues.Add(path, "Number exceeds the allowed maximum.");
            }
        }
    }

    private static IEnumerable<(JsonObject Row, string Path)> ObjectRows(JsonObject model, string field, string parent = "$")
    {
        if (model[field] is not JsonArray rows) yield break;
        for (var index = 0; index < rows.Count; index++) if (rows[index] is JsonObject row) yield return (row, FieldPath(parent, field) + "[" + index + "]");
    }
    private static void UniqueFieldIds(JsonObject parent, string field, string parentPath, ValidationIssueList issues)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (row, path) in ObjectRows(parent, field, parentPath))
        {
            if (issues.Truncated) break;
            if (row["id"] is JsonValue value && value.TryGetValue<string>(out var id) && !seen.Add(id)) issues.Add(path + ".id", "IDs must be unique within " + FieldPath(parentPath, field) + ".");
        }
    }
    private static void ObjectBudget(JsonNode? node, string path, int maximum, ValidationIssueList issues)
    {
        if (node is JsonObject value && EncodedBytes(value) > maximum) issues.Add(path, "The complete object exceeds " + maximum + " bytes of compact escaped UTF-8 JSON; keep the full actionable object within this budget.");
    }
    private static HashSet<string> IdSet(JsonObject? parent, string field) => parent is not null && parent[field] is JsonArray values
        ? values.OfType<JsonObject>().Select(row => Text(row, "id")).Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal) : [];

    private static void ExplainRelationships(JsonObject model, int version, ValidationIssueList issues)
    {
        if (issues.Truncated) return;
        foreach (var field in new[] { "findings", "validation", "verificationEvidence" }) UniqueFieldIds(model, field, "$", issues);
        foreach (var (row, path) in ObjectRows(model, "verificationEvidence"))
        {
            if (issues.Truncated) return;
            if (row["runId"] is not null && Text(row, "source") != "prior-run") issues.Add(path + ".runId", "Must be null for current-run, ci or author evidence. Only prior-run may reference an earlier Pulse task; put TRX, test-run or CI identifiers in evidence text.");
            if (Text(row, "status") is "passed" or "failed" && row["evidence"] is JsonArray { Count: 0 }) issues.Add(path + ".evidence", "Passed or failed observations require supporting evidence.");
        }
        var review = model["review"] as JsonObject;
        var suggestionIds = IdSet(review, "suggestions");
        if (review is not null)
        {
            UniqueFieldIds(review, "suggestions", "$.review", issues);
            foreach (var (row, path) in ObjectRows(review, "suggestions", "$.review"))
                if (row["line"] is JsonValue end && end.TryGetValue<int>(out var line) && row["startLine"] is JsonValue start && start.TryGetValue<int>(out var first) && (first > line || (long)line - first > 999))
                    issues.Add(path + ".startLine", "startLine must not exceed line, and the inclusive range must contain at most 1000 lines.");
        }
        foreach (var (row, path) in ObjectRows(model, "nextActions"))
        {
            if (issues.Truncated) return;
            if (row["suggestionIds"] is not JsonArray ids) continue;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < ids.Count; index++)
                if (ids[index] is JsonValue value && value.TryGetValue<string>(out var id))
                {
                    if (!seen.Add(id)) issues.Add(path + ".suggestionIds[" + index + "]", "Suggestion references must be unique within this proposal.");
                    else if (!suggestionIds.Contains(id)) issues.Add(path + ".suggestionIds[" + index + "]", "Must reference an existing review.suggestions[].id.");
                }
        }
        ObjectBudget(model["verificationRecommendation"], "$.verificationRecommendation", MaximumVerificationRecommendationBytes, issues);
        if (version != 3 || issues.Truncated) return;

        UniqueFieldIds(model, "plans", "$", issues);
        foreach (var (row, path) in ObjectRows(model, "findings"))
        {
            if (issues.Truncated) return;
            if (row["confirmed"] is JsonValue flag && flag.TryGetValue<bool>(out var confirmed) && confirmed)
            {
                if (Text(row, "status") == "unverified") issues.Add(path + ".status", "An unverified observation must not be marked confirmed.");
                if (row["evidence"] is JsonArray { Count: 0 }) issues.Add(path + ".evidence", "A confirmed finding requires supporting evidence.");
            }
            if (row["feedback"] is JsonObject feedback && feedback["suggestionId"] is JsonValue reference && reference.TryGetValue<string>(out var id) && !suggestionIds.Contains(id))
                issues.Add(path + ".feedback.suggestionId", "Must reference an existing review.suggestions[].id, or be null for ordinary feedback.");
        }
        var plans = ObjectRows(model, "plans").Where(pair => Text(pair.Row, "id").Length > 0).GroupBy(pair => Text(pair.Row, "id"), StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Row, StringComparer.Ordinal);
        foreach (var (row, path) in ObjectRows(model, "plans")) { if (issues.Truncated) return; ObjectBudget(row, path, MaximumPlanBytes, issues); }
        var feature = model["featureAssessment"] as JsonObject;
        var bug = model["bugAssessment"] as JsonObject;
        if (feature is not null && bug is not null) issues.Add("$.bugAssessment", "Feature and Bug assessments are mutually exclusive; keep the irrelevant assessment null.");
        if (feature is not null)
        {
            RelatedIssue(feature["relatedIssue"], "$.featureAssessment.relatedIssue", issues);
            if (feature["planId"] is JsonValue id && id.TryGetValue<string>(out var key) && (!plans.TryGetValue(key, out var plan) || Text(plan, "kind") != "feature-implement"))
                issues.Add("$.featureAssessment.planId", "Must reference an existing feature-implement plan.");
        }
        if (bug is not null)
        {
            RelatedIssue(bug["relatedIssue"], "$.bugAssessment.relatedIssue", issues);
            if (bug["planId"] is JsonValue id && id.TryGetValue<string>(out var key) && (!plans.TryGetValue(key, out var plan) || Text(plan, "kind") == "feature-implement"))
                issues.Add("$.bugAssessment.planId", "Must reference an existing non-feature implementation, reproduction or verification plan.");
            if (Text(bug, "status") == "confirmed" && !ObjectRows(model, "findings").Any(pair => pair.Row["confirmed"] is JsonValue flag && flag.TryGetValue<bool>(out var value) && value))
                issues.Add("$.bugAssessment.status", "A confirmed Bug assessment requires at least one confirmed finding.");
        }
        foreach (var (row, path) in ObjectRows(model, "nextActions"))
        {
            if (issues.Truncated) return;
            if (Text(row, "kind") == "start-task" && (!plans.TryGetValue(Text(row, "planId"), out var plan) || Text(plan, "kind") != Text(row, "taskKind")))
                issues.Add(path + ".planId", "Must reference an existing plan whose kind matches taskKind.");
            if (Text(row, "kind") == "close-as-duplicate")
            {
                RelatedIssue(row["duplicateOf"], path + ".duplicateOf", issues);
                var assessment = feature ?? bug;
                if (assessment is null || Text(assessment, "status") != "duplicate" || !JsonNode.DeepEquals(assessment["relatedIssue"], row["duplicateOf"]))
                    issues.Add(path + ".duplicateOf", "Must exactly match relatedIssue on a duplicate Feature or Bug assessment.");
            }
        }
        if (model["e2eAssessment"] is JsonObject e2e)
        {
            ObjectBudget(e2e, "$.e2eAssessment", MaximumVerificationRecommendationBytes, issues);
            if (e2e["scenarios"] is JsonArray scenarios && e2e["expectedResults"] is JsonArray expected && scenarios.Count != expected.Count)
                issues.Add("$.e2eAssessment.expectedResults", "Must have exactly one expected result per scenario, in the same order.");
        }
    }

    private static void RelatedIssue(JsonNode? value, string path, ValidationIssueList issues)
    {
        if (value is JsonObject row && Exact(row, "repository", "number", "url") && String(row, "repository", 256, true) &&
            PositiveInteger(row["number"]) && String(row, "url", 2048, true) &&
            Regex.IsMatch(Text(row, "repository"), @"\A[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}\z") && !ValidRelatedIssueV3(value))
            issues.Add(path + ".url", "Must be the HTTPS github.com issue URL matching repository and number, with no credentials, nondefault port, query or fragment.");
    }

    private static IReadOnlyList<ResultValidationIssue> DuplicateFieldIssues(JsonElement element)
    {
        var issues = new ValidationIssueList();
        Visit(element, "$");
        return issues.Finish();
        void Visit(JsonElement node, string path)
        {
            if (issues.Truncated) return;
            if (node.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in node.EnumerateObject())
                {
                    if (issues.Truncated) break;
                    var current = FieldPath(path, property.Name);
                    if (!names.Add(property.Name)) issues.Add(current, "Duplicate JSON property name is not allowed.");
                    Visit(property.Value, current);
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                var index = 0; foreach (var item in node.EnumerateArray()) { if (issues.Truncated) break; Visit(item, path + "[" + index++ + "]"); }
            }
        }
    }

    private static void AddValidationDiagnostics(JsonObject result, IReadOnlyList<ResultValidationIssue>? issues)
    {
        if (issues is null) return;
        foreach (var issue in issues) AddDiagnostic(result, "INVALID_RESULT_FIELD", Limit(OutputRedactor.Redact(issue.Path), 512) + ": " + Limit(OutputRedactor.Redact(issue.Message), 1024), "inspectResult");
    }

    /// <summary>Older rejected reports gain explanations in their read projection; saved outcomes and bytes remain untouched.</summary>
    private static void ExplainRetainedInvalidResult(JsonObject projected)
    {
        if (!(IsVersion(projected, 2) || IsVersion(projected, 3)) || Text(projected, "outcome") == "completed" ||
            projected["diagnostics"] is not JsonArray diagnostics || !diagnostics.OfType<JsonObject>().Any(row => Text(row, "code") == "INVALID_RESULT") ||
            diagnostics.OfType<JsonObject>().Any(row => Text(row, "code") == "INVALID_RESULT_FIELD")) return;
        var raw = Text(projected, "rawOutput");
        if (raw.Length == 0 || raw.Length > ResultLimits.RawDiagnosticCharacters) return;
        try
        {
            var candidate = raw.Trim();
            if (candidate.StartsWith("```", StringComparison.Ordinal))
            {
                var start = candidate.IndexOf('\n'); var end = candidate.LastIndexOf("```", StringComparison.Ordinal);
                if (start >= 0 && end > start) candidate = candidate[(start + 1)..end];
            }
            using var document = JsonDocument.Parse(candidate, new JsonDocumentOptions { MaxDepth = 32 });
            IReadOnlyList<ResultValidationIssue> issues = !NoDuplicateFields(document.RootElement) ? DuplicateFieldIssues(document.RootElement)
                : JsonNode.Parse(candidate) is JsonObject model ? ValidationIssues(model, IsVersion(projected, 3) ? 3 : 2) : [];
            AddValidationDiagnostics(projected, issues);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            // Historical raw output may be truncated; don't invent a new diagnosis from a partial excerpt.
        }
    }
}
