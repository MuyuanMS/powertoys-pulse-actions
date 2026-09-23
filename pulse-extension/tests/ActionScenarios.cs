using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Pulse.Host.Tests;

/// <summary>Public local-action readiness and fresh PR context contracts, without model or network calls.</summary>
internal static class ActionScenarios
{
    internal static async Task RunAllAsync()
    {
        using var fixture = new Fixture();
        await ReadinessIsReadOnlyAndPublicSafe(fixture);
        await TargetsAreFixedAndFresh(fixture);
        await DispatcherRejectsInvalidActions(fixture);
        Console.WriteLine("PASS actions: per-action readiness, private-data filtering, read-only checks, fixed PR targets and fresh complete SHA (offline)");
    }

    private static async Task ReadinessIsReadOnlyAndPublicSafe(Fixture fixture)
    {
        var config = new Configuration(fixture.Store).Read();
        config["agent"] = "copilot";
        config["githubAccount"] = "private-account";
        config["mainRepoFolder"] = "C:\\private-repository";
        config["worktreeRoot"] = "C:\\private-worktrees";
        config["cliSelections"] = new JsonObject { ["codex"] = "C:\\private-codex\\codex.exe", ["copilot"] = "C:\\private-copilot\\copilot.exe" };
        fixture.Save(config);
        var files = fixture.Files();
        var probed = new List<string>();
        var accountCalls = 0;
        var foldersChecked = 0;
        var readiness = new ActionReadiness(fixture.Store, agent =>
        {
            probed.Add(agent);
            return Task.FromResult(new JsonObject { ["available"] = true, ["path"] = "C:\\private-cli", ["version"] = "private-version" });
        }, () =>
        {
            accountCalls++;
            return Task.FromResult(new JsonObject { ["accounts"] = new JsonArray(new JsonObject { ["login"] = "private-account", ["state"] = "success" }) });
        }, value =>
        {
            foldersChecked++;
            Check(value["mainRepoFolder"]!.GetValue<string>() == "C:\\private-repository", "Readiness must use locally saved folders.");
            return Task.CompletedTask;
        });
        var ready = await readiness.CheckAsync(new JsonObject { ["actionKind"] = "reproduction-setup" });
        Check(ready["ready"]!.GetValue<bool>() && ready["blockers"]!.AsArray().Count == 0 && ready["reviewModes"]!.AsArray().Count == 3, "Bundled reproduction instructions must allow readiness without prompt synchronization and advertise supported review scopes.");
        Check(probed.SequenceEqual(new[] { "copilot" }) && foldersChecked == 1 && accountCalls == 0, "Only the selected CLI is probed and an issue reproduction does not require a gh account probe.");
        var pr = await readiness.CheckAsync(new JsonObject { ["actionKind"] = "pr-review" });
        Check(pr["ready"]!.GetValue<bool>() && pr["blockers"]!.AsArray().Count == 0 && accountCalls == 1, "PR readiness uses its bundled prompt and still verifies the selected local GitHub account.");
        Check(!pr.ToJsonString().Contains("private", StringComparison.Ordinal), "Public readiness must never expose account names, folders, paths, or raw CLI details.");
        Check(fixture.Files() == files && !fixture.Store.RunIds().Any(), "Readiness must not write local records, start workers, or create a task.");

        foreach (var mode in ReviewModes.Values)
        {
            var scoped = await readiness.CheckAsync(new JsonObject { ["actionKind"] = "pr-review", ["reviewOptions"] = new JsonObject { ["mode"] = mode } });
            Check(scoped["reviewOptions"]?["mode"]?.GetValue<string>() == mode && scoped["reviewModes"]!.AsArray().Any(item => item?.GetValue<string>() == mode), "Readiness confirms the exact requested scope without exposing local settings.");
        }
        await ExpectAsync("INVALID_REQUEST", () => readiness.CheckAsync(new JsonObject { ["actionKind"] = "pr-review", ["reviewOptions"] = new JsonObject { ["mode"] = "everything" } }));
        await ExpectAsync("INVALID_REQUEST", () => readiness.CheckAsync(new JsonObject { ["actionKind"] = "issue-fix", ["reviewOptions"] = new JsonObject { ["mode"] = "static" } }));
        await ExpectAsync("INVALID_REQUEST", () => readiness.CheckAsync(new JsonObject { ["actionKind"] = "pr-verify" }));

        var broken = new ActionReadiness(fixture.Store,
            _ => Task.FromResult(new JsonObject { ["available"] = false, ["error"] = new JsonObject { ["code"] = "CLI_PROBE_FAILED", ["message"] = "private-cli-error" } }),
            () => Task.FromResult(new JsonObject { ["accounts"] = new JsonArray(new JsonObject { ["login"] = "different-account", ["state"] = "success" }) }),
            _ => throw new ProtocolException("WORKTREE_PATH_INVALID", "private-folder-error"));
        var errors = await broken.CheckAsync(new JsonObject { ["actionKind"] = "e2e" });
        Check(errors["blockers"]!.AsArray().Count == 3 && !errors.ToJsonString().Contains("private", StringComparison.Ordinal), "Independent folder, CLI and account failures must be returned together without inventing a missing bundled prompt or exposing private diagnostics.");
        var missing = new ActionReadiness(fixture.Store,
            _ => Task.FromResult(new JsonObject { ["available"] = false, ["path"] = "private-cli-path", ["error"] = new JsonObject { ["code"] = "CLI_NOT_FOUND", ["message"] = "private-installation-error" } }),
            checkFolders: _ => Task.CompletedTask);
        var missingResponse = await missing.CheckAsync(new JsonObject { ["actionKind"] = "reproduction-setup" });
        var missingBlocker = missingResponse["blockers"]!.AsArray().Single()!;
        Check(missingResponse["ready"]!.GetValue<bool>() == false && missingBlocker["code"]!.GetValue<string>() == "CLI_SELECTION_UNAVAILABLE" &&
            !missingResponse.ToJsonString().Contains("private", StringComparison.Ordinal),
            "A missing selected executable reports a public-safe selection error without exposing its path.");
        var callsBefore = probed.Count;
        await ExpectAsync("INVALID_REQUEST", () => readiness.CheckAsync(new JsonObject { ["actionKind"] = "merge" }));
        await ExpectAsync("INVALID_REQUEST", () => readiness.CheckAsync(new JsonObject { ["actionKind"] = "e2e", ["cliPath"] = "override" }));
        Check(probed.Count == callsBefore, "Invalid or override-bearing actions are rejected before touching local tools.");
    }

