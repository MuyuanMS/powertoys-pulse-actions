using System.Text;
using System.Text.Json.Nodes;

namespace Pulse.Host.Tests;

/// <summary>Task admission, bundled snapshots and adapter schema selection without executing a workflow.</summary>
internal static class TaskWorkflowV3Scenarios
{
    private static readonly (string Kind, string File, string Field, string[] Checks)[] NewWorkflows =
    [
        ("feature-research", TaskPrompt.DefaultFeatureResearchPrompt, "featureResearchPrompt", ["requirements", "existing-capabilities", "feasibility", "next-step"]),
        ("bug-investigation", TaskPrompt.DefaultBugInvestigationPrompt, "bugInvestigationPrompt", ["context", "investigation", "local-review"]),
        ("feature-implement", TaskPrompt.DefaultFeatureImplementPrompt, "featureImplementPrompt", ["requirements", "implementation", "verification"]),
        ("issue-verify", TaskPrompt.DefaultIssueVerifyPrompt, "issueVerifyPrompt", ["setup", "verification"])
    ];

    internal static Task RunAllAsync()
    {
        PublicResearchRequiresAnIssue();
        LinkedImplementationIsHostOnly();
        ValidatesEveryPlanSourceIdentity();
        CandidatePlanSourcesRemainHostOwned();
        ContextBudgetsFollowAdmissionAuthority();
        ValidationPreservesInputAndExecutionBoundaries();
        RequiredChecksAndInstructionsMatchTheWorkflow();
        BothAdaptersReceiveTheAcceptedSchema();
        AdapterLimitsFollowTheAcceptedVersion();
        using (var fixture = new Fixture()) FixedBundledSnapshotsIgnoreVirtualSelections(fixture);
        Console.WriteLine("PASS v3 task workflows: public/internal boundaries, immutable plan identity, context budgets, bundled snapshots, exact checks, schema delivery and transport budgets (offline)");
        return Task.CompletedTask;
    }

    private static void PublicResearchRequiresAnIssue()
    {
        foreach (var kind in new[] { "feature-research", "bug-investigation" })
        {
            var input = IssueTask(kind);
            var accepted = Protocol.ValidateTask(input);
            Check(accepted["actionKind"]!.GetValue<string>() == kind && accepted["target"]!["type"]!.GetValue<string>() == "issue",
                "Both public research workflows must accept a fixed Issue target.");
            var missing = input.DeepClone().AsObject(); missing.Remove("target");
            Expect("INVALID_REQUEST", () => Protocol.ValidateTask(missing));
            foreach (var target in new JsonNode?[] { null, new JsonArray(), new JsonObject { ["type"] = "pr", ["number"] = 42 },
                new JsonObject { ["type"] = "issue", ["number"] = 0 }, new JsonObject { ["type"] = "issue", ["number"] = "42" } })
            {
                var bad = input.DeepClone().AsObject(); bad["target"] = target?.DeepClone();
                Expect("INVALID_REQUEST", () => Protocol.ValidateTask(bad));
            }
            var reviewScope = input.DeepClone().AsObject(); reviewScope["reviewOptions"] = new JsonObject { ["mode"] = "static" };
            Expect("INVALID_REQUEST", () => Protocol.ValidateTask(reviewScope));
        }
    }

    private static void LinkedImplementationIsHostOnly()
    {
        foreach (var kind in new[] { "feature-implement", "issue-verify" })
        {
            var unlinked = IssueTask(kind);
            Expect("INVALID_REQUEST", () => Protocol.ValidateTask(unlinked));
            Expect("INVALID_REQUEST", () => Protocol.ValidateTask(unlinked, allowFollowUp: true));
            var linked = LinkedTask(kind);
            Expect("INVALID_REQUEST", () => Protocol.ValidateTask(linked));
            Check(Protocol.ValidateTask(linked, allowFollowUp: true)["planSource"] is JsonObject,
                "Only Host-prepared implementation and verification tasks may carry saved plan provenance.");
            var nullSource = linked.DeepClone().AsObject(); nullSource["planSource"] = null;
            Expect("INVALID_REQUEST", () => Protocol.ValidateTask(nullSource, allowFollowUp: true));
            var wrongTarget = linked.DeepClone().AsObject(); wrongTarget["target"]!["type"] = "pr";
            wrongTarget["planSource"]!["target"]!["type"] = "pr"; wrongTarget["expectedHeadSha"] = new string('b', 40);
            Expect("INVALID_REQUEST", () => Protocol.ValidateTask(wrongTarget, allowFollowUp: true));
        }
        foreach (var kind in new[] { "feature-research", "bug-investigation", "issue-fix", "reproduction-setup" })
        {
            var linked = IssueTask(kind); linked["planSource"] = PlanSource(linked);
            Expect("INVALID_REQUEST", () => Protocol.ValidateTask(linked));
            if (kind is "issue-fix" or "reproduction-setup") Protocol.ValidateTask(linked, allowFollowUp: true);
            else Expect("INVALID_REQUEST", () => Protocol.ValidateTask(linked, allowFollowUp: true));
        }
        foreach (var kind in new[] { "issue-fix", "reproduction-setup" })
            Check(Protocol.ValidateTask(IssueTask(kind))["planSource"] is null, "Existing public local-fix and reproduction tasks must remain usable without a source plan.");
    }

