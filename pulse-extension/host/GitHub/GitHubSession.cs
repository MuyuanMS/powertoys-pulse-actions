using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pulse.Host;

/// <summary>A short-lived gh session. The selected credential exists only in process memory.</summary>
public sealed class GitHubSession : IDisposable
{
    private readonly HttpClient? testClient;
    private readonly GhCommandRunner runCommand;
    private string? token;
    public string Account { get; private set; }
    internal const int MaximumResponseBytes = 16 * 1024 * 1024;

    /// <summary>Inject an offline HTTP handler for service contract tests. Production uses gh api.</summary>
    public GitHubSession(HttpClient client, string account)
    {
        testClient = client;
        Account = account;
        runCommand = GhCli.RunAsync;
    }

    private GitHubSession(string credential, GhCommandRunner runner)
    {
        token = credential;
        Account = "";
        runCommand = runner;
    }

    public static Task<JsonObject> GetAccountsAsync() => GetAccountsAsync(GhCli.RunAsync);

    internal static async Task<JsonObject> GetAccountsAsync(GhCommandRunner runner)
    {
        try
        {
            var start = GhCli.Start("auth", "status", "--hostname", "github.com", "--json", "hosts");
            var result = await runner(start, null, 1024 * 1024);
            if (result.ExitCode != 0) return AccountsUnavailable("GITHUB_CLI_UNAVAILABLE", "GitHub CLI could not read its saved accounts.", "Install or update GitHub CLI and run gh auth status.");
            var document = JsonNode.Parse(result.Output)?.AsObject() ?? throw new JsonException();
            if (document["hosts"] is not JsonObject hosts) throw new JsonException();
            var accounts = new JsonArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in (hosts["github.com"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var login = StringValue(row, "login");
                if (!ValidAccount(login) || !seen.Add(login)) continue;
                var state = StringValue(row, "state");
                if (state is not ("success" or "error" or "timeout")) state = "error";
                // Never relay the command's token, scopes, tokenSource, or raw error fields.
                accounts.Add(new JsonObject
                {
                    ["login"] = login,
                    ["active"] = row["active"] is JsonValue active && active.TryGetValue<bool>(out var value) && value,
                    ["state"] = state
                });
            }
            var available = accounts.OfType<JsonObject>().Any(row => StringValue(row, "state") == "success");
            var response = new JsonObject { ["available"] = available, ["accounts"] = accounts };
            if (!available) response["error"] = AuthError();
            return response;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or JsonException or OperationCanceledException or DecoderFallbackException)
        {
            return AccountsUnavailable("GITHUB_CLI_UNAVAILABLE", "The local GitHub CLI account status is unavailable.", "Install or update GitHub CLI and run gh auth login for github.com.");
        }
    }

    public static Task<GitHubSession> OpenAsync(JsonObject config) => OpenAsync(config, GhCli.RunAsync);

    internal static async Task<GitHubSession> OpenAsync(JsonObject config, GhCommandRunner runner)
    {
        var configuredAccount = StringValue(config, "githubAccount").Trim();
        if (configuredAccount.Length == 0) throw AuthenticationRequired();
        if (!ValidAccount(configuredAccount))
            throw new ProtocolException("INVALID_CONFIG", "The GitHub account name is invalid.", "Choose an existing github.com account in settings.");

        var status = await GetAccountsAsync(runner);
        if (!status["accounts"]!.AsArray().OfType<JsonObject>().Any(row =>
            StringValue(row, "login").Equals(configuredAccount, StringComparison.OrdinalIgnoreCase) && StringValue(row, "state") == "success"))
            throw AuthenticationRequired();

        string credential;
        try
        {
            var start = GhCli.Start("auth", "token", "--hostname", "github.com", "--user", configuredAccount);
            var result = await runner(start, null, 8192);
            credential = result.Output.Trim();
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(credential) || credential.Any(char.IsWhiteSpace))
                throw new InvalidOperationException();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or OperationCanceledException or DecoderFallbackException)
        {
            throw AuthenticationRequired();
        }

        var session = new GitHubSession(credential, runner);
        try
        {
            var identity = await session.RequestAsync(HttpMethod.Get, "/user") as JsonObject ?? throw UnknownResult(false);
            var login = StringValue(identity, "login");
            if (!login.Equals(configuredAccount, StringComparison.OrdinalIgnoreCase))
                throw new ProtocolException("GITHUB_IDENTITY_MISMATCH", "GitHub returned a different account from the selected account.", "Correct the saved GitHub CLI account before submitting.");
            session.Account = login;
            return session;
        }
        catch { session.Dispose(); throw; }
    }

