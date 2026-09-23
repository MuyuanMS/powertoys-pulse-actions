using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pulse.Host;

/// <summary>Observes runtime-selected parameters; never infers them from requested configuration or assistant text.</summary>
internal sealed partial class RunExecutionMetadata
{
    private const int ReadBudget = 256 * 1024;
    private const int MaximumLineBytes = 256 * 1024;
    private const long MaximumSessionBytes = 64L * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] Efforts = ["none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra"];
    private readonly string agent;
    private readonly string? codexHome;
    private readonly DateTimeOffset startedAt;
    private readonly object gate = new();
    private readonly List<byte> line = new();
    private volatile string? sessionId;
    private string? sessionPath;
    private JsonObject? observation;
    private bool verifiedSession, overlong, stopped;
    private long offset;

    public RunExecutionMetadata(string agent, DateTimeOffset startedAt, string? codexHome = null)
    {
        this.agent = agent;
        this.startedAt = startedAt;
        this.codexHome = codexHome ?? (Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } configured
            ? configured : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));
    }

    // Called by the stdout pump: parsing only, no filesystem lookup and no status-lock acquisition.
    public void ObserveOutput(string text)
    {
        if (text.Length > MaximumLineBytes) return;
        lock (gate)
        {
            try
            {
                using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;
                if (agent == "codex")
                {
                    if (Text(root, "type") != "thread.started") return;
                    var id = Text(root, "thread_id");
                    if (Guid.TryParseExact(id, "D", out var parsed) && sessionId is null) sessionId = parsed.ToString("D");
                    return;
                }
                if (agent != "copilot" || Text(root, "agentId") is { Length: > 0 } || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return;
                var type = Text(root, "type");
                // GitHub's generated session-events schema defines these root session fields:
                // https://github.com/github/copilot-sdk/blob/main/nodejs/src/generated/session-events.ts
                // Subagent, assistant message, tool-result, provider URL, and arbitrary model-looking fields are ignored.
                if (type == "session.start")
                {
                    var id = Text(data, "sessionId");
                    if (!Guid.TryParseExact(id, "D", out var parsed) || sessionId is not null && sessionId != parsed.ToString("D")) return;
                    sessionId = parsed.ToString("D");
                    SetObservation(Text(data, "selectedModel"), Text(data, "reasoningEffort"), "cli-event");
                }
                else if (sessionId is not null && type is "assistant.usage" or "session.model_change")
                {
                    var eventSession = Text(data, "sessionId");
                    if (eventSession is not null && eventSession != sessionId) return;
                    SetObservation(Text(data, type == "assistant.usage" ? "model" : "newModel"), Text(data, "reasoningEffort"), "cli-event");
                }
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException) { }
        }
    }

    /// <summary>Reads only the run's exact session file, incrementally and with a fixed per-poll byte budget.</summary>
    public JsonObject? ReadSnapshot()
    {
        // The worker owns filesystem state. Do not hold the stdout observer gate during disk reads.
        if (agent == "codex" && sessionId is not null && !stopped)
        {
            try
            {
                sessionPath ??= FindSessionPath();
                if (sessionPath is not null) ReadSession();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        lock (gate) return observation?.DeepClone().AsObject();
    }

    private string? FindSessionPath()
    {
        if (codexHome is null || !Path.IsPathFullyQualified(codexHome) || !Directory.Exists(codexHome) || Reparse(codexHome)) return null;
        var sessions = Path.Combine(codexHome, "sessions");
        if (!Directory.Exists(sessions) || Reparse(sessions)) return null;
        // Codex uses a local-date folder. Check only launch-adjacent local/UTC dates, never enumerate user history.
        var days = new[] { startedAt.LocalDateTime.Date, startedAt.UtcDateTime.Date }
            .SelectMany(date => new[] { date.AddDays(-1), date, date.AddDays(1) }).Distinct();
        string? found = null;
        foreach (var day in days)
        {
            var year = Path.Combine(sessions, day.ToString("yyyy"));
            var month = Path.Combine(year, day.ToString("MM"));
            var directory = Path.Combine(month, day.ToString("dd"));
            if (!Directory.Exists(directory) || Reparse(year) || Reparse(month) || Reparse(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "rollout-*-" + sessionId + ".jsonl", SearchOption.TopDirectoryOnly).Take(2))
            {
                if (Reparse(path) || found is not null) { stopped = true; return null; }
                found = path;
            }
        }
        return found;
    }

    private void ReadSession()
    {
        if (Reparse(sessionPath!)) { stopped = true; return; }
        using var file = new FileStream(sessionPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length < offset || offset >= MaximumSessionBytes) { stopped = true; return; }
        file.Position = offset;
        var remaining = (int)Math.Min(ReadBudget, MaximumSessionBytes - offset);
        var buffer = new byte[8192];
        while (remaining > 0)
        {
            var count = file.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (count == 0) break;
            offset += count; remaining -= count;
            for (var index = 0; index < count; index++)
            {
                if (buffer[index] != (byte)'\n')
                {
                    if (line.Count < MaximumLineBytes) line.Add(buffer[index]); else overlong = true;
                    continue;
                }
                if (!overlong) ConsumeSessionLine(line.ToArray());
                line.Clear(); overlong = false;
                if (stopped) return;
            }
        }
    }

    private void ConsumeSessionLine(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(StrictUtf8.GetString(bytes), new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return;
            if (Text(root, "type") == "session_meta")
            {
                var id = Text(payload, "id");
                if (id != sessionId) { stopped = true; return; }
                verifiedSession = true;
            }
            else if (verifiedSession && Text(root, "type") == "turn_context")
                SetObservation(Text(payload, "model"), Text(payload, "effort"), "codex-turn-context");
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidOperationException or ArgumentException) { }
    }

    private void SetObservation(string? model, string? effort, string source)
    {
        model = model is not null && ModelIdentifier().IsMatch(model) && model is not ("auto" or "default") ? model : null;
        effort = effort is not null && Efforts.Contains(effort, StringComparer.Ordinal) ? effort : null;
        lock (gate)
        {
            if (model is null && effort is null) { observation = null; return; }
            // A new runtime turn/event replaces the previous snapshot. Missing fields must not inherit another model's settings.
            if (observation?["model"]?.GetValue<string>() == model && observation?["reasoningEffort"]?.GetValue<string>() == effort && observation?["source"]?.GetValue<string>() == source) return;
            observation = new JsonObject { ["source"] = source, ["observedAt"] = DateTimeOffset.UtcNow.ToString("O") };
            if (model is not null) observation["model"] = model;
            if (effort is not null) observation["reasoningEffort"] = effort;
        }
    }

    private static bool Reparse(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ModelIdentifier();
}
