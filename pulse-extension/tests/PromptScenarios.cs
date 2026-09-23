using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pulse.Host.Tests;

/// <summary>Bundled prompt assets, offline compatibility, selection, and immutable task snapshots.</summary>
internal static class PromptScenarios
{
    internal static async Task RunAllAsync()
    {
        CatalogIsAvailableOffline();
        AgentNeutralContract();
        PrReviewSeparatesReviewerScopeFromProductAcceptance();
        RuntimeSkillRespectsSelectedWork();
        ResultProtocolMatchesTheTask();
        ResultProtocolExplainsEvidenceIdentityAndCrossFieldRules();
        await ReloadIgnoresOldCachesAndAccounts();
        await RendersTargetsAndPreservesSnapshots();
        await EveryActionUsesItsBundledWorkflow();
        await ConfigurationSelectionsRemainCompatible();
        Console.WriteLine("PASS prompts: embedded offline catalog, content hashes, ignored legacy caches, v3 local workflows, quoted targets and immutable accepted snapshots");
    }

    private static void AgentNeutralContract()
    {
        using var fixture = new Fixture();
        var expectedSchema = WorkflowResult.SchemaV3();
        foreach (var entry in fixture.Catalog.List()["prompts"]!.AsArray().OfType<JsonObject>())
        {
            var body = fixture.Catalog.Get(entry["name"]!.GetValue<string>())["content"]!.GetValue<string>();
            Check(!body.Contains("copilot", StringComparison.OrdinalIgnoreCase) && !body.Contains("codex", StringComparison.OrdinalIgnoreCase),
                "Business prompts do not require a particular agent vendor.");
            var task = new JsonObject { ["actionKind"] = entry["actionKind"]!.DeepClone(), ["repository"] = "microsoft/PowerToys", ["prompt"] = "Neutral fixture context" };
            foreach (var agent in new[] { "codex", "copilot" })
            foreach (var permission in new[] { "read-only", "workspace-write", "yolo" })
            {
                var rendered = CliAdapter.Prompt(task, permission, agent, body);
                var schema = Block(rendered, "RESULT JSON SCHEMA");
                Check(JsonNode.DeepEquals(schema, expectedSchema) && rendered.Split("--- BEGIN RESULT JSON SCHEMA ---", StringSplitOptions.None).Length == 2,
                    "Every supported agent receives the same full result schema exactly once in the prompt.");
                Check(schema["properties"]!["assessment"] is JsonObject && schema["properties"]!["diagnostics"] is JsonObject && schema["properties"]!["report"] is JsonObject &&
                    schema["properties"]!["featureAssessment"] is JsonObject && schema["properties"]!["bugAssessment"] is JsonObject && schema["properties"]!["nextActions"]!["items"]!["anyOf"]!.AsArray()
                    .OfType<JsonObject>().Any(variant => variant["properties"]?["suggestionIds"] is JsonObject),
                    "Every supported agent receives product assessments and explicit proposal-to-suggestion associations.");
                Check(rendered.Contains(body, StringComparison.Ordinal) && rendered.Contains(string.Join(", ", ReviewModes.RequiredChecks(task)), StringComparison.Ordinal),
                    "Agent selection preserves the shared business workflow and mandatory checks.");
                Check(!rendered.Contains("copilot", StringComparison.OrdinalIgnoreCase) && !rendered.Contains("codex", StringComparison.OrdinalIgnoreCase),
                    "The common instruction wrapper describes capabilities without vendor-specific workflow text.");
                var capabilities = Block(rendered, "EXECUTION CAPABILITIES");
                var arguments = CliAdapter.Arguments(agent, permission, "schema.json");
                Check(agent != "codex" || arguments.Contains("--output-schema"), "Text assembly changes retain the existing structured-output CLI argument.");
                Check(capabilities["fileToolsOnly"]!.GetValue<bool>() == arguments.Contains("--deny-tool=shell") &&
                    capabilities["builtInToolServers"]!.GetValue<string>() == (arguments.Contains("--disable-builtin-mcps") ? "disabled" : "cli-configured") &&
                    capabilities["permissionPolicy"]!.GetValue<string>() == permission && !capabilities["interactiveApprovals"]!.GetValue<bool>(),
                    "Generic capability descriptions match actual adapter grants and preserve saved permissions.");
            }
        }
        try { CliAdapter.Prompt(new JsonObject(), "yolo", "unsupported-agent"); throw new Exception("An unsupported adapter was silently accepted."); }
        catch (InvalidOperationException) { }

        static JsonNode Block(string prompt, string label)
        {
            var startMarker = "--- BEGIN " + label + " ---";
            var endMarker = "--- END " + label + " ---";
            var start = prompt.IndexOf(startMarker, StringComparison.Ordinal);
            var end = prompt.IndexOf(endMarker, StringComparison.Ordinal);
            Check(start >= 0 && end > start, "The shared prompt contains a complete structured contract block.");
            return JsonNode.Parse(prompt[(start + startMarker.Length)..end])!;
        }
    }