    public async Task<JsonNode> RequestAsync(HttpMethod method, string endpoint, JsonObject? body = null)
    {
        // All callers supply fixed REST paths assembled from validated identifiers, never browser URLs.
        if (!Regex.IsMatch(endpoint, @"\A/(?:[A-Za-z0-9_.-]+/)*[A-Za-z0-9_.-]+(?:\?(?:state=all&)?per_page=100&page=[1-9][0-9]*)?\z") ||
            endpoint.Split('?', 2)[0].Split('/').Any(segment => segment is "." or "..") ||
            method != HttpMethod.Get && method != HttpMethod.Post && method != HttpMethod.Patch &&
                !(method == HttpMethod.Put && Regex.IsMatch(endpoint, @"\A/repos/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/pulls/[1-9][0-9]*/merge\z")))
            throw new ProtocolException("INVALID_GITHUB_ENDPOINT", "The GitHub endpoint or method is invalid.");
        var writing = method != HttpMethod.Get;
        try
        {
            if (testClient is not null) return await RequestOfflineAsync(method, endpoint, body);
            if (token is null) throw new ObjectDisposedException(nameof(GitHubSession));
            var start = GhCli.Start("api", "https://api.github.com" + endpoint, "--hostname", "github.com", "--method", method.Method,
                "--include", "--header", "Accept: application/vnd.github+json", "--header", "X-GitHub-Api-Version: 2022-11-28");
            if (body is not null)
            {
                start.ArgumentList.Add("--header"); start.ArgumentList.Add("Content-Type: application/json; charset=utf-8");
                start.ArgumentList.Add("--input"); start.ArgumentList.Add("-");
            }
            // gh api has no --user flag. Override only this child process; never switch global gh state.
            start.Environment["GH_TOKEN"] = token;
            GhCommandResult result;
            try { result = await runCommand(start, body?.ToJsonString(), MaximumResponseBytes); }
            finally { start.Environment.Remove("GH_TOKEN"); }
            return ParseApiResponse(result, writing);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or HttpRequestException or OperationCanceledException or IOException or JsonException or DecoderFallbackException)
        {
            throw UnknownResult(writing);
        }
    }