    private static void ValidatesEveryPlanSourceIdentity()
    {
        foreach (var sha in new string?[] { null, new string('a', 40), new string('B', 40) })
        {
            var input = LinkedTask(); input["planSource"]!["revisionSha"] = sha;
            Protocol.ValidateTask(input, allowFollowUp: true);
        }
        var casing = LinkedTask(); casing["repository"] = "Microsoft/PowerToys"; casing["planSource"]!["repository"] = "MICROSOFT/POWERTOYS";
        Check(Protocol.ValidateTask(casing, allowFollowUp: true)["repository"]!.GetValue<string>() == "microsoft/powertoys", "Equivalent repository casing may normalize without changing plan identity.");

        foreach (var field in new[] { "parentRunId", "parentResultFingerprint", "proposalId", "planId", "repository", "target", "revisionSha" })
        {
            var input = LinkedTask(); input["planSource"]!.AsObject().Remove(field);
            Expect("INVALID_REQUEST", () => Protocol.ValidateTask(input, allowFollowUp: true));
        }
        foreach (var invalid in new[] { "not-a-guid", Guid.NewGuid().ToString("N"), "../parent" })
            BadSource("parentRunId", JsonValue.Create(invalid));
        foreach (var invalid in new[] { new string('a', 63), new string('a', 65), new string('A', 64), new string('g', 64) })
            BadSource("parentResultFingerprint", JsonValue.Create(invalid));
        foreach (var field in new[] { "proposalId", "planId" })
        {
            foreach (var invalid in new[] { "", " ", new string('x', 129), "identifier\n", "identifier\0", "identifier\t" }) BadSource(field, JsonValue.Create(invalid));
            foreach (var invalid in new JsonNode?[] { null, JsonValue.Create(42), new JsonObject(), new JsonArray() }) BadSource(field, invalid);
            var atLimit = LinkedTask(); atLimit["planSource"]![field] = new string('x', 128);
            Protocol.ValidateTask(atLimit, allowFollowUp: true);
        }
        foreach (var invalid in new JsonNode?[] { JsonValue.Create(""), JsonValue.Create("short"), JsonValue.Create(new string('a', 64)),
            JsonValue.Create(new string('z', 40)), JsonValue.Create(42), new JsonObject() }) BadSource("revisionSha", invalid);
        BadSource("repository", JsonValue.Create("owner/another"), "REPOSITORY_NOT_SUPPORTED");
        foreach (var target in new JsonNode?[] { null, new JsonArray(), new JsonObject { ["type"] = "issue", ["number"] = 43 },
            new JsonObject { ["type"] = "pr", ["number"] = 42 }, new JsonObject { ["type"] = "issue", ["number"] = 0 },
            new JsonObject { ["type"] = "issue", ["number"] = "42" }, new JsonObject { ["type"] = "issue", ["number"] = 42, ["url"] = "https://example.invalid" } })
            BadSource("target", target);
        var foreignTask = LinkedTask(); foreignTask["repository"] = "owner/another";
        Expect("INVALID_REQUEST", () => Protocol.ValidateTask(foreignTask, allowFollowUp: true));
        var changedTarget = LinkedTask(); changedTarget["target"]!["number"] = 43;
        Expect("INVALID_REQUEST", () => Protocol.ValidateTask(changedTarget, allowFollowUp: true));
        foreach (var extra in new[] { "command", "cliPath", "permission", "plan" })
        {
            var input = LinkedTask(); input["planSource"]![extra] = "untrusted override";
            Expect("INVALID_REQUEST", () => Protocol.ValidateTask(input, allowFollowUp: true));
        }
    }

