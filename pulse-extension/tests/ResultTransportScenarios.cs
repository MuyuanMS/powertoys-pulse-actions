using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pulse.Host.Tests;

/// <summary>CLI event transport without processes, model calls or external repositories.</summary>
internal static class ResultTransportScenarios
{
    internal static async Task RunAllAsync()
    {
        RecognizedFinalMessagesNeedNoAdditionalMarker();
        ExplicitResponsePhasesAreRespected();
        ToolAndProgressOutputCannotBecomeResults();
        LaterInvalidResponsesDiscardEarlierResults();
        NewResponsesDiscardEarlierResults();
        using var fixture = new Fixture();
        foreach (var agent in new[] { "codex", "copilot" })
        {
            await LargeEscapedFinalSurvivesFragmentation(fixture, agent);
            await LiteralUnicodeSurvivesByteFragments(fixture, agent);
            await OversizedTransportDiscardsTheStaleFinal(fixture, agent);
        }
        Console.WriteLine("PASS result transport: recognized finals only, stale-final invalidation, fragmented escaped large results, complete raw logs and bounded oversized-event diagnostics (offline)");
    }

    private static void RecognizedFinalMessagesNeedNoAdditionalMarker()
    {
        var model = Model().ToJsonString();
        foreach (var agent in new[] { "codex", "copilot" })
        foreach (var final in ValidFinals(agent, model))
        {
            var adapter = new CliAdapter(agent);
            adapter.Consume(final);
            var result = Result(adapter);
            Completed(result, "A complete recognized assistant response with exit zero needs no separate terminal event.");
            Check(adapter.AssistantReply is { Length: > 0 } && adapter.OutputIsComplete,
                "Recognized final text must be retained without inventing output loss.");
        }
    }

    private static void ToolAndProgressOutputCannotBecomeResults()
    {
        var output = Model().ToJsonString();
        foreach (var agent in new[] { "codex", "copilot" })
        {
            string[] events = agent == "codex"
                ? [output, Event(new JsonObject { ["type"] = "item.completed", ["item"] = new JsonObject { ["type"] = "command_execution", ["aggregated_output"] = output } }),
                    Event(new JsonObject { ["type"] = "item.updated", ["item"] = new JsonObject { ["type"] = "agent_message", ["text"] = output } })]
                : [output, Event(new JsonObject { ["type"] = "tool.result", ["data"] = new JsonObject { ["content"] = output } }),
                    Event(new JsonObject { ["type"] = "assistant.message_delta", ["data"] = new JsonObject { ["deltaContent"] = output } })];
            foreach (var message in events)
            {
                var adapter = new CliAdapter(agent);
                adapter.Consume(message); adapter.Consume(Terminal(agent));
                Invalid(Result(adapter), "Raw, tool or progress JSON must not become a final result merely because a terminal marker followed it.");
                Check(adapter.AssistantReply is null, "Unrecognized progress text cannot satisfy the assistant-response contract.");
            }
        }
    }

    private static void ExplicitResponsePhasesAreRespected()
    {
        var model = Model().ToJsonString();
        foreach (var agent in new[] { "codex", "copilot" })
        foreach (var phase in new[] { "final", "final_answer", "commentary", "analysis" })
        {
            var adapter = new CliAdapter(agent);
            adapter.Consume(Final(agent, JsonValue.Create(model)));
            var message = agent == "codex"
                ? new JsonObject { ["type"] = "item.completed", ["item"] = new JsonObject { ["type"] = "agent_message", ["phase"] = phase, ["text"] = model } }
                : new JsonObject { ["type"] = "assistant.message", ["data"] = new JsonObject { ["phase"] = phase, ["content"] = model } };
            adapter.Consume(Event(message));
            if (phase is "final" or "final_answer")
                Completed(Result(adapter), "Explicit final phases must qualify without a separate terminal marker.");
            else
            {
                adapter.Consume(Terminal(agent));
                Invalid(Result(adapter), "Analysis and commentary phases must clear an earlier final without becoming workflow results.");
            }
        }
    }