    internal static JsonNode ParseApiResponse(GhCommandResult result, bool writing)
    {
        var split = result.Output.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var separatorLength = 4;
        if (split < 0) { split = result.Output.IndexOf("\n\n", StringComparison.Ordinal); separatorLength = 2; }
        var firstLineEnd = result.Output.IndexOf('\n');
        var status = firstLineEnd < 0 ? Match.Empty : Regex.Match(result.Output[..firstLineEnd].TrimEnd('\r'), @"^HTTP/\S+ ([1-5][0-9]{2})(?: .*)?$");
        if (split < 0 || !status.Success) throw UnknownResult(writing);
        var code = int.Parse(status.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (code is < 200 or >= 300)
            throw new GitHubRequestException(code, writing && (code >= 500 || code == 408), ErrorFor(code), GuidanceFor(code));
        if (result.ExitCode != 0) throw UnknownResult(writing);
        var json = result.Output[(split + separatorLength)..];
        try { return code == 204 && string.IsNullOrWhiteSpace(json) ? new JsonObject() : JsonNode.Parse(json) ?? throw new JsonException(); }
        catch (JsonException) { throw UnknownResult(writing); }
    }

    private async Task<JsonNode> RequestOfflineAsync(HttpMethod method, string endpoint, JsonObject? body)
    {
        using var request = new HttpRequestMessage(method, "https://api.github.com" + endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await testClient!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var writing = method != HttpMethod.Get;
        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            throw new GitHubRequestException(code, writing && (code >= 500 || code == 408), ErrorFor(code), GuidanceFor(code));
        }
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk)) != 0)
        {
            if (buffer.Length + count > MaximumResponseBytes) throw UnknownResult(writing);
            buffer.Write(chunk, 0, count);
        }
        return JsonNode.Parse(buffer.ToArray()) ?? throw new JsonException();
    }

    public async Task<JsonArray> ReadPagesAsync(string endpoint, int maximumPages = 100, bool includeClosed = false)
    {
        var all = new JsonArray();
        for (var page = 1; page <= maximumPages; page++)
        {
            var rows = (await RequestAsync(HttpMethod.Get, $"{endpoint}?{(includeClosed ? "state=all&" : "")}per_page=100&page={page}")).AsArray();
            foreach (var row in rows) all.Add(row?.DeepClone());
            if (rows.Count < 100) return all;
        }
        throw new ProtocolException("GITHUB_READ_LIMIT", "The complete GitHub record exceeds the supported read limit.", "Review this target directly on GitHub; Pulse cannot safely submit or verify an incomplete record.");
    }

    private static string StringValue(JsonObject row, string name) => row[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    private static bool ValidAccount(string login) => Regex.IsMatch(login, @"\A[A-Za-z0-9][A-Za-z0-9_-]{0,99}\z");
    private static JsonObject AuthError() => new() { ["code"] = "GITHUB_AUTH_REQUIRED", ["message"] = "No valid saved GitHub CLI account is selected.", ["guidance"] = "Run gh auth login for github.com, then detect and select an account in settings." };
    private static ProtocolException AuthenticationRequired() => new("GITHUB_AUTH_REQUIRED", "No valid saved GitHub CLI account is selected.", "Run gh auth login for github.com, then detect and select an account in settings.");
    private static JsonObject AccountsUnavailable(string code, string message, string guidance) => new()
    {
        ["available"] = false, ["accounts"] = new JsonArray(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message, ["guidance"] = guidance }
    };
    private static GitHubRequestException UnknownResult(bool writing) => new(0, writing,
        writing ? "The GitHub submission result is unknown." : "GitHub could not be read.",
        writing ? "Check the recorded operation against GitHub before trying another submission." : "Check GitHub CLI, the network, and GitHub availability, then refresh.");
    private static string ErrorFor(int code) => code switch
    {
        401 => "The GitHub authentication is invalid or expired.",
        403 => "GitHub denied this operation or has temporarily limited requests.",
        404 => "The GitHub target is unavailable to this account.",
        409 => "The GitHub target changed or conflicts with this operation.",
        422 => "GitHub rejected the operation's state, body, or review location.",
        429 => "GitHub has temporarily limited requests.",
        >= 300 and < 400 => "GitHub returned a redirect response.",
        _ => $"GitHub returned HTTP {code}."
    };
    private static string GuidanceFor(int code) => code switch
    {
        401 => "Sign in again with gh auth login and refresh the preview.",
        403 or 404 => "Check the selected account, repository access, token permissions, organization SSO, and GitHub rate limits.",
        409 or 422 => "Refresh the target and re-analyze if the PR HEAD or diff changed. GitHub may prohibit self-review or the selected operation.",
        _ => "Open the saved target on GitHub and refresh its current state before continuing."
    };
    public void Dispose() { token = null; testClient?.Dispose(); }
}

public sealed class GitHubRequestException(int statusCode, bool uncertain, string message, string guidance) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public bool Uncertain { get; } = uncertain;
    public string Guidance { get; } = guidance;
}
