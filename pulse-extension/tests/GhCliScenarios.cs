using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Pulse.Host.Tests;

/// <summary>Offline gh command contracts. No credential command or GitHub request is executed.</summary>
internal static class GhCliScenarios
{
    internal static async Task RunAllAsync()
    {
        await EnumeratesAndSanitizesAccounts();
        await SelectsAccountPerCommandAndUsesStdin();
        await RequiresAValidSelectedAccount();
        ParsesHttpStatusAndPreservesUnknownWrites();
        Console.WriteLine("PASS gh: account detection, per-command identity, stdin JSON, HTTP errors, and uncertain writes (offline)");
    }

    private static async Task EnumeratesAndSanitizesAccounts()
    {
        var calls = 0;
        var result = await GitHubSession.GetAccountsAsync((start, input, limit) =>
        {
            calls++;
            Check(start.ArgumentList.SequenceEqual(new[] { "auth", "status", "--hostname", "github.com", "--json", "hosts" }), "Account detection must use machine-readable gh auth status.");
            Check(!start.Environment.ContainsKey("GH_TOKEN") && !start.Environment.ContainsKey("GITHUB_TOKEN") && !start.Environment.ContainsKey("GH_DEBUG"), "Saved-account detection must clear ambient credentials and HTTP debug logging.");
            Check(input is null && limit <= 1024 * 1024, "Account detection must have bounded output and no stdin body.");
            return Task.FromResult(new GhCommandResult(0, Accounts().ToJsonString()));
        });
        var accounts = result["accounts"]!.AsArray();
        Check(calls == 1 && result["available"]!.GetValue<bool>() && accounts.Count == 3, "One invalid account must not hide the valid accounts.");
        Check(accounts[0]!["login"]!.GetValue<string>() == "enterprise_user" && accounts[0]!["active"]!.GetValue<bool>(), "Managed account names and active state must survive detection.");
        Check(accounts[2]!["state"]!.GetValue<string>() == "error", "Failed logins must remain visible with their own status.");
        Check(accounts.OfType<JsonObject>().All(row => row.Count == 3) && !result.ToJsonString().Contains("private-value", StringComparison.Ordinal), "Only login, active, and state may leave the Host.");

        var empty = await GitHubSession.GetAccountsAsync((_, _, _) => Task.FromResult(new GhCommandResult(0, "{\"hosts\":{}}")));
        Check(!empty["available"]!.GetValue<bool>() && empty["error"]!["code"]!.GetValue<string>() == "GITHUB_AUTH_REQUIRED", "An installed CLI with no login needs a saved account.");
        var missing = await GitHubSession.GetAccountsAsync((_, _, _) => throw new System.ComponentModel.Win32Exception());
        Check(!missing["available"]!.GetValue<bool>() && missing["accounts"]!.AsArray().Count == 0, "Missing gh must produce a structured unavailable result.");
        var malformed = await GitHubSession.GetAccountsAsync((_, _, _) => Task.FromResult(new GhCommandResult(0, "private-value malformed output")));
        Check(!malformed.ToJsonString().Contains("private-value", StringComparison.Ordinal), "Raw account detection failures must not leak.");
        var failedAccounts = Accounts();
        foreach (var row in failedAccounts["hosts"]!["github.com"]!.AsArray().OfType<JsonObject>()) row["state"] = "error";
        var allFailed = await GitHubSession.GetAccountsAsync((_, _, _) => Task.FromResult(new GhCommandResult(0, failedAccounts.ToJsonString())));
        Check(!allFailed["available"]!.GetValue<bool>() && allFailed["accounts"]!.AsArray().Count == 3 && allFailed["error"] is JsonObject, "When every login is invalid, their individual status must remain visible.");
    }