    private static void CatalogIsAvailableOffline()
    {
        using var fixture = new Fixture();
        var catalog = fixture.Catalog.List();
        Check(catalog["prompts"] is JsonArray { Count: 8 } && catalog["source"]?.GetValue<string>() == "bundled" &&
            catalog["sourceUrl"]?.GetValue<string>() == PromptCatalog.SourceUrl && catalog["schemaVersion"]?.GetValue<int>() == 3,
            "A fresh installation must expose all eight repository-owned prompts without GitHub login or synchronization.");
        Check(catalog["syncedAt"] is null && !Directory.Exists(Path.Combine(fixture.Root, "prompts")) && fixture.SessionCalls == 0,
            "Reading bundled prompts must not create download caches or claim a network synchronization happened.");
        var manifest = new StringBuilder();
        foreach (var entry in catalog["prompts"]!.AsArray().OfType<JsonObject>().OrderBy(row => row["name"]!.GetValue<string>(), StringComparer.Ordinal))
        {
            var name = entry["name"]!.GetValue<string>();
            var prompt = fixture.Catalog.Get(name);
            using var resource = typeof(PromptCatalog).Assembly.GetManifestResourceStream("Pulse.Host.Prompts." + name);
            Check(resource is not null, "The actual Host assembly must contain each listed prompt asset.");
            using var bytes = new MemoryStream();
            resource!.CopyTo(bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
            Check(prompt["content"]?.GetValue<string>() == Encoding.UTF8.GetString(bytes.ToArray()) && prompt["sha"]?.GetValue<string>() == hash &&
                prompt["hashAlgorithm"]?.GetValue<string>() == "sha256" && prompt["revision"]?.GetValue<string>() == catalog["revision"]?.GetValue<string>(),
                "Catalog hashes and selected prompt bodies must identify the exact embedded UTF-8 bytes.");
            Check(prompt["path"]?.GetValue<string>() == "prompts/" + name && prompt["source"]?.GetValue<string>() == "bundled" &&
                prompt["sourceUrl"]?.GetValue<string>() == PromptCatalog.SourceUrl, "Prompt provenance must describe its bundled repository asset.");
            manifest.Append(name).Append('\0').Append(hash).Append('\n');
            prompt["content"] = "caller mutation";
            Check(fixture.Catalog.Get(name)["content"]?.GetValue<string>() != "caller mutation", "A caller cannot mutate the next task's bundled instructions.");
        }
        var fragments = new[] { fixture.Catalog.VerificationInstructions(), fixture.Catalog.RuntimeSkillInstructions() };
        Check(catalog["fragments"] is JsonArray { Count: 2 }, "The catalog identifies both shared fragments without exposing another selectable action.");
        foreach (var fragment in fragments)
        {
            var name = fragment["name"]!.GetValue<string>();
            using var resource = typeof(PromptCatalog).Assembly.GetManifestResourceStream("Pulse.Host.Prompts." + name);
            Check(resource is not null, "The shared verification fragment must be embedded alongside the standalone and plan-based workflows.");
            using var bytes = new MemoryStream(); resource!.CopyTo(bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
            Check(fragment["content"]?.GetValue<string>() == Encoding.UTF8.GetString(bytes.ToArray()) && fragment["sha"]?.GetValue<string>() == hash &&
                catalog["fragments"]!.AsArray().OfType<JsonObject>().Single(row => row["name"]!.GetValue<string>() == name)["sha"]?.GetValue<string>() == hash,
                "The shared fragment identifies its exact source bytes without becoming another selectable workflow.");
            manifest.Append(name).Append('\0').Append(hash).Append('\n');
            Expect("PROMPT_NOT_FOUND", () => fixture.Catalog.Get(name));
        }
        fragments[1]["content"] = "caller mutation";
        Check(fixture.Catalog.RuntimeSkillInstructions()["content"]!.GetValue<string>() != "caller mutation", "A caller cannot alter the runtime skill integration for future tasks.");
        Check(catalog["revision"]?.GetValue<string>() == Protocol.Hash(manifest.ToString()), "The bundle revision must deterministically identify all workflows and their shared verification instructions.");
        catalog["prompts"]!.AsArray().Clear();
        Check(fixture.Catalog.List()["prompts"]!.AsArray().Count == 8, "Catalog responses must be detached from the immutable embedded asset index.");
        foreach (var name in new[] { "../escape.md", "C:\\prompt.md", "https://example.test/prompt.md", "CON.md", "bad\0.md", "sub/child.md" })
            Expect("INVALID_PROMPT_NAME", () => fixture.Catalog.Get(name));
        Expect("PROMPT_NOT_FOUND", () => fixture.Catalog.Get("unbundled-custom.prompt.md"));
    }

    private static void PrReviewSeparatesReviewerScopeFromProductAcceptance()
    {
        using var fixture = new Fixture();
        var config = new Configuration(fixture.Store).Read();
        var shared = fixture.Catalog.VerificationInstructions()["content"]!.GetValue<string>();
        foreach (var mode in ReviewModes.Values)
        {
            var task = BusinessTask("pr-review", "pr", 42, "Scoped review"); task["reviewOptions"] = new JsonObject { ["mode"] = mode };
            var snapshot = TaskPrompt.Snapshot(fixture.Catalog, task, config)!;
            Check(snapshot["fragments"]!.AsArray().Count == (mode == "static" ? 0 : mode == "ui-e2e" ? 2 : 1) &&
                snapshot["body"]!.GetValue<string>().Contains(shared, StringComparison.Ordinal) == (mode != "static"),
                "Verification scopes share evidence guidance while actual UI scope additionally receives runtime skill integration.");
            foreach (var agent in new[] { "codex", "copilot" })
            {
                var rendered = CliAdapter.Prompt(task, "yolo", agent, snapshot["body"]!.GetValue<string>());
                Check(rendered.Contains(ReviewModes.Instructions(task), StringComparison.Ordinal), "Assembly preserves the exact accepted execution scope.");
                var protocol = TextBlock(rendered, "WORKFLOW RESULT PROTOCOL");
                Check(protocol.Contains("e2eAssessment", StringComparison.Ordinal) && protocol.Contains("24576", StringComparison.Ordinal) && protocol.Contains("P0", StringComparison.Ordinal),
                    "Every PR scope retains its result identity, bounded E2E assessment and manual-operation boundary.");
                Check(protocol.Contains("validation IDs e2e-scenario-1", StringComparison.Ordinal) == (mode == "ui-e2e") &&
                    !protocol.Contains("context.reviewVerification.scenarioIdPrefix", StringComparison.Ordinal),
                    "Direct runtime reporting instructions are disclosed only for direct UI/E2E review.");
            }
        }
        foreach (var agent in new[] { "codex", "copilot" })
        {
            var pr = CliAdapter.Prompt(new JsonObject { ["actionKind"] = "pr-review", ["prompt"] = "A PR description recommends a large runtime matrix." }, "yolo", agent);
            var e2e = CliAdapter.Prompt(new JsonObject { ["actionKind"] = "e2e", ["prompt"] = "Execute the required runtime acceptance scenarios." }, "yolo", agent,
                fixture.Catalog.Get(TaskPrompt.DefaultE2ePrompt)["content"]!.GetValue<string>());
            Check(pr.Contains("historical request did not record a review scope", StringComparison.Ordinal) &&
                e2e.Contains("Accepted scope: ui-e2e", StringComparison.Ordinal),
                "Old unscoped review remains explicitly unrecorded while legacy E2E retains runtime execution.");
        }
    }

    private static void RuntimeSkillRespectsSelectedWork()
    {
        using var fixture = new Fixture();
        var config = new Configuration(fixture.Store).Read();
        var runtime = fixture.Catalog.RuntimeSkillInstructions()["content"]!.GetValue<string>();
        foreach (var (kind, mode, expected) in new (string, string?, bool)[]
        {
            ("pr-review", "static", false), ("pr-review", "build-tests", false), ("pr-review", "ui-e2e", true), ("pr-review", null, true),
            ("pr-verify", "build-tests", false), ("pr-verify", "ui-e2e", true), ("e2e", null, true),
            ("feature-research", null, false), ("bug-investigation", null, true), ("issue-fix", null, true),
            ("reproduction-setup", null, true), ("feature-implement", null, true), ("issue-verify", null, true)
        })
        {
            var task = ScopeTask(kind, mode);
            var original = task.ToJsonString();
            var snapshot = TaskPrompt.Snapshot(fixture.Catalog, task, config)!;
            var body = snapshot["body"]!.GetValue<string>();
            Check(body.Contains(runtime, StringComparison.Ordinal) == expected &&
                body.Contains("pulse-powertoys-verification", StringComparison.Ordinal) == expected &&
                snapshot["fragments"]!.AsArray().OfType<JsonObject>().Any(row => row["name"]!.GetValue<string>() == PromptCatalog.RuntimeSkillFragmentName) == expected,
                "Runtime integration must be absent from static/build-only PR work and Feature research, and conditional for accepted Issue experiments and implementation.");
            Check(task.ToJsonString() == original && snapshot["requiredChecks"]!.AsArray().Select(row => row!.GetValue<string>()).SequenceEqual(ReviewModes.RequiredChecks(task)),
                "Optional skill guidance cannot change accepted task context, plans or completion checks.");
            foreach (var agent in new[] { "codex", "copilot" })
            {
                var rendered = CliAdapter.Prompt(task, "yolo", agent, body);
                Check(rendered.Contains(runtime, StringComparison.Ordinal) == expected &&
                    rendered.Split("--- BEGIN RESULT JSON SCHEMA ---", StringSplitOptions.None).Length == 2 &&
                    rendered.Contains("Return schemaVersion: 3.", StringComparison.Ordinal),
                    "Both adapters preserve runtime guidance selection and the sole authoritative Host result schema.");
            }
        }

        static JsonObject ScopeTask(string kind, string? mode)
        {
            var publicKind = kind switch { "pr-verify" => "pr-review", "feature-implement" or "issue-verify" => "bug-investigation", _ => kind };
            var task = BusinessTask(publicKind, publicKind is "pr-review" or "e2e" ? "pr" : "issue", 42, "Runtime skill scope");
            task["actionKind"] = kind;
            if (mode is not null) task["reviewOptions"] = new JsonObject { ["mode"] = mode };
            if (kind == "pr-verify")
                task["followUp"] = new JsonObject
                {
                    ["parentRunId"] = Guid.NewGuid().ToString("D"), ["recommendationId"] = new string('c', 64),
                    ["parentResultFingerprint"] = new string('b', 64), ["subject"] = "original-pr", ["revisionSha"] = task["expectedHeadSha"]!.DeepClone()
                };
            if (kind is "feature-implement" or "issue-verify")
                task["planSource"] = new JsonObject
                {
                    ["parentRunId"] = Guid.NewGuid().ToString("D"), ["parentResultFingerprint"] = new string('b', 64),
                    ["proposalId"] = "scope-proposal-1", ["planId"] = "scope-plan-1", ["repository"] = task["repository"]!.DeepClone(),
                    ["target"] = task["target"]!.DeepClone(), ["revisionSha"] = new string('a', 40), ["sourceKind"] = "original"
                };
            return Protocol.ValidateTask(task, allowFollowUp: kind is "pr-verify" or "feature-implement" or "issue-verify");
        }
    }

    private static void ResultProtocolMatchesTheTask()
    {
        foreach (var (kind, expected, excluded) in new[]
        {
            ("feature-research", "Feature research result protocol", "Bug investigation result protocol"),
            ("bug-investigation", "Bug investigation result protocol", "Feature research result protocol"),
            ("feature-implement", "Implementation result protocol", "Feature research result protocol"),
            ("issue-fix", "Implementation result protocol", "Bug investigation result protocol"),
            ("issue-verify", "Issue verification result protocol", "Feature research result protocol"),
            ("reproduction-setup", "Issue verification result protocol", "Bug investigation result protocol")
        })
        foreach (var agent in new[] { "codex", "copilot" })
        {
            var task = new JsonObject { ["actionKind"] = kind, ["prompt"] = "Fixture intent." };
            var assembled = CliAdapter.Prompt(task, "workspace-write", agent, "Business workflow fixture.");
            var protocol = TextBlock(assembled, "WORKFLOW RESULT PROTOCOL");
            Check(protocol.Contains(expected, StringComparison.Ordinal) && !protocol.Contains(excluded, StringComparison.Ordinal) &&
                !protocol.Contains("PR result protocol", StringComparison.Ordinal) && !protocol.Contains("e2e-scenario", StringComparison.Ordinal),
                "Each Issue task receives only its own result protocol, not all PR, Feature and Bug procedures.");
            Check(protocol.Contains("24576", StringComparison.Ordinal), "Applicable Issue workflows retain the whole-plan byte budget.");
            Check(assembled.Contains("Complete the accepted local Pulse task autonomously", StringComparison.Ordinal) &&
                assembled.Contains("finish independent work", StringComparison.Ordinal) && assembled.Contains("diagnostics.message", StringComparison.Ordinal) &&
                assembled.Contains("validation.details/evidence", StringComparison.Ordinal) && assembled.Contains("User build and package-source instructions retain their priority", StringComparison.Ordinal),
                "The common boundary grants scoped autonomy and requires concrete applicable-rule evidence for actual blockers.");
        }
        foreach (var kind in new[] { "e2e", "pr-verify" })
        {
            var task = new JsonObject { ["actionKind"] = kind, ["reviewOptions"] = new JsonObject { ["mode"] = "ui-e2e" } };
            var protocol = TextBlock(CliAdapter.Prompt(task, "read-only", "codex"), "WORKFLOW RESULT PROTOCOL");
            Check(protocol.Contains("PR verification result protocol", StringComparison.Ordinal) &&
                protocol.Contains("context.reviewVerification.scenarioIdPrefix", StringComparison.Ordinal) == (kind == "pr-verify") &&
                !protocol.Contains("Feature research result protocol", StringComparison.Ordinal), "Linked verification alone receives its saved scenario-ID prefix protocol.");
        }
    }

    private static string TextBlock(string prompt, string label)
    {
        var begin = "--- BEGIN " + label + " ---"; var end = "--- END " + label + " ---";
        var first = prompt.IndexOf(begin, StringComparison.Ordinal); var last = prompt.IndexOf(end, StringComparison.Ordinal);
        Check(first >= 0 && last > first, "The assembled prompt contains the requested protocol block.");
        return prompt[(first + begin.Length)..last];
    }

    private static void ResultProtocolExplainsEvidenceIdentityAndCrossFieldRules()
    {
        foreach (var version in new[] { 2, 3 })
        foreach (var agent in new[] { "codex", "copilot" })
        {
            var task = new JsonObject { ["actionKind"] = "pr-review", ["reviewOptions"] = new JsonObject { ["mode"] = "build-tests" } };
            var protocol = TextBlock(CliAdapter.Prompt(task, "read-only", agent, resultSchemaVersion: version), "WORKFLOW RESULT PROTOCOL");
            Check(protocol.Contains("source current-run, ci or author", StringComparison.Ordinal) && protocol.Contains("return runId:null", StringComparison.Ordinal) &&
                protocol.Contains("earlier Pulse run's D-format GUID, or null if unknown", StringComparison.Ordinal) &&
                protocol.Contains("TRX run IDs, test invocation IDs and CI job/build IDs in evidence text, never in runId", StringComparison.Ordinal),
                "Every supported agent and result version distinguish a prior Pulse run identity from current execution, test and CI identifiers without inventing missing provenance.");
            Check(protocol.Contains("UTF-16 code units", StringComparison.Ordinal) && protocol.Contains("UTF-8", StringComparison.Ordinal) && protocol.Contains("escaping", StringComparison.Ordinal) &&
                protocol.Contains(version == 3 ? "8388608" : "327680", StringComparison.Ordinal) && protocol.Contains("24576", StringComparison.Ordinal),
                "The emitted protocol distinguishes per-string UTF-16 ceilings, serialized UTF-8 object budgets and whole-result limits.");
            Check(protocol.Contains("IDs are unique within", StringComparison.Ordinal) && protocol.Contains("has no duplicates", StringComparison.Ordinal) &&
                protocol.Contains("references only IDs in review.suggestions", StringComparison.Ordinal) && protocol.Contains("1 <= startLine <= line <= 2147483647", StringComparison.Ordinal) && protocol.Contains("at most 1000 lines", StringComparison.Ordinal),
                "The emitted protocol explains ID identity, suggestion reference integrity and line-range constraints beyond individual field shapes.");
        }
        var issueProtocol = TextBlock(CliAdapter.Prompt(new JsonObject { ["actionKind"] = "bug-investigation" }, "read-only", "codex"), "WORKFLOW RESULT PROTOCOL");
        foreach (var rule in new[] { "existing plans entry by planId and matching taskKind", "feature-implement plan", "issue-fix, reproduction-setup or", "mutually exclusive", "at least one confirmed finding", "identical relatedIssue object as duplicateOf", "https://github.com/{repository}/issues/{number}" })
            Check(issueProtocol.Contains(rule, StringComparison.Ordinal), "Issue result guidance retains the Host's cross-field plan, finding and duplicate-reference contracts.");
        Check(issueProtocol.Contains("complete:true, rechecked:true and nonempty coverage", StringComparison.Ordinal) &&
            issueProtocol.Contains("unresolved candidate findings make the report incomplete", StringComparison.Ordinal),
            "A final-report claim retains the recorded coverage and recheck requirements rather than silently dropping unresolved evidence.");
    }

    private static async Task ReloadIgnoresOldCachesAndAccounts()
    {
        using var fixture = new Fixture();
        var oldDirectory = Path.Combine(fixture.Root, "prompts", "generations", new string('a', 32));
        Directory.CreateDirectory(oldDirectory);
        File.WriteAllText(Path.Combine(oldDirectory, TaskPrompt.DefaultPrPrompt), "STALE_REMOTE_SKILL_AND_CLOUD_REVIEW_GATE");
        File.WriteAllText(Path.Combine(oldDirectory, "catalog.json"), "corrupt legacy metadata");
        File.WriteAllText(Path.Combine(fixture.Root, "prompts", "current.json"), "invalid legacy pointer");
        fixture.Store.WriteJson(Path.Combine(fixture.Root, "config.json"), new JsonObject { ["githubAccount"] = "preserve-this-selection", ["permission"] = "read-only" });
        var before = Files(fixture.Root);
        var expected = fixture.Catalog.List().ToJsonString();
        var reloaded = await fixture.Catalog.SyncAsync(new JsonObject { ["githubAccount"] = "different-unused-account" });
        Check(reloaded.ToJsonString() == expected && fixture.SessionCalls == 0, "Legacy prompts.sync must return the bundled catalog without opening an account or calling GitHub.");
        Check(!fixture.Catalog.Get(TaskPrompt.DefaultPrPrompt)["content"]!.GetValue<string>().Contains("STALE_REMOTE_SKILL", StringComparison.Ordinal),
            "Old synchronized caches, including damaged caches, must never override a new task's bundled prompt.");
        Check(Files(fixture.Root).OrderBy(pair => pair.Key).SequenceEqual(before.OrderBy(pair => pair.Key)),
            "Listing, selecting and compatibility reload must leave old caches and user configuration byte-for-byte unchanged.");
    }

    private static async Task RendersTargetsAndPreservesSnapshots()
    {
        using var fixture = new Fixture();
        var config = new Configuration(fixture.Store).Read();
        var task = BusinessTask("pr-review", "pr", 42, "Fix \"quotes\"\r\nInjected title <PRNumber> $(literal)");
        var fingerprint = Protocol.Fingerprint(task);
        var snapshot = TaskPrompt.Snapshot(fixture.Catalog, task, config)!;
        var body = snapshot["body"]!.GetValue<string>();
        var quoted = JsonSerializer.Serialize("Fix \"quotes\"Injected title <PRNumber> $(literal)");
        Check(body.Contains(quoted, StringComparison.Ordinal) && body.Contains("/pull/42", StringComparison.Ordinal) &&
            snapshot["name"]?.GetValue<string>() == TaskPrompt.DefaultPrPrompt && snapshot["appliesTo"]?.GetValue<string>() == "pr",
            "Rendering must pin the saved PR number and quote untrusted titles without injecting control lines or replacing placeholder-looking title data twice.");
        Check(Protocol.Fingerprint(task) == fingerprint && !body.Contains("WEB_CONTEXT_ONLY", StringComparison.Ordinal),
            "The website prompt remains separate request context and cannot replace bundled action instructions or change request identity.");
        var id = Guid.NewGuid().ToString("D");
        fixture.Store.CreateRun(id, task, config, Protocol.ProductionOrigin, snapshot);
        var accepted = fixture.Store.ReadTask(id).ToJsonString();
        snapshot["body"] = "caller mutation after acceptance";
        snapshot["fragments"]!.AsArray().Clear();
        await fixture.Catalog.SyncAsync(new JsonObject());
        Check(fixture.Store.ReadTask(id).ToJsonString() == accepted && fixture.Store.FindRequest(task, Protocol.ProductionOrigin) == id,
            "Accepted task, prompt body, version and request mapping must remain immutable after reload and caller mutation.");

        var oldTask = BusinessTask("pr-review", "pr", 43, "Historical task");
        var oldTemplate = new JsonObject { ["name"] = TaskPrompt.DefaultPrPrompt, ["body"] = "Historical remote skill instructions", ["sha"] = new string('b', 40), ["revision"] = new string('c', 40), ["source"] = "catalog", ["schemaVersion"] = 2 };
        var oldId = Guid.NewGuid().ToString("D");
        fixture.Store.CreateRun(oldId, oldTask, config, Protocol.ProductionOrigin, oldTemplate);
        var oldBytes = File.ReadAllBytes(Path.Combine(fixture.Store.RunDirectory(oldId), "task.json"));
        await fixture.Catalog.SyncAsync(new JsonObject());
        Check(File.ReadAllBytes(Path.Combine(fixture.Store.RunDirectory(oldId), "task.json")).SequenceEqual(oldBytes),
            "Shipping local workflows must not rewrite a historical task's saved instructions or provenance.");
        Check(TaskPrompt.Snapshot(fixture.Catalog, oldTask, config)!["source"]?.GetValue<string>() == "bundled", "A deliberate new task receives current bundled instructions even when an older task used the same filename.");
    }

    private static async Task EveryActionUsesItsBundledWorkflow()
    {
        using var fixture = new Fixture();
        var config = new Configuration(fixture.Store).Read();
        foreach (var (kind, target, filename, checks) in new[]
        {
            ("pr-review", "pr", TaskPrompt.DefaultPrPrompt, new[] { "context", "local-review", "verification" }),
            ("issue-fix", "issue", TaskPrompt.DefaultIssuePrompt, new[] { "reproduction", "implementation", "verification" }),
            ("e2e", "pr", TaskPrompt.DefaultE2ePrompt, new[] { "setup", "e2e" }),
            ("reproduction-setup", "issue", TaskPrompt.DefaultReproductionPrompt, new[] { "reproduction", "instructions" }),
            ("feature-research", "issue", TaskPrompt.DefaultFeatureResearchPrompt, new[] { "requirements", "existing-capabilities", "feasibility", "next-step" }),
            ("bug-investigation", "issue", TaskPrompt.DefaultBugInvestigationPrompt, new[] { "context", "investigation", "local-review" })
        })
        {
            var task = BusinessTask(kind, target, 99, "Local workflow");
            var snapshot = TaskPrompt.Snapshot(fixture.Catalog, task, config)!;
            var body = snapshot["body"]!.GetValue<string>();
            var assembled = CliAdapter.Prompt(task, "read-only", "codex", body);
            Check(snapshot["name"]?.GetValue<string>() == filename && snapshot["source"]?.GetValue<string>() == "bundled" && snapshot["schemaVersion"]?.GetValue<int>() == 3,
                "Each new action must snapshot its repository-owned v3 prompt, including the legacy blank reproduction selection.");
            Check(snapshot["requiredChecks"]!.AsArray().Select(node => node!.GetValue<string>()).SequenceEqual(checks) &&
                assembled.Contains("The required workflow validation IDs for this action are: " + string.Join(", ", checks) + ".", StringComparison.Ordinal),
                "Each bundled workflow must require the exact action-specific check IDs selected by the Host.");
            Check(!body.Contains(".github/skills/", StringComparison.Ordinal) &&
                assembled.Contains("Return schemaVersion: 3.", StringComparison.Ordinal),
                "Business prompts are self-contained while the Host injects the current result protocol.");
        }
        config["reproductionPrompt"] = TaskPrompt.BundledReproductionPrompt;
        Check(TaskPrompt.Snapshot(fixture.Catalog, BusinessTask("reproduction-setup", "issue", 99, "Legacy selection"), config)!["name"]?.GetValue<string>() == TaskPrompt.DefaultReproductionPrompt,
            "The old inline reproduction alias must map to the new bundled filename for new tasks.");
        config["cliSelections"] = new JsonObject { ["codex"] = "C:\\fixture-cli\\codex.exe", ["copilot"] = "" };
        config["githubAccount"] = "selected";
        fixture.Store.WriteJson(Path.Combine(fixture.Root, "config.json"), config);
        var ready = new ActionReadiness(fixture.Store, _ => Task.FromResult(new JsonObject { ["available"] = true }),
            () => Task.FromResult(new JsonObject { ["accounts"] = new JsonArray(new JsonObject { ["login"] = "selected", ["state"] = "success" }) }), _ => Task.CompletedTask);
        foreach (var kind in new[] { "pr-review", "issue-fix", "e2e", "reproduction-setup", "feature-research", "bug-investigation" })
            Check((await ready.CheckAsync(new JsonObject { ["actionKind"] = kind }))["ready"]?.GetValue<bool>() == true, "Configured action readiness must not require prompt synchronization.");
    }

    private static async Task ConfigurationSelectionsRemainCompatible()
    {
        using var fixture = new Fixture();
        var configuration = new Configuration(fixture.Store);
        var config = await configuration.SaveAsync(configuration.Read());
        Check(config["prPrompt"]?.GetValue<string>() == TaskPrompt.DefaultPrPrompt && config["issuePrompt"]?.GetValue<string>() == TaskPrompt.DefaultIssuePrompt &&
            config["e2ePrompt"]?.GetValue<string>() == TaskPrompt.DefaultE2ePrompt, "Fresh settings must save familiar prompt filenames without downloading a catalog.");
        config["reproductionPrompt"] = TaskPrompt.DefaultReproductionPrompt;
        var selected = await configuration.SaveAsync(config);
        foreach (var invalid in new JsonNode[] { JsonValue.Create(42)!, new JsonObject(), new JsonArray(), JsonValue.Create("../escape.md")!, JsonValue.Create("bad\0.md")! })
        {
            var bad = selected.DeepClone().AsObject(); bad["prPrompt"] = invalid.DeepClone();
            await ExpectAsync("INVALID_PROMPT_NAME", () => configuration.SaveAsync(bad));
        }
        var missing = selected.DeepClone().AsObject(); missing["prPrompt"] = "unbundled.prompt.md";
        await ExpectAsync("PROMPT_NOT_FOUND", () => configuration.SaveAsync(missing));
        var mismatch = selected.DeepClone().AsObject(); mismatch["issuePrompt"] = TaskPrompt.DefaultPrPrompt;
        await ExpectAsync("PROMPT_TARGET_MISMATCH", () => configuration.SaveAsync(mismatch));
        foreach (var (field, action, target, wrongPrompt) in new[]
        {
            ("prPrompt", "pr-review", "pr", TaskPrompt.DefaultE2ePrompt),
            ("e2ePrompt", "e2e", "pr", TaskPrompt.DefaultPrPrompt),
            ("issuePrompt", "issue-fix", "issue", TaskPrompt.DefaultReproductionPrompt),
            ("reproductionPrompt", "reproduction-setup", "issue", TaskPrompt.DefaultIssuePrompt)
        })
        {
            var wrongWorkflow = selected.DeepClone().AsObject();
            wrongWorkflow[field] = wrongPrompt;
            await ExpectAsync("PROMPT_ACTION_MISMATCH", () => configuration.SaveAsync(wrongWorkflow));
            Expect("PROMPT_ACTION_MISMATCH", () => TaskPrompt.ValidateActionSelection(fixture.Catalog, wrongWorkflow, action));
            Expect("PROMPT_ACTION_MISMATCH", () => TaskPrompt.Snapshot(fixture.Catalog, BusinessTask(action, target, 42, "Wrong workflow"), wrongWorkflow));
        }
        Check(JsonNode.DeepEquals(configuration.Read(), selected), "Invalid or incompatible selections must not partially alter valid settings.");
        var older = selected.DeepClone().AsObject(); older.Remove("e2ePrompt"); older.Remove("reproductionPrompt");
        var preserved = await configuration.SaveAsync(older);
        Check(preserved["e2ePrompt"]?.GetValue<string>() == TaskPrompt.DefaultE2ePrompt && preserved["reproductionPrompt"]?.GetValue<string>() == TaskPrompt.DefaultReproductionPrompt,
            "Older clients must preserve prompt choices they cannot edit.");
        preserved["prPrompt"] = "";
        var blank = await configuration.SaveAsync(preserved);
        Check(blank["prPrompt"]?.GetValue<string>() == "", "An explicitly cleared prompt choice remains cleared rather than silently selecting another workflow.");
        Expect("PROMPT_NOT_CONFIGURED", () => TaskPrompt.Snapshot(fixture.Catalog, BusinessTask("pr-review", "pr", 42, "Explicitly unconfigured"), blank));
    }

    private static JsonObject BusinessTask(string kind, string target, int number, string title)
    {
        var task = new JsonObject
        {
            ["requestId"] = Guid.NewGuid().ToString("D"), ["actionId"] = "prompt-fixture", ["actionKind"] = kind,
            ["repository"] = "microsoft/powertoys", ["target"] = new JsonObject { ["type"] = target, ["number"] = number },
            ["context"] = new JsonObject { ["title"] = title }, ["prompt"] = "WEB_CONTEXT_ONLY"
        };
        if (target == "pr") task["expectedHeadSha"] = new string('a', 40);
        return Protocol.ValidateTask(task);
    }

    private static Dictionary<string, string> Files(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect(string code, Action action) { try { action(); } catch (ProtocolException error) when (error.Code == code) { return; } throw new InvalidOperationException("Expected " + code); }
    private static async Task ExpectAsync(string code, Func<Task<JsonObject>> action) { try { await action(); } catch (ProtocolException error) when (error.Code == code) { return; } throw new InvalidOperationException("Expected " + code); }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "PulsePromptTests-" + Guid.NewGuid().ToString("N"));
        internal readonly Store Store;
        internal readonly PromptCatalog Catalog;
        internal int SessionCalls;
        internal Fixture()
        {
            Store = new Store(Root);
            Catalog = new PromptCatalog(Store, _ => { SessionCalls++; throw new InvalidOperationException("Bundled prompts must never create a GitHub session."); });
        }
        public void Dispose()
        {
            var full = Path.GetFullPath(Root);
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            Check(Path.GetDirectoryName(full)!.Equals(temporary, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("PulsePromptTests-", StringComparison.Ordinal), "Prompt fixture cleanup must remain within its dedicated temporary directory.");
            Directory.Delete(full, recursive: true);
        }
    }
}
