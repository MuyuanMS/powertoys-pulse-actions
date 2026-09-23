using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pulse.Host.Tests;

/// <summary>Export actual bundled prompt assembly with synthetic context, without running any workflow.</summary>
internal static class PromptAuditExporter
{
    private const string Permission = "workspace-write";
    private const string ParentRunId = "11111111-1111-4111-8111-111111111111";
    private static readonly string Revision = new('a', 40);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };
    private static readonly Profile[] Profiles =
    [
        new("pr-review-static", "pr-review", "PR Review / Static", "static"),
        new("pr-review-build-tests", "pr-review", "PR Review / Build and automated tests", "build-tests"),
        new("pr-review-ui-e2e", "pr-review", "PR Review / UI and E2E", "ui-e2e"),
        new("issue-fix", "issue-fix", "Issue Local Fix"),
        new("reproduction-setup", "reproduction-setup", "Issue Reproduction Setup"),
        new("e2e", "e2e", "Standalone PR E2E"),
        new("feature-research", "feature-research", "Issue Feature Research"),
        new("bug-investigation", "bug-investigation", "Issue Bug Investigation"),
        new("feature-implement", "feature-implement", "Issue Feature Implementation"),
        new("issue-verify", "issue-verify", "Issue Plan Verification / Original source"),
        new("pr-verify-build-tests", "pr-verify", "Linked PR Verification / Build and automated tests", "build-tests"),
        new("pr-verify-ui-e2e", "pr-verify", "Linked PR Verification / UI and E2E", "ui-e2e"),
        new("issue-verify-local-candidate", "issue-verify", "Issue Plan Verification / Local candidate", LocalCandidate: true)
    ];

    internal static async Task ExportAsync(string destination)
    {
        var output = ValidateDestination(destination);
        var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var fixtureRoot = Path.GetFullPath(Path.Combine(temporary, "PulsePromptAudit-" + Guid.NewGuid().ToString("N")));
        try
        {
            // Only this new temporary Store is read. No installed Host settings, task records,
            // executable inventory, account session, or user repository is consulted.
            var store = new Store(fixtureRoot);
            var catalog = new PromptCatalog(store, _ => throw new InvalidOperationException("Prompt audit cannot open a GitHub session."));
            var selections = new Configuration(store).Read();
            selections["permission"] = Permission;
            var originalSelections = selections.ToJsonString();
            Directory.CreateDirectory(output);

            var schema = CliAdapter.ResultSchema(3);
            if (schema["properties"]?["schemaVersion"]?["const"]?.GetValue<int>() != 3)
                throw new InvalidOperationException("Prompt audit requires the actual Host v3 schema.");
            var schemaText = JsonText(schema);
            await WriteNewAsync(Path.Combine(output, "schema.json"), schemaText);
            var entries = new JsonArray();
            for (var index = 0; index < Profiles.Length; index++)
            {
                var profile = Profiles[index];
                var original = FixtureTask(profile, index);
                var originalTask = original.ToJsonString();
                var task = Protocol.ValidateTask(original, allowFollowUp: profile.Kind is "pr-verify" or "feature-implement" or "issue-verify");
                var validatedTask = task.ToJsonString();
                var snapshot = TaskPrompt.Snapshot(catalog, task, selections)
                    ?? throw new InvalidOperationException("Every audit profile requires a real bundled prompt snapshot.");
                if (snapshot["schemaVersion"]?.GetValue<int>() != 3)
                    throw new InvalidOperationException("A new audit profile did not resolve to its bundled v3 prompt.");
                var template = snapshot["body"]!.GetValue<string>();
                var profileDirectory = OwnedChild(output, profile.Id);
                if (Directory.Exists(profileDirectory) || File.Exists(profileDirectory))
                    throw new IOException("An audit profile destination already exists; no files were overwritten.");
                Directory.CreateDirectory(profileDirectory);
                var taskText = JsonText(task);
                var snapshotText = JsonText(snapshot);
                await WriteNewAsync(Path.Combine(profileDirectory, "task.json"), taskText);
                await WriteNewAsync(Path.Combine(profileDirectory, "prompt-template.json"), snapshotText);

                var agents = new JsonArray();
                foreach (var agent in new[] { "codex", "copilot" })
                {
                    var prompt = CliAdapter.Prompt(task, Permission, agent, template, resultSchemaVersion: 3);
                    VerifyAssembledSchema(prompt, schema);
                    var name = agent + ".prompt.md";
                    await WriteNewAsync(Path.Combine(profileDirectory, name), prompt);
                    var agentEntry = Stats(prompt);
                    agentEntry["agent"] = agent;
                    agentEntry["permission"] = Permission;
                    agentEntry["file"] = profile.Id + "/" + name;
                    agents.Add(agentEntry);
                }
                if (original.ToJsonString() != originalTask || task.ToJsonString() != validatedTask || selections.ToJsonString() != originalSelections)
                    throw new InvalidOperationException("Prompt assembly must preserve fixture tasks and prompt selections.");
                entries.Add(new JsonObject
                {
                    ["id"] = profile.Id, ["label"] = profile.Label, ["actionKind"] = profile.Kind,
                    ["reviewMode"] = ReviewModes.Mode(task), ["schemaVersion"] = 3, ["permission"] = Permission,
                    ["target"] = task["target"]!.DeepClone(), ["sourceKind"] = task["planSource"]?["sourceKind"]?.DeepClone(),
                    ["taskFile"] = profile.Id + "/task.json", ["taskFileStats"] = Stats(taskText),
                    ["templateFile"] = profile.Id + "/prompt-template.json", ["templateFileStats"] = Stats(snapshotText),
                    ["selectedTemplate"] = snapshot["name"]!.DeepClone(), ["templateSha"] = snapshot["sha"]?.DeepClone(),
                    ["renderedSha"] = snapshot["renderedSha"]?.DeepClone(), ["templateBodyStats"] = Stats(template),
                    ["requiredChecks"] = snapshot["requiredChecks"]!.DeepClone(),
                    ["scopeInstructions"] = ReviewModes.Instructions(task), ["agents"] = agents
                });
            }
            var schemaEntry = Stats(schemaText); schemaEntry["file"] = "schema.json";
            var audit = new JsonObject
            {
                ["formatVersion"] = 1, ["resultSchemaVersion"] = 3,
                ["fixture"] = "Synthetic microsoft/PowerToys target 42 and fixed placeholder revisions; no source or account was queried.",
                ["measurement"] = "utf8Bytes counts UTF-8 bytes without a BOM; characters counts .NET UTF-16 code units. Neither value is a model token count.",
                ["permission"] = Permission, ["profileCount"] = entries.Count, ["assembledPromptCount"] = entries.Count * 2,
                ["schema"] = schemaEntry, ["profiles"] = entries
            };
            // A complete index is written last. CreateNew applies to every artifact, including
            // this final marker, so a concurrent file cannot be silently overwritten.
            await WriteNewAsync(Path.Combine(output, "index.json"), JsonText(audit));
        }
        finally
        {
            if (!string.Equals(Path.GetDirectoryName(fixtureRoot), temporary, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(fixtureRoot).StartsWith("PulsePromptAudit-", StringComparison.Ordinal))
                throw new InvalidOperationException("Prompt audit cleanup escaped its dedicated temporary directory.");
            if (Directory.Exists(fixtureRoot)) Directory.Delete(fixtureRoot, recursive: true);
            if (Directory.Exists(fixtureRoot)) throw new IOException("Prompt audit fixture cleanup did not finish.");
        }
    }

    private static JsonObject FixtureTask(Profile profile, int index)
    {
        var isPr = profile.Kind is "pr-review" or "pr-verify" or "e2e";
        var task = new JsonObject
        {
            ["requestId"] = "00000000-0000-4000-8000-" + (index + 1).ToString("D12", CultureInfo.InvariantCulture),
            ["actionId"] = "prompt-audit:" + profile.Id, ["actionKind"] = profile.Kind, ["repository"] = "Microsoft/PowerToys",
            ["target"] = new JsonObject { ["type"] = isPr ? "pr" : "issue", ["number"] = 42 },
            ["context"] = new JsonObject { ["title"] = "Synthetic prompt audit target", ["fixture"] = true },
            ["prompt"] = "Inspect the accepted synthetic workflow context. This audit exports instructions without executing them."
        };
        if (isPr) task["expectedHeadSha"] = Revision;
        if (profile.Mode is not null) task["reviewOptions"] = new JsonObject { ["mode"] = profile.Mode };
        if (profile.Kind == "pr-verify")
        {
            task["followUp"] = new JsonObject
            {
                ["parentRunId"] = ParentRunId, ["recommendationId"] = new string('c', 64), ["parentResultFingerprint"] = new string('b', 64),
                ["subject"] = "original-pr", ["revisionSha"] = Revision
            };
            task["context"]!["reviewVerification"] = new JsonObject
            {
                ["parentRunId"] = ParentRunId, ["scenarioChecks"] = null,
                ["scenarioIdPrefix"] = "e2e-" + new string('c', 24) + "-",
                ["parentSummary"] = "Synthetic review with a saved verification question.",
                ["parentReviewStatus"] = "no-blocking-findings", ["parentAssessmentStatus"] = "inconclusive",
                ["prerequisitesReportedReady"] = false,
                ["recommendation"] = new JsonObject
                {
                    ["mode"] = profile.Mode, ["reason"] = "Does the selected scenario match the recorded expectation?",
                    ["scenarios"] = new JsonArray("Inspect the synthetic scenario specified by the saved parent review."),
                    ["expectedResults"] = new JsonArray("Record the observed behavior without changing the original source."),
                    ["prerequisites"] = new JsonArray("Use the accepted source revision and the selected execution scope."),
                    ["evidence"] = new JsonArray("Synthetic parent-review context; no external evidence was collected."), ["readiness"] = "unknown"
                }
            };
        }
        if (profile.Kind is "feature-implement" or "issue-verify")
        {
            var source = new JsonObject
            {
                ["parentRunId"] = ParentRunId, ["parentResultFingerprint"] = new string('b', 64), ["proposalId"] = "audit-proposal-1", ["planId"] = "audit-plan-1",
                ["repository"] = "microsoft/powertoys", ["target"] = task["target"]!.DeepClone(), ["revisionSha"] = Revision,
                ["sourceKind"] = profile.LocalCandidate ? "local-candidate" : "original"
            };
            if (profile.LocalCandidate) source["candidateSnapshotHash"] = new string('d', 64);
            task["planSource"] = source;
            task["context"]!["resultPlan"] = new JsonObject
            {
                ["parentRunId"] = ParentRunId, ["parentReportPath"] = "C:\\synthetic-pulse-audit\\runs\\" + ParentRunId + "\\result.json",
                ["parentSummary"] = "Synthetic saved research plan.",
                ["parentActionKind"] = profile.Kind == "feature-implement" ? "feature-research" : "bug-investigation",
                ["parentOutcome"] = "completed", ["prerequisitesReportedReady"] = false, ["initialRevisionSha"] = Revision,
                ["plan"] = new JsonObject
                {
                    ["id"] = "audit-plan-1", ["kind"] = profile.Kind, ["summary"] = "A synthetic saved plan for prompt inspection.",
                    ["steps"] = new JsonArray(profile.Kind == "feature-implement" ? "Implement the accepted feature requirements within the isolated worktree." : "Verify the accepted scenario against the inherited source."),
                    ["acceptanceCriteria"] = new JsonArray("Record concrete observations for the accepted scope and retain limitations."),
                    ["prerequisites"] = new JsonArray("Preserve the supplied source identity and saved permissions."),
                    ["evidence"] = new JsonArray("Synthetic saved-plan fixture; these are audit inputs, not proof that a workflow ran.")
                }
            };
            if (profile.LocalCandidate)
            {
                task["context"]!["resultPlan"]!["sourceKind"] = "local-candidate";
                task["context"]!["resultPlan"]!["candidateSnapshotHash"] = new string('d', 64);
            }
        }
        return task;
    }

    private static string ValidateDestination(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination) || !Path.IsPathFullyQualified(destination))
            throw new ArgumentException("The prompt audit destination must be an absolute directory path.", nameof(destination));
        var absolute = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (File.Exists(absolute) || Directory.Exists(absolute) && Directory.EnumerateFileSystemEntries(absolute).Any())
            throw new IOException("The prompt audit destination must be empty; existing files will not be overwritten.");
        return absolute;
    }

    private static string OwnedChild(string destination, string name)
    {
        var full = Path.GetFullPath(Path.Combine(destination, name));
        var prefix = Path.TrimEndingDirectorySeparator(destination) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("An audit profile escaped its destination.");
        return full;
    }

    private static void VerifyAssembledSchema(string prompt, JsonObject expected)
    {
        const string begin = "--- BEGIN RESULT JSON SCHEMA ---";
        const string end = "--- END RESULT JSON SCHEMA ---";
        var start = prompt.IndexOf(begin, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException("The assembled audit prompt is missing its schema marker.");
        start += begin.Length;
        var stop = prompt.IndexOf(end, start, StringComparison.Ordinal);
        if (stop < start || !JsonNode.DeepEquals(JsonNode.Parse(prompt[start..stop].Trim()), expected))
            throw new InvalidOperationException("An assembled audit prompt did not contain the exact full Host v3 schema.");
    }

    private static string JsonText(JsonNode value) => value.ToJsonString(PrettyJson) + "\n";
    private static JsonObject Stats(string text) => new() { ["utf8Bytes"] = Utf8.GetByteCount(text), ["characters"] = text.Length };
    private static async Task WriteNewAsync(string path, string text)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await stream.WriteAsync(Utf8.GetBytes(text));
        await stream.FlushAsync();
    }

    private sealed record Profile(string Id, string Kind, string Label, string? Mode = null, bool LocalCandidate = false);
}