    private static async Task SelectsAccountPerCommandAndUsesStdin()
    {
        const string credential = "fixture-credential-never-log";
        var requests = new List<ProcessStartInfo>();
        var writeBody = new JsonObject { ["body"] = "Full text 中文\n`$(untrusted)` \"quoted\"", ["comments"] = new JsonArray(new JsonObject { ["line"] = 2, ["body"] = "a suggestion" }) };
        var writes = 0;
        Task<GhCommandResult> Runner(ProcessStartInfo start, string? input, int _)
        {
            requests.Add(start);
            Check(!start.UseShellExecute && start.CreateNoWindow && start.RedirectStandardInput, "gh must run directly, hidden, with redirected stdin.");
            var args = start.ArgumentList.ToArray();
            Check(!args.Contains("switch") && !args.Contains("--show-token") && !args.Contains("--verbose"), "The Host must never change global auth or enable credential logging.");
            if (args.Take(2).SequenceEqual(new[] { "auth", "status" })) return Task.FromResult(new GhCommandResult(0, Accounts().ToJsonString()));
            if (args.Take(2).SequenceEqual(new[] { "auth", "token" }))
            {
                Check(args.SequenceEqual(new[] { "auth", "token", "--hostname", "github.com", "--user", "moooyo" }), "Credential selection must explicitly target the saved account.");
                Check(!start.Environment.ContainsKey("GH_TOKEN") && !start.Environment.ContainsKey("GITHUB_TOKEN"), "Token lookup must ignore ambient PATs.");
                return Task.FromResult(new GhCommandResult(0, credential + "\n"));
            }
            Check(args[0] == "api" && start.Environment["GH_TOKEN"] == credential && !args.Contains(credential), "Only the gh api child environment may receive the selected credential.");
            Check(args.Contains("--include") && args.Contains("--hostname") && args.Contains("github.com") && !args.Contains("--user"), "gh api must expose HTTP status and use github.com.");
            if (args[1] == "https://api.github.com/user")
            {
                Check(input is null && !args.Contains("--input"), "Identity reads must have no request body.");
                return Task.FromResult(Http(200, new JsonObject { ["login"] = "moooyo" }));
            }
            writes++;
            Check(args[1] == "https://api.github.com/repos/microsoft/PowerToys/issues/7/comments" && args.Contains("POST"), "The operation must use its fixed API URL and method.");
            Check(args.Contains("--input") && args.Contains("-") && input == writeBody.ToJsonString() && !args.Contains(input), "The complete JSON body must be stdin data, never shell arguments.");
            return Task.FromResult(Http(201, new JsonObject { ["id"] = 42 }));
        }

        using var session = await GitHubSession.OpenAsync(new JsonObject { ["githubAccount"] = "moooyo" }, Runner);
        Check(session.Account == "moooyo", "The session identity must come from /user for the saved account.");
        var written = await session.RequestAsync(HttpMethod.Post, "/repos/microsoft/PowerToys/issues/7/comments", writeBody);
        Check(written["id"]!.GetValue<int>() == 42 && writes == 1 && requests.Count == 4, "One explicit operation must issue one gh API write.");
        Check(requests.All(start => !start.Environment.ContainsKey("GH_TOKEN")), "Credentials must be removed from retained ProcessStartInfo after execution.");
        var beforeInvalid = requests.Count;
        foreach (var endpoint in new[] { "https://evil.example/api", "//evil.example", "/repos/{owner}/{repo}", "/repos/x/../y", "/repos/x/%2e%2e/y", "/repos/x/y#fragment", "/repos/x/y\\escape", "/repos/x/y?x=1", "/user\n" })
            await ExpectProtocol("INVALID_GITHUB_ENDPOINT", async () => { await session.RequestAsync(HttpMethod.Get, endpoint); });
        Check(requests.Count == beforeInvalid, "Invalid endpoints must never launch gh.");
    }