    private static void ContextBudgetsFollowAdmissionAuthority()
    {
        var ordinary = IssueTask("feature-research"); ordinary["context"] = SizedContext(32 * 1024);
        Protocol.ValidateTask(ordinary);
        ordinary["context"] = SizedContext(32 * 1024 + 1);
        Expect("INPUT_TOO_LARGE", () => Protocol.ValidateTask(ordinary));
        Expect("INPUT_TOO_LARGE", () => Protocol.ValidateTask(ordinary, allowFollowUp: true));
        var prepared = LinkedTask(); prepared["context"] = SizedContext(256 * 1024);
        Protocol.ValidateTask(prepared, allowFollowUp: true);
        prepared["context"] = SizedContext(256 * 1024 + 1);
        Expect("INPUT_TOO_LARGE", () => Protocol.ValidateTask(prepared, allowFollowUp: true));
        var bareInternalFlag = IssueTask("issue-fix"); bareInternalFlag["context"] = SizedContext(40 * 1024);
        Expect("INPUT_TOO_LARGE", () => Protocol.ValidateTask(bareInternalFlag, allowFollowUp: true));
        var linkedExistingKind = LinkedTask("issue-fix"); linkedExistingKind["context"] = SizedContext(40 * 1024);
        Protocol.ValidateTask(linkedExistingKind, allowFollowUp: true);
    }

    private static void CandidatePlanSourcesRemainHostOwned()
    {
        var original = LinkedTask("issue-verify"); original["planSource"]!["sourceKind"] = "original";
        Protocol.ValidateTask(original, allowFollowUp: true);
        foreach (var hash in new[] { new string('a', 64), new string('B', 64) })
        {
            var task = LinkedTask("issue-verify"); task["planSource"]!["sourceKind"] = "local-candidate";
            task["planSource"]!["candidateSnapshotHash"] = hash;
            var before = task.ToJsonString();
            var accepted = Protocol.ValidateTask(task, allowFollowUp: true);
            Check(accepted["planSource"]!["candidateSnapshotHash"]!.GetValue<string>() == hash && task.ToJsonString() == before,
                "A Host-prepared local candidate preserves the immutable snapshot identity without changing the request.");
            Expect("INVALID_REQUEST", () => Protocol.ValidateTask(task));
            accepted["planSource"]!["candidateSnapshotHash"] = new string('c', 64);
            Check(task.ToJsonString() == before, "The normalized candidate source must not share mutable nodes with the prepared request.");
        }
        foreach (var sourceKind in new JsonNode?[] { null, JsonValue.Create(""), JsonValue.Create("candidate"), JsonValue.Create(42) })
        {
            var task = LinkedTask("issue-verify"); task["planSource"]!["sourceKind"] = sourceKind?.DeepClone();
            Expect("INVALID_REQUEST", () => Protocol.ValidateTask(task, allowFollowUp: true));
        }
        foreach (var hash in new JsonNode?[] { null, JsonValue.Create(""), JsonValue.Create(new string('a', 63)), JsonValue.Create(new string('a', 65)), JsonValue.Create(new string('g', 64)), JsonValue.Create(42), new JsonObject() })
        {
            var task = LinkedTask("issue-verify"); task["planSource"]!["sourceKind"] = "local-candidate";
            task["planSource"]!["candidateSnapshotHash"] = hash?.DeepClone();
            Expect("INVALID_REQUEST", () => Protocol.ValidateTask(task, allowFollowUp: true));
        }
        var missingHash = LinkedTask("issue-verify"); missingHash["planSource"]!["sourceKind"] = "local-candidate";
        Expect("INVALID_REQUEST", () => Protocol.ValidateTask(missingHash, allowFollowUp: true));
        var noBase = LinkedTask("issue-verify"); noBase["planSource"]!["sourceKind"] = "local-candidate";
        noBase["planSource"]!["candidateSnapshotHash"] = new string('a', 64); noBase["planSource"]!["revisionSha"] = null;
        Expect("INVALID_REQUEST", () => Protocol.ValidateTask(noBase, allowFollowUp: true));
        foreach (var kind in new string?[] { null, "original" })
        {
            var task = LinkedTask("issue-verify");
            if (kind is not null) task["planSource"]!["sourceKind"] = kind;
            task["planSource"]!["candidateSnapshotHash"] = new string('a', 64);
            Expect("INVALID_REQUEST", () => Protocol.ValidateTask(task, allowFollowUp: true));
        }
        var withPath = LinkedTask("issue-verify"); withPath["planSource"]!["candidateSnapshotPath"] = "C:\\caller-selected.zip";
        Expect("INVALID_REQUEST", () => Protocol.ValidateTask(withPath, allowFollowUp: true));
    }

