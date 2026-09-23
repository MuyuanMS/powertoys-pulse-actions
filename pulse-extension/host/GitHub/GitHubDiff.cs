using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pulse.Host;

/// <summary>Only complete, provable RIGHT-side diff hunks can receive suggestions.</summary>
public static class GitHubDiff
{
    public static JsonArray ParseLines(string? patch)
    {
        var result = new JsonArray();
        if (string.IsNullOrEmpty(patch)) return result;
        var hunk = 0;
        var current = new List<JsonObject>();
        var oldRemaining = 0;
        var newRemaining = 0;
        var rightLine = 0;
        var valid = false;
        void Finish()
        {
            if (valid && oldRemaining == 0 && newRemaining == 0)
                foreach (var line in current) result.Add(line);
            current.Clear();
        }
        foreach (var raw in patch.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("@@"))
            {
                Finish();
                hunk++;
                var match = Regex.Match(raw, @"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@(?:.*)$");
                valid = match.Success &&
                    int.TryParse(match.Groups[3].Value, out rightLine) &&
                    int.TryParse(match.Groups[2].Success ? match.Groups[2].Value : "1", out oldRemaining) &&
                    int.TryParse(match.Groups[4].Success ? match.Groups[4].Value : "1", out newRemaining);
                continue;
            }
            if (!valid || raw.StartsWith("\\ No newline at end of file")) continue;
            // Split's trailing empty item is not a diff line. Blank source lines carry a prefix.
            if (raw.Length == 0)
            {
                if (oldRemaining != 0 || newRemaining != 0) valid = false;
                continue;
            }
            switch (raw[0])
            {
                case ' ':
                case '+':
                    if (raw[0] == ' ') oldRemaining--;
                    newRemaining--;
                    if (rightLine <= 0 || rightLine == int.MaxValue) { valid = false; break; }
                    current.Add(new JsonObject { ["line"] = rightLine++, ["kind"] = raw[0] == '+' ? "add" : "context", ["original"] = raw[1..], ["hunk"] = hunk });
                    break;
                case '-': oldRemaining--; break;
                default: valid = false; break;
            }
            if (oldRemaining < 0 || newRemaining < 0) valid = false;
        }
        Finish();
        return result;
    }

    public static string ValidateSuggestion(JsonObject suggestion, JsonArray files)
    {
        var path = suggestion["path"]!.GetValue<string>();
        var last = suggestion["line"]!.GetValue<int>();
        var first = suggestion["startLine"]?.GetValue<int>() ?? last;
        var file = files.OfType<JsonObject>().SingleOrDefault(f => f["path"]?.GetValue<string>() == path);
        if (file is null || file["status"]?.GetValue<string>() == "removed") throw InvalidLocation();
        var lines = (file["lines"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(row => row["line"]!.GetValue<int>() >= first && row["line"]!.GetValue<int>() <= last).ToList();
        if (lines.Count != last - first + 1 || lines.Select(row => row["hunk"]!.GetValue<int>()).Distinct().Count() != 1 ||
            lines.Select(row => row["line"]!.GetValue<int>()).Distinct().Count() != lines.Count)
            throw InvalidLocation();
        return string.Join('\n', lines.OrderBy(row => row["line"]!.GetValue<int>()).Select(row => row["original"]!.GetValue<string>()));
    }

    private static ProtocolException InvalidLocation() => new("INVALID_DIFF_LOCATION", "The suggestion is outside a complete RIGHT-side diff hunk.", "Refresh the PR and re-analyze its current revision, or explicitly choose a normal comment.");
}