    private static async Task RequiresAValidSelectedAccount()
    {
        var calls = 0;
        Task<GhCommandResult> Runner(ProcessStartInfo start, string? _, int __)
        {
            calls++;
            Check(start.ArgumentList[1] == "status", "Unavailable selected accounts must stop before credential extraction or API calls.");
            return Task.FromResult(new GhCommandResult(0, Accounts().ToJsonString()));
        }
        await ExpectProtocol("GITHUB_AUTH_REQUIRED", async () => { using var session = await GitHubSession.OpenAsync(new JsonObject(), Runner); });
        Check(calls == 0, "No account selection must not silently choose the global active account.");
        foreach (var account in new[] { "missing-account", "expired" })
            await ExpectProtocol("GITHUB_AUTH_REQUIRED", async () => { using var session = await GitHubSession.OpenAsync(new JsonObject { ["githubAccount"] = account }, Runner); });
        Check(calls == 2, "Both missing and failed saved accounts must be rejected.");

        await ExpectProtocol("GITHUB_IDENTITY_MISMATCH", async () =>
        {
            using var session = await GitHubSession.OpenAsync(new JsonObject { ["githubAccount"] = "moooyo" }, (start, _, _) =>
            {
                var args = start.ArgumentList;
                return Task.FromResult(args[0] == "api" ? Http(200, new JsonObject { ["login"] = "enterprise_user" }) :
                    args[1] == "status" ? new GhCommandResult(0, Accounts().ToJsonString()) : new GhCommandResult(0, "fixture-token"));
            });
        });
    }

    private static void ParsesHttpStatusAndPreservesUnknownWrites()
    {
        Check(GitHubSession.ParseApiResponse(new GhCommandResult(0, "HTTP/2.0 200 OK\nContent-Type: application/json\n\n{\"id\":7}"), false)["id"]!.GetValue<int>() == 7, "HTTP/2 status with LF separators must parse.");
        Check(GitHubSession.ParseApiResponse(new GhCommandResult(0, "HTTP/2.0 204 No Content\r\n\r\n"), true).AsObject().Count == 0, "A successful empty response must be recognized.");
        foreach (var code in new[] { 401, 403, 404, 409, 422, 429 }) ExpectHttp(new GhCommandResult(1, $"HTTP/2.0 {code} Error\r\n\r\nprivate-value"), true, code, false);
        foreach (var code in new[] { 408, 500, 502, 503 }) ExpectHttp(new GhCommandResult(1, $"HTTP/2.0 {code} Error\r\n\r\nprivate-value"), true, code, true);
        foreach (var output in new[] { "private-value", "HTTP/2.0 200 OK\n\nprivate-value", "HTTP/2.0 200 OK\nmissing separator" })
        {
            ExpectHttp(new GhCommandResult(0, output), true, 0, true);
            ExpectHttp(new GhCommandResult(0, output), false, 0, false);
        }
        ExpectHttp(Http(200, new JsonObject()) with { ExitCode = 1 }, true, 0, true);
    }

    private static JsonObject Accounts() => new()
    {
        ["hosts"] = new JsonObject
        {
            ["github.com"] = new JsonArray(
                new JsonObject { ["login"] = "enterprise_user", ["active"] = true, ["state"] = "success", ["token"] = "private-value", ["tokenSource"] = "private-value" },
                new JsonObject { ["login"] = "moooyo", ["active"] = false, ["state"] = "success" },
                new JsonObject { ["login"] = "expired", ["active"] = false, ["state"] = "error", ["error"] = "private-value" }),
            ["other.example"] = new JsonArray(new JsonObject { ["login"] = "other", ["active"] = true, ["state"] = "success" })
        }
    };
    private static GhCommandResult Http(int code, JsonNode body) => new(0, $"HTTP/2.0 {code} Status\r\nContent-Type: application/json\r\n\r\n{body.ToJsonString()}");
    private static void ExpectHttp(GhCommandResult result, bool writing, int code, bool uncertain)
    {
        try { GitHubSession.ParseApiResponse(result, writing); }
        catch (GitHubRequestException ex)
        {
            Check(ex.StatusCode == code && ex.Uncertain == uncertain && !ex.Message.Contains("private-value", StringComparison.Ordinal), "HTTP status must define known failures versus uncertain writes without raw output leakage.");
            return;
        }
        throw new InvalidOperationException("Expected GitHubRequestException.");
    }
    private static async Task ExpectProtocol(string code, Func<Task> action)
    {
        try { await action(); } catch (ProtocolException ex) when (ex.Code == code) { return; }
        throw new InvalidOperationException("Expected ProtocolException " + code);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