    private static void ValidationPreservesInputAndExecutionBoundaries()
    {
        var input = LinkedTask(); input["repository"] = "Microsoft/PowerToys";
        input["context"] = new JsonObject { ["title"] = "Quoted \"request\"", ["command"] = "This is source data, not an executable setting." };
        input["execution"] = new JsonObject { ["agent"] = "codex", ["model"] = "fixture-model", ["reasoningEffort"] = "low" };
        var original = input.ToJsonString(); var fingerprint = Protocol.Fingerprint(input);
        var accepted = Protocol.ValidateTask(input, allowFollowUp: true);
        Check(input.ToJsonString() == original && Protocol.Fingerprint(input) == fingerprint, "Admission must not mutate the caller's request or its request fingerprint.");
        accepted["context"]!["title"] = "changed clone"; accepted["planSource"]!["planId"] = "changed clone";
        Check(input.ToJsonString() == original, "Normalized tasks must own independent nested context and plan-source objects.");
        foreach (var field in new[] { "cliPath", "repoFolder", "permission", "command", "arguments", "agent" })
        {
            var topLevel = IssueTask("feature-research"); topLevel[field] = "external execution override";
            Expect("INVALID_REQUEST", () => Protocol.ValidateTask(topLevel));
        }
        foreach (var field in new[] { "cliPath", "repoFolder", "permission", "command", "arguments" })
        {
            var task = LinkedTask(); task["execution"] = new JsonObject { [field] = "external execution override" };
            Expect("INVALID_REQUEST", () => Protocol.ValidateTask(task, allowFollowUp: true));
        }
    }

    private static void FixedBundledSnapshotsIgnoreVirtualSelections(Fixture fixture)
    {
        var oldConfig = new Configuration(fixture.Store).Read();
        foreach (var workflow in NewWorkflows) oldConfig.Remove(workflow.Field);
        foreach (var workflow in NewWorkflows)
        {
            var task = workflow.Kind is "feature-implement" or "issue-verify" ? LinkedTask(workflow.Kind) : IssueTask(workflow.Kind);
            foreach (var proposed in new JsonNode?[] { null, JsonValue.Create(""), JsonValue.Create("../outside.prompt.md"), JsonValue.Create(TaskPrompt.DefaultIssuePrompt), JsonValue.Create(42) })
            {
                var config = oldConfig.DeepClone().AsObject(); config[workflow.Field] = proposed?.DeepClone();
                var snapshot = TaskPrompt.Snapshot(fixture.Catalog, task, config)!;
                AssertSnapshot(snapshot, workflow.File, workflow.Checks);
            }
            var before = task.ToJsonString();
            var captured = TaskPrompt.Snapshot(fixture.Catalog, task, oldConfig)!;
            AssertSnapshot(captured, workflow.File, workflow.Checks);
            Check(task.ToJsonString() == before, "Rendering fixed bundled workflows must preserve the original request.");
            var id = Guid.NewGuid().ToString("D");
            fixture.Store.CreateRun(id, task, oldConfig, Protocol.ProductionOrigin, captured);
            var saved = fixture.Store.ReadTask(id).ToJsonString();
            captured["body"] = "mutated after acceptance"; captured["schemaVersion"] = 2;
            Check(fixture.Store.ReadTask(id).ToJsonString() == saved, "Accepted v3 instruction snapshots must not follow caller mutations.");
        }
        Check(fixture.SessionCalls == 0, "Bundled task snapshots must not open a GitHub session or download a prompt.");
    }