    private static async Task TargetsAreFixedAndFresh(Fixture fixture)
    {
        var response = new JsonObject
        {
            ["number"] = 42, ["title"] = "PR title\r\n" + new string('x', 300),
            ["head"] = new JsonObject { ["sha"] = new string('A', 40) },
            ["base"] = new JsonObject { ["repo"] = new JsonObject { ["full_name"] = "microsoft/PowerToys" } },
            ["privateMetadata"] = "must-not-leak"
        };
        var requests = new List<string>();
        var sessions = 0;
        var service = new GitHubService(fixture.Store, config =>
        {
            sessions++;
            Check(config["githubAccount"]!.GetValue<string>() == "saved-account", "Target lookup must use the saved selected GitHub account.");
            return Task.FromResult(new GitHubSession(new HttpClient(new Source(requests, () => response)), "saved-account"));
        });
        var config = new JsonObject { ["githubAccount"] = "saved-account" };
        var payload = new JsonObject { ["target"] = new JsonObject { ["type"] = "pr", ["number"] = 42 } };
        var files = fixture.Files();
        var first = await service.GetTargetAsync(payload, config);
        Check(first["headSha"]!.GetValue<string>() == new string('a', 40) && first["title"]!.GetValue<string>().Length == 256 && !first["title"]!.GetValue<string>().Contains('\n'), "Fresh target responses contain a normalized complete SHA and a bounded, control-free title.");
        Check(first.Count == 3 && first["target"]!["number"]!.GetValue<int>() == 42 && !first.ToJsonString().Contains("must-not-leak", StringComparison.Ordinal), "Only fixed public target metadata may cross the bridge.");
        response["head"]!["sha"] = new string('b', 40);
        Check((await service.GetTargetAsync(payload, config))["headSha"]!.GetValue<string>() == new string('b', 40), "Every lookup must read the current remote HEAD rather than caching an older revision.");
        Check(requests.SequenceEqual(new[] { "/repos/microsoft/powertoys/pulls/42", "/repos/microsoft/powertoys/pulls/42" }), "PR context uses only the fixed upstream repository's GET endpoint.");
        Check(fixture.Files() == files, "Target lookup must not write local records.");

        var before = sessions;
        foreach (var forbidden in new[] { "repository", "url", "method", "command" })
        {
            var invalid = (JsonObject)payload.DeepClone();
            invalid[forbidden] = "override";
            await ExpectAsync("INVALID_REQUEST", () => service.GetTargetAsync(invalid, config));
        }
        foreach (var number in new JsonNode[] { JsonValue.Create(0)!, JsonValue.Create(-1)!, JsonValue.Create(1.5)!, JsonValue.Create("42")! })
        {
            var invalid = (JsonObject)payload.DeepClone();
            invalid["target"]!["number"] = number.DeepClone();
            await ExpectAsync("INVALID_REQUEST", () => service.GetTargetAsync(invalid, config));
        }
        var wrongType = (JsonObject)payload.DeepClone();
        wrongType["target"]!["type"] = "issue";
        await ExpectAsync("INVALID_REQUEST", () => service.GetTargetAsync(wrongType, config));
        wrongType["target"]!["type"] = "pr";
        wrongType["target"]!["url"] = "https://example.invalid";
        await ExpectAsync("INVALID_REQUEST", () => service.GetTargetAsync(wrongType, config));
        Check(sessions == before, "Rejected external targets must not open GitHub sessions.");

        response["number"] = 43;
        await ExpectAsync("TARGET_MISMATCH", () => service.GetTargetAsync(payload, config));
        response["number"] = 42;
        response["base"]!["repo"]!["full_name"] = "microsoft/another";
        await ExpectAsync("TARGET_MISMATCH", () => service.GetTargetAsync(payload, config));
        response["base"]!["repo"]!["full_name"] = "microsoft/PowerToys";
        foreach (var sha in new[] { "", "short", new string('a', 64), new string('z', 40), new string('a', 40) + "\n" })
        {
            response["head"]!["sha"] = sha;
            await ExpectAsync("INVALID_GITHUB_RESPONSE", () => service.GetTargetAsync(payload, config));
        }
    }