    private static void LaterInvalidResponsesDiscardEarlierResults()
    {
        var valid = Model().ToJsonString();
        foreach (var agent in new[] { "codex", "copilot" })
        {
            foreach (var payload in new JsonNode?[] { null, JsonValue.Create(""), JsonValue.Create(" \n\t"), JsonValue.Create(4), new JsonObject(),
                new JsonArray("wrong shape"), JsonValue.Create("{invalid model JSON"), JsonValue.Create(new string('x', ResultLimits.MaximumFinalTextCharacters + 1)) })
            {
                var adapter = new CliAdapter(agent);
                adapter.Consume(Final(agent, JsonValue.Create(valid)));
                adapter.Consume(Final(agent, payload)); adapter.Consume(Terminal(agent));
                Invalid(Result(adapter), "A later empty, wrong-type, malformed or oversized final must never reuse earlier successful result text.");
            }
            var malformedEvent = new CliAdapter(agent);
            malformedEvent.Consume(Final(agent, JsonValue.Create(valid)));
            malformedEvent.Consume(agent == "codex" ? "{\"type\":\"item.completed\",\"item\":" : "{\"type\":\"result\",\"result\":");
            Invalid(Result(malformedEvent), "A malformed subsequent JSON event cannot leave a stale success eligible.");
        }
        foreach (var later in new[]
        {
            Event(new JsonObject { ["type"] = "assistant.message", ["data"] = new JsonObject { ["content"] = new JsonObject(), ["deltaContent"] = valid, ["message"] = valid } }),
            Event(new JsonObject { ["type"] = "assistant", ["message"] = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(TextBlock(valid)) } }),
            Event(new JsonObject { ["type"] = "assistant", ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(TextBlock(valid), new JsonObject { ["type"] = "tool_use", ["name"] = "fixture" }) } }),
            Event(new JsonObject { ["type"] = "assistant", ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray() } })
        })
        {
            var adapter = new CliAdapter("copilot");
            adapter.Consume(Final("copilot", JsonValue.Create(valid))); adapter.Consume(later);
            Invalid(Result(adapter), "Copilot final content must be a string or an assistant message containing only complete text blocks.");
        }
    }

    private static void NewResponsesDiscardEarlierResults()
    {
        var model = Model().ToJsonString();
        foreach (var agent in new[] { "codex", "copilot" })
        {
            string[] starts = agent == "codex"
                ? [Event(new JsonObject { ["type"] = "thread.started", ["thread_id"] = "new-session" }), "{\"type\":\"turn.started\"}",
                    Event(new JsonObject { ["type"] = "item.started", ["item"] = new JsonObject { ["type"] = "agent_message" } }),
                    Event(new JsonObject { ["type"] = "item.updated", ["item"] = new JsonObject { ["type"] = "agent_message", ["text"] = "Unfinished next answer." } })]
                : [Event(new JsonObject { ["type"] = "session.start", ["data"] = new JsonObject { ["sessionId"] = "new-session" } }),
                    "{\"type\":\"system\"}", "{\"type\":\"assistant.turn_start\"}", "{\"type\":\"assistant.message_start\"}",
                    Event(new JsonObject { ["type"] = "assistant.message_delta", ["data"] = new JsonObject { ["deltaContent"] = "Unfinished next answer." } })];
            foreach (var start in starts)
            {
                var adapter = new CliAdapter(agent);
                adapter.Consume(Final(agent, JsonValue.Create(model))); adapter.Consume(start); adapter.Consume(Terminal(agent));
                Invalid(Result(adapter), "A new session, turn or assistant response must invalidate an earlier final until another final arrives.");
                Check(adapter.AssistantReply is null, "Starting another response must clear the previous assistant reply.");
                adapter.Consume(Final(agent, JsonValue.Create(model)));
                Completed(Result(adapter), "A complete new final may satisfy the new response after ordinary start/progress events.");
            }
        }
    }

    private static async Task LargeEscapedFinalSurvivesFragmentation(Fixture fixture, string agent)
    {
        var model = LargeModel();
        var text = model.ToJsonString();
        var modelBytes = Encoding.UTF8.GetByteCount(text);
        Check(modelBytes > ResultLimits.MaximumModelBytes - 1024 && modelBytes <= ResultLimits.MaximumModelBytes,
            "The large fixture must exercise the accepted model-byte boundary.");
        // Force the valid worst-case string encoding rather than relying on one JSON encoder's choices.
        var quoted = new StringBuilder(text.Length * 6 + 2).Append('"');
        foreach (var character in text) quoted.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
        quoted.Append('"');
        var line = agent == "codex"
            ? "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":" + quoted + "}}"
            : "{\"type\":\"result\",\"subtype\":\"success\",\"result\":" + quoted + "}";
        Check(line.Length > 5 * ResultLimits.MaximumModelBytes && line.Length <= ResultLimits.MaximumCliEventCharacters,
            "Escaped valid final events must exercise the derived transport budget rather than the old 256 KiB line limit.");
        var (id, task) = fixture.Create(agent);
        var adapter = new CliAdapter(agent);
        await using var stream = new FragmentedStream(Encoding.UTF8.GetBytes(line), 17);
        using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 128, leaveOpen: true))
            await RuntimeService.DrainOutputAsync(fixture.Store, id, reader, "stdout.jsonl", true, adapter, new StringBuilder(), CancellationToken.None);
        Check(stream.ReadCalls > 1000, "The real StreamReader must assemble the final event from tiny byte fragments.");
        var raw = await File.ReadAllTextAsync(Path.Combine(fixture.Store.RunDirectory(id), "stdout.jsonl"));
        Check(raw == line + "\n" && !raw.Contains("OUTPUT_LINE_TOO_LONG", StringComparison.Ordinal) && adapter.OutputIsComplete,
            "The complete escaped event, including a final line without LF, must survive in the raw log.");
        var result = Result(adapter, task);
        Completed(result, "A valid near-limit model result must remain completed after transport and Host compatibility projections.");
        var normalizedBytes = Encoding.UTF8.GetByteCount(result.ToJsonString());
        Check(normalizedBytes > ResultLimits.MaximumModelBytes && normalizedBytes <= ResultLimits.MaximumNormalizedResultBytes,
            "Host projections and proposal metadata have their own normalized budget beyond model JSON size.");
        Check(JsonNode.DeepEquals(result["nextActions"], result["nextSteps"]) && result["nextActions"]!.AsArray().Count == model["nextActions"]!.AsArray().Count,
            "Compatibility projections must preserve all valid proposals despite the large result.");
        var proposalIds = result["nextActions"]!.AsArray().OfType<JsonObject>().Select(action => action["proposalId"]?.GetValue<string>()).ToArray();
        Check(proposalIds.All(id => !string.IsNullOrWhiteSpace(id)) && proposalIds.Distinct(StringComparer.Ordinal).Count() == proposalIds.Length,
            "Host proposal identities must fit the normalized budget without dropping or conflating valid near-limit actions.");
        for (var index = 0; index < model["nextActions"]!.AsArray().Count; index++)
            Check(result["nextActions"]![index]!["body"]!.GetValue<string>() == model["nextActions"]![index]!["body"]!.GetValue<string>(),
                "Quoted and Unicode proposal bodies must survive escaping, fragmentation and normalization exactly.");
        fixture.Store.Complete(id, "succeeded", 0, result);
        Completed(fixture.Store.ReadResult(id)!, "Persisting the normalized result must preserve workflow completion and structured data.");
    }

    private static async Task OversizedTransportDiscardsTheStaleFinal(Fixture fixture, string agent)
    {
        var (id, task) = fixture.Create(agent);
        var first = Final(agent, JsonValue.Create(Model().ToJsonString()));
        var oversized = Final(agent, JsonValue.Create(new string('x', ResultLimits.MaximumCliEventCharacters + 1)));
        var wire = first + "\n" + oversized + "\n" + Terminal(agent) + "\n";
        var adapter = new CliAdapter(agent);
        await using var stream = new FragmentedStream(Encoding.UTF8.GetBytes(wire), 31);
        using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 128, leaveOpen: true))
            await RuntimeService.DrainOutputAsync(fixture.Store, id, reader, "stdout.jsonl", true, adapter, new StringBuilder(), CancellationToken.None);
        var raw = await File.ReadAllTextAsync(Path.Combine(fixture.Store.RunDirectory(id), "stdout.jsonl"));
        Check(raw.StartsWith(first + "\n", StringComparison.Ordinal) && raw.Contains("OUTPUT_LINE_TOO_LONG", StringComparison.Ordinal) && raw.Length < first.Length + 2048,
            "Oversized transport must retain the preceding log and one bounded discard marker instead of writing an oversized partial event.");
        Check(!adapter.OutputIsComplete && adapter.AssistantReply is null, "Discarding a later oversized event must invalidate the earlier final and mark output loss.");
        Invalid(Result(adapter, task), "The earlier valid final cannot survive a later discarded oversized final event.");
    }

    private static async Task LiteralUnicodeSurvivesByteFragments(Fixture fixture, string agent)
    {
        var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var model = Model();
        model["summary"] = string.Concat(Enumerable.Repeat("中文😃", 5000));
        var text = model.ToJsonString(options);
        var envelope = agent == "codex"
            ? new JsonObject { ["type"] = "item.completed", ["item"] = new JsonObject { ["type"] = "agent_message", ["text"] = text } }
            : new JsonObject { ["type"] = "result", ["subtype"] = "success", ["result"] = text };
        var line = envelope.ToJsonString(options);
        Check(line.Contains("中文", StringComparison.Ordinal) && Encoding.UTF8.GetByteCount(line) > line.Length,
            "The Unicode fixture must include literal multibyte UTF-8 rather than testing only JSON escape sequences.");
        var (id, task) = fixture.Create(agent);
        var adapter = new CliAdapter(agent);
        await using var stream = new FragmentedStream(Encoding.UTF8.GetBytes(line), 1);
        using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 128, leaveOpen: true))
            await RuntimeService.DrainOutputAsync(fixture.Store, id, reader, "stdout.jsonl", true, adapter, new StringBuilder(), CancellationToken.None);
        var result = Result(adapter, task);
        Completed(result, "Literal UTF-8 fragmented across individual bytes must still produce a complete workflow result.");
        Check(result["summary"]!.GetValue<string>() == model["summary"]!.GetValue<string>() &&
            (await File.ReadAllBytesAsync(Path.Combine(fixture.Store.RunDirectory(id), "stdout.jsonl"))).SequenceEqual(Encoding.UTF8.GetBytes(line + "\n")),
            "Surrogate pairs, multibyte characters and the full raw log must round-trip without replacement or lost bytes.");
    }

    private static JsonObject LargeModel()
    {
        var model = Model();
        var actions = new JsonArray();
        for (var index = 0; index < 10; index++) actions.Add(new JsonObject
        {
            ["kind"] = "comment", ["reason"] = "Recorded proposal " + index,
            ["body"] = "Proposal " + index + ": \"quoted change\" 中文 😃 <tag> \\source\n" + new string('x', 32000)
        });
        model["nextActions"] = actions;
        var remaining = ResultLimits.MaximumModelBytes - 256 - Encoding.UTF8.GetByteCount(model.ToJsonString());
        Check(remaining > 0 && remaining < 30000, "Large-model padding must stay inside the summary and total model bounds.");
        model["summary"] = model["summary"]!.GetValue<string>() + new string('s', remaining);
        return model;
    }

    private static JsonObject Model() => new()
    {
        ["schemaVersion"] = 2, ["outcome"] = "completed", ["phase"] = "reporting", ["summary"] = "Local transport fixture completed.",
        ["findings"] = new JsonArray(), ["artifacts"] = new JsonArray(),
        ["validation"] = new JsonArray(new[] { "context", "local-review", "verification" }.Select(id => (JsonNode?)new JsonObject
        {
            ["id"] = id, ["name"] = id, ["status"] = "passed", ["required"] = true,
            ["details"] = "This isolated fixture records the required check.", ["evidence"] = new JsonArray("Controlled transport fixture evidence.")
        }).ToArray()),
        ["diagnostics"] = new JsonArray(), ["nextActions"] = new JsonArray(new JsonObject { ["kind"] = "comment", ["reason"] = "Inspect the recorded local evidence.", ["body"] = "Fixture comment draft." }),
        ["review"] = null, ["needsReview"] = false
    };
    private static JsonObject TaskContext(string? id = null) => Protocol.ValidateTask(new JsonObject
    {
        ["requestId"] = id ?? Guid.NewGuid().ToString("D"), ["actionId"] = "result-transport-fixture", ["actionKind"] = "pr-review",
        ["repository"] = Configuration.PowerToysRepository, ["prompt"] = "Verify this isolated offline transport fixture.",
        ["target"] = new JsonObject { ["type"] = "pr", ["number"] = 42 }, ["expectedHeadSha"] = new string('a', 40)
    });
    private static IEnumerable<string> ValidFinals(string agent, string model)
    {
        yield return Final(agent, JsonValue.Create(model));
        if (agent != "copilot") yield break;
        yield return Event(new JsonObject { ["type"] = "assistant.message", ["data"] = new JsonObject { ["content"] = model } });
        var split = model.IndexOf(',') + 1;
        yield return Event(new JsonObject { ["type"] = "assistant", ["message"] = new JsonObject
            { ["role"] = "assistant", ["content"] = new JsonArray(TextBlock(model[..split]), TextBlock(model[split..])) } });
    }
    private static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };
    private static string Final(string agent, JsonNode? payload) => agent == "codex"
        ? Event(new JsonObject { ["type"] = "item.completed", ["item"] = new JsonObject { ["type"] = "agent_message", ["text"] = payload?.DeepClone() } })
        : Event(new JsonObject { ["type"] = "result", ["subtype"] = "success", ["result"] = payload?.DeepClone() });
    private static string Terminal(string agent) => agent == "codex" ? "{\"type\":\"turn.completed\"}" : "{\"type\":\"session.idle\"}";
    private static string Event(JsonObject value) => value.ToJsonString();
    private static JsonObject Result(CliAdapter adapter, JsonObject? task = null) => adapter.Result("succeeded", 0, null, null, task ?? TaskContext(), expectedSchemaVersion: 2);
    private static void Completed(JsonObject result, string message) => Check(result["outcome"]?.GetValue<string>() == "completed" && result["structured"]?.GetValue<bool>() == true && result["cliExitCode"]?.GetValue<int>() == 0, message);
    private static void Invalid(JsonObject result, string message) => Check(result["outcome"]?.GetValue<string>() == "blocked" && result["structured"]?.GetValue<bool>() == true &&
        result["diagnostics"]!.AsArray().OfType<JsonObject>().Any(row => row["code"]?.GetValue<string>() == "INVALID_RESULT") &&
        result["nextActions"]!.AsArray().OfType<JsonObject>().All(row => row["kind"]?.GetValue<string>() is not ("approve" or "suggestChanges" or "requestChanges" or "comment" or "close")), message);
    private static void Check(bool condition, string message) => CoreScenarios.Check(condition, message);

    private sealed class FragmentedStream(byte[] bytes, int maximumRead) : MemoryStream(bytes, writable: false)
    {
        public int ReadCalls { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); ReadCalls++;
            return ValueTask.FromResult(base.Read(buffer.Span[..Math.Min(buffer.Length, maximumRead)]));
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        private readonly string root;
        public Store Store { get; }
        public Fixture() { root = Path.Combine(temporary, "PulseResultTransport-" + Guid.NewGuid().ToString("N")); Store = new Store(root); }
        public (string Id, JsonObject Task) Create(string agent)
        {
            var id = Guid.NewGuid().ToString("D"); var task = TaskContext(id);
            Store.CreateRun(id, task, new JsonObject { ["agent"] = agent, ["cliPath"] = Path.Combine(root, agent + ".exe"), ["repoFolder"] = root,
                ["repositoryKey"] = id, ["permission"] = "read-only", ["githubAccount"] = "" }, Protocol.ProductionOrigin,
                new JsonObject { ["schemaVersion"] = 2, ["name"] = "transport-fixture", ["body"] = "Offline fixture", ["sha"] = Protocol.Hash("Offline fixture") });
            Check(Store.ReadStatus(id)["state"]?.GetValue<string>() == "accepted", "The transport fixture must use a real accepted Store record without launching a worker.");
            return (id, task);
        }
        public void Dispose()
        {
            var absolute = Path.GetFullPath(root);
            Check(string.Equals(Path.GetDirectoryName(absolute), temporary, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(absolute).StartsWith("PulseResultTransport-", StringComparison.Ordinal),
                "Transport cleanup must remain in its dedicated direct child of temp.");
            if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
            Check(!Directory.Exists(absolute), "Transport fixture cleanup must remove every accepted record and saved log.");
        }
    }
}