    private static void RequiredChecksAndInstructionsMatchTheWorkflow()
    {
        foreach (var workflow in NewWorkflows)
        {
            var task = IssueTask(workflow.Kind);
            Check(ReviewModes.RequiredChecks(task).SequenceEqual(workflow.Checks), "New task kinds require their own exact workflow checks.");
            if (workflow.Kind is "feature-research" or "feature-implement")
                Check(!ReviewModes.RequiredChecks(task).Contains("reproduction"), "Feature workflows must not inherit a Bug reproduction gate.");
        }
        foreach (var (mode, checks) in new[]
        {
            ("static", new[] { "context", "local-review" }), ("build-tests", new[] { "context", "local-review", "build-tests" }),
            ("ui-e2e", new[] { "context", "local-review", "setup", "e2e" })
        })
        {
            var task = new JsonObject { ["actionKind"] = "pr-review", ["reviewOptions"] = new JsonObject { ["mode"] = mode } };
            Check(ReviewModes.RequiredChecks(task).SequenceEqual(checks), "PR review checks must follow the saved execution scope.");
            var instruction = ReviewModes.Instructions(task);
            Check(instruction.Contains(mode, StringComparison.Ordinal) && !instruction.Contains("Top N", StringComparison.Ordinal) && !instruction.Contains("P0", StringComparison.Ordinal),
                "ReviewModes injects the accepted execution scope without repeating the business review loop.");
        }
        var bug = ReviewModes.Instructions(IssueTask("bug-investigation"));
        Check(bug.Contains("investigate this Bug Issue", StringComparison.Ordinal) && bug.Contains("Product repair is a separate task", StringComparison.Ordinal) &&
            !bug.Contains("not_a_bug", StringComparison.Ordinal), "Bug scope defines the authorized task; its conclusion semantics remain in the business prompt.");
        var feature = ReviewModes.Instructions(IssueTask("feature-research"));
        Check(feature.Contains("research the supplied Feature Issue", StringComparison.Ordinal) && feature.Contains("Product implementation is a separate task", StringComparison.Ordinal) &&
            !feature.Contains("feasibility", StringComparison.Ordinal), "Feature scope does not duplicate the integrated research protocol or authorize implementation.");
        Check(ReviewModes.Instructions(IssueTask("feature-implement")).Contains("planSource", StringComparison.Ordinal) &&
            ReviewModes.Instructions(IssueTask("issue-verify")).Contains("do not repeat the full parent investigation", StringComparison.Ordinal),
            "Internal tasks must retain their saved plan and bounded scope.");
    }

    private static void BothAdaptersReceiveTheAcceptedSchema()
    {
        foreach (var agent in new[] { "codex", "copilot" })
        foreach (var version in new[] { 2, 3 })
        {
            var task = IssueTask("feature-research");
            var text = CliAdapter.Prompt(task, "read-only", agent, "Selected fixture instructions.", resultSchemaVersion: version);
            const string beginMarker = "--- BEGIN RESULT JSON SCHEMA ---";
            const string endMarker = "--- END RESULT JSON SCHEMA ---";
            var begin = text.IndexOf(beginMarker, StringComparison.Ordinal) + beginMarker.Length;
            var end = text.IndexOf(endMarker, begin, StringComparison.Ordinal);
            Check(begin >= beginMarker.Length && end > begin, "Each CLI receives the complete marked schema in the shared prompt.");
            var actual = JsonNode.Parse(text[begin..end].Trim())!.AsObject();
            var expected = version == 3 ? WorkflowResult.SchemaV3() : WorkflowResult.Schema();
            Check(JsonNode.DeepEquals(actual, expected) && JsonNode.DeepEquals(CliAdapter.ResultSchema(version), expected),
                "Both CLI wrappers must deliver the accepted full v2 or v3 schema without relying on vendor-specific schema flags.");
            Check(text.Contains("Selected fixture instructions.", StringComparison.Ordinal) && text.Contains("--- BEGIN TASK CONTEXT JSON ---", StringComparison.Ordinal),
                "Schema selection must preserve selected local instructions and separately marked task context.");
        }
        Check(CliAdapter.ResultSchema()["properties"]!["schemaVersion"]!["const"]!.GetValue<int>() == 3 &&
            WorkflowResult.Schema()["properties"]!["schemaVersion"]!["const"]!.GetValue<int>() == 2,
            "New default adapters use v3 while the historical Schema API retains v2.");
    }