    private static async Task DispatcherRejectsInvalidActions(Fixture fixture)
    {
        var dispatcher = new Dispatcher(fixture.Store);
        foreach (var (type, payload, code) in new[]
        {
            ("actions.check", new JsonObject { ["actionKind"] = "arbitrary-command" }, "INVALID_REQUEST"),
            ("targets.get", new JsonObject { ["target"] = new JsonObject { ["type"] = "issue", ["number"] = 42 } }, "INVALID_REQUEST"),
            ("webActions.prepare", new JsonObject { ["draft"] = new JsonObject(), ["sourceOrigin"] = "https://evil.invalid" }, "ORIGIN_NOT_ALLOWED"),
            ("webActions.get", new JsonObject { ["operationId"] = "test", ["sourceOrigin"] = "https://evil.invalid" }, "ORIGIN_NOT_ALLOWED")
        })
        {
            var result = await dispatcher.HandleAsync(new JsonObject { ["id"] = "fixture", ["protocolVersion"] = 1, ["type"] = type, ["payload"] = payload });
            Check(result["ok"]!.GetValue<bool>() == false && result["error"]!["code"]!.GetValue<string>() == code, "The dispatcher must reject unsupported or unauthorized bridge payloads before executing them.");
        }
    }

    private sealed class Source(List<string> requests, Func<JsonObject> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.Method == HttpMethod.Get && request.RequestUri!.Host == "api.github.com", "Target lookup must only perform GitHub reads.");
            requests.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response().ToJsonString(), Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "PulseActionTests-" + Guid.NewGuid().ToString("N"));
        internal readonly Store Store;
        internal Fixture() => Store = new Store(Root);
        internal void Save(JsonObject config) => Store.WriteJson(Path.Combine(Root, "config.json"), config);
        internal string Files() => string.Join("\n", Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(path => path + ":" + Protocol.Hash(File.ReadAllText(path))));
        public void Dispose()
        {
            var absolute = Path.GetFullPath(Root);
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (absolute.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(absolute).StartsWith("PulseActionTests-", StringComparison.Ordinal) && Directory.Exists(absolute))
                Directory.Delete(absolute, recursive: true);
        }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task ExpectAsync(string code, Func<Task<JsonObject>> action)
    {
        try { await action(); }
        catch (ProtocolException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected " + code);
    }
}
