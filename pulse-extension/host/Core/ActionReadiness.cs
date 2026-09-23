using System.Text.Json.Nodes;

namespace Pulse.Host;

/// <summary>Read-only, public-safe checks before a webpage submits a local action.</summary>
public sealed class ActionReadiness(Store store,
    Func<string, Task<JsonObject>>? probeAgent = null,
    Func<Task<JsonObject>>? getAccounts = null,
    Func<JsonObject, Task>? checkFolders = null)
{
    public async Task<JsonObject> CheckAsync(JsonObject payload)
    {
        Protocol.OnlyKeys(payload, "actionKind", "execution", "reviewOptions");
        if (payload.ContainsKey("execution")) ExecutionOptions.ValidateOverride(payload["execution"]);
        var kind = Protocol.RequiredString(payload, "actionKind", 40);
        if (!Dispatcher.PublicWorkflowKinds.Contains(kind, StringComparer.Ordinal)) throw new ProtocolException("INVALID_REQUEST", "This workflow starts from a saved result or is unsupported.");
        ReviewModes.Validate(payload);
        var (_, targetType) = TaskPrompt.SelectionForAction(kind);
        var blockers = new JsonArray();
        JsonObject config;
        try { config = new Configuration(store).Read(); }
        catch (Exception error) when (IsCheckFailure(error))
        {
            blockers.Add(Blocker("INVALID_CONFIG"));
            return Result(kind, blockers, payload);
        }

        await CheckAsync(async () =>
        {
            if (checkFolders is null) await Configuration.ResolveExecutionFoldersAsync(config);
            else await checkFolders(config);
        }, "REPO_NOT_CONFIGURED");

        await CheckAsync(async () =>
        {
            var execution = ExecutionOptions.Resolve(config, payload["execution"]);
            var agent = execution["agent"]!.GetValue<string>();
            var probe = await CliSelections.ProbeSelectedAsync(config, agent,
                probeAgent is null ? null : (selectedAgent, _) => probeAgent(selectedAgent));
            if (probe["available"]?.GetValue<bool>() != true)
                throw new ProtocolException(probe["error"]?["code"]?.GetValue<string>() ?? "CLI_UNAVAILABLE", "The configured CLI is unavailable.");
        }, "CLI_UNAVAILABLE");

        await CheckAsync(() =>
        {
            TaskPrompt.ValidateActionSelection(new PromptCatalog(store), config, kind);
            return Task.CompletedTask;
        }, "PROMPT_NOT_CONFIGURED");

        if (targetType == "pr")
            await CheckAsync(async () =>
            {
                var selected = config["githubAccount"]?.GetValue<string>() ?? "";
                if (selected.Length == 0) throw new ProtocolException("GITHUB_AUTH_REQUIRED", "Select a local GitHub CLI account.");
                var accounts = getAccounts is null ? await GitHubSession.GetAccountsAsync() : await getAccounts();
                if (accounts["accounts"] is not JsonArray rows || !rows.OfType<JsonObject>().Any(row =>
                    string.Equals(row["login"]?.GetValue<string>(), selected, StringComparison.OrdinalIgnoreCase) && row["state"]?.GetValue<string>() == "success"))
                    throw new ProtocolException("GITHUB_AUTH_REQUIRED", "The selected GitHub CLI account is unavailable.");
            }, "GITHUB_AUTH_REQUIRED");

        return Result(kind, blockers, payload);

        async Task CheckAsync(Func<Task> check, string fallbackCode)
        {
            try { await check(); }
            catch (Exception error) when (IsCheckFailure(error))
            {
                var code = error is ProtocolException known ? known.Code : fallbackCode;
                blockers.Add(Blocker(code));
            }
        }
    }

    private static JsonObject Result(string kind, JsonArray blockers, JsonObject request)
    {
        var result = new JsonObject
        {
            ["actionKind"] = kind, ["ready"] = blockers.Count == 0, ["blockers"] = blockers,
            ["reviewModes"] = new JsonArray(ReviewModes.Values.Select(mode => (JsonNode?)JsonValue.Create(mode)).ToArray()),
            ["workflowKinds"] = new JsonArray(Dispatcher.PublicWorkflowKinds.Select(kind => (JsonNode?)JsonValue.Create(kind)).ToArray()),
            ["resultSchemaVersions"] = new JsonArray(2, 3)
        };
        if (request["reviewOptions"] is not null) result["reviewOptions"] = request["reviewOptions"]!.DeepClone();
        return result;
    }

    private static bool IsCheckFailure(Exception error) => error is ProtocolException or IOException or UnauthorizedAccessException
        or InvalidOperationException or ArgumentException or FormatException or System.Text.Json.JsonException;

    // Do not relay local paths, account names, CLI output, prompt contents, or exception messages to a webpage.
    private static JsonObject Blocker(string code)
    {
        var (publicCode, message, guidance) = code switch
        {
            "REPO_NOT_CONFIGURED" => (code, "The PowerToys checkout and worktree root are not configured.", "Open extension Settings and select both local folders."),
            "REPO_PATH_INVALID" or "REPOSITORY_MISMATCH" or "MAIN_REPO_REQUIRED" or "WORKTREE_PATH_INVALID" or "GIT_OPERATION_FAILED" or "GIT_MISSING"
                => (code, "The configured repository folders could not be verified.", "Open extension Settings and check Git, the main PowerToys checkout, and the separate worktree root."),
            "CLI_UNAVAILABLE" or "CLI_NOT_FOUND" or "CLI_NOT_SUPPORTED" or "CLI_VERSION_UNSUPPORTED" or "CLI_PROBE_FAILED" or "CLI_CAPABILITY_UNSUPPORTED" or "CLI_INSTALLATION_INCOMPLETE"
                => (code, "The selected CLI could not be started.", "Inspect the actual CLI output and choose an existing executable in extension Settings."),
            "CLI_SELECTION_REQUIRED" => (code, "Choose a CLI installation for this agent before running the action.", "Open extension Settings and select an available detected CLI installation."),
            "CLI_SELECTION_UNAVAILABLE" or "INVALID_CLI_SELECTION" => (code, "The selected CLI executable is missing or its path is invalid.", "Open extension Settings, refresh detected installations, and choose an existing executable."),
            "INVALID_EXECUTION" => (code, "The selected model or reasoning effort is invalid for this CLI.", "Review the default model settings or this task's execution overrides."),
            "PROMPT_NOT_CONFIGURED" or "PROMPT_NOT_FOUND" or "PROMPT_TARGET_MISMATCH" or "PROMPT_ACTION_MISMATCH" or "UNSUPPORTED_PROMPT_VARIABLE" or "INVALID_PROMPT_NAME" or "PROMPT_CATALOG_INVALID"
                => (code, "The bundled prompt selected for this action is unavailable or incompatible.", "Open extension Settings and select a bundled template for this action."),
            "PROMPT_BUNDLE_INVALID" => (code, "The Host's bundled prompts could not be loaded.", "Update or repair the local Host, then reload prompts in extension Settings."),
            "GITHUB_AUTH_REQUIRED" or "GITHUB_CLI_UNAVAILABLE" or "GITHUB_ACCOUNT_UNAVAILABLE"
                => (code, "A signed-in GitHub CLI account is required to verify the pull request revision.", "Open extension Settings, refresh GitHub accounts, and save an available account."),
            _ => ("INVALID_CONFIG", "The local action configuration could not be verified.", "Open extension Settings and review the saved configuration.")
        };
        return new ProtocolException(publicCode, message, guidance).ToJson();
    }
}