    private static void AdapterLimitsFollowTheAcceptedVersion()
    {
        foreach (var agent in new[] { "codex", "copilot" })
        {
            var current = new CliAdapter(agent, 3);
            Check(current.MaximumEventCharacters == ResultLimits.MaximumV3CliEventCharacters && current.MaximumFinalCharacters == ResultLimits.MaximumV3FinalTextCharacters &&
                current.MaximumLogBytes == ResultLimits.MaximumV3LogBytes, "V3 adapters must use the complete-report transport and log budgets.");
            foreach (var legacyVersion in new int?[] { null, 1, 2 })
            {
                var historical = new CliAdapter(agent, legacyVersion);
                Check(historical.MaximumEventCharacters == ResultLimits.MaximumCliEventCharacters && historical.MaximumFinalCharacters == ResultLimits.MaximumFinalTextCharacters &&
                    historical.MaximumLogBytes == ResultLimits.MaximumLegacyLogBytes, "Legacy adapters retain their historical transport and log budgets.");
            }
        }
    }

    private static JsonObject IssueTask(string kind) => new()
    {
        ["requestId"] = Guid.NewGuid().ToString("D"), ["actionId"] = "task-v3-fixture", ["actionKind"] = kind,
        ["repository"] = Configuration.PowerToysRepository, ["target"] = new JsonObject { ["type"] = "issue", ["number"] = 42 },
        ["context"] = new JsonObject { ["title"] = "A bounded local workflow" }, ["prompt"] = "Inspect this offline task admission fixture."
    };
    private static JsonObject LinkedTask(string kind = "feature-implement")
    {
        var task = IssueTask(kind); task["planSource"] = PlanSource(task); return task;
    }
    private static JsonObject PlanSource(JsonObject task) => new()
    {
        ["parentRunId"] = Guid.NewGuid().ToString("D"), ["parentResultFingerprint"] = new string('a', 64), ["proposalId"] = "saved-proposal-1", ["planId"] = "saved-plan-1",
        ["repository"] = task["repository"]!.DeepClone(), ["target"] = task["target"]!.DeepClone(), ["revisionSha"] = new string('b', 40)
    };
    private static JsonObject SizedContext(int bytes)
    {
        var value = new JsonObject { ["text"] = "" }; value["text"] = new string('x', bytes - Encoding.UTF8.GetByteCount(value.ToJsonString()));
        Check(Encoding.UTF8.GetByteCount(value.ToJsonString()) == bytes, "Context fixture must hit the exact encoded-byte boundary."); return value;
    }
    private static void BadSource(string field, JsonNode? value, string code = "INVALID_REQUEST")
    {
        var task = LinkedTask(); task["planSource"]![field] = value?.DeepClone(); Expect(code, () => Protocol.ValidateTask(task, allowFollowUp: true));
    }
    private static void AssertSnapshot(JsonObject snapshot, string file, string[] checks) => Check(snapshot["name"]?.GetValue<string>() == file &&
        snapshot["schemaVersion"]?.GetValue<int>() == 3 && snapshot["source"]?.GetValue<string>() == "bundled" && snapshot["appliesTo"]?.GetValue<string>() == "issue" &&
        snapshot["requiredChecks"]!.AsArray().Select(value => value!.GetValue<string>()).SequenceEqual(checks), "Each new workflow must capture its fixed bundled v3 prompt and exact required checks.");
    private static void Expect(string code, Action action)
    {
        try { action(); } catch (ProtocolException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static void Check(bool condition, string message) => CoreScenarios.Check(condition, message);

    private sealed class Fixture : IDisposable
    {
        private readonly string temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        private readonly string root;
        public Store Store { get; }
        public PromptCatalog Catalog { get; }
        public int SessionCalls { get; private set; }
        public Fixture()
        {
            root = Path.Combine(temporary, "PulseTaskWorkflowV3-" + Guid.NewGuid().ToString("N")); Store = new Store(root);
            Catalog = new PromptCatalog(Store, _ => { SessionCalls++; throw new InvalidOperationException("Task workflow fixtures cannot open a GitHub session."); });
        }
        public void Dispose()
        {
            var absolute = Path.GetFullPath(root);
            Check(string.Equals(Path.GetDirectoryName(absolute), temporary, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(absolute).StartsWith("PulseTaskWorkflowV3-", StringComparison.Ordinal),
                "Workflow fixture cleanup must remain inside its dedicated temporary directory.");
            if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
            Check(!Directory.Exists(absolute), "Workflow fixture cleanup must remove every local record.");
        }
    }
}
