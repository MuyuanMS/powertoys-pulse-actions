using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Pulse.Host;

internal sealed record AgentInstallationCandidate(string Path, string Source, bool? IsCurrent = null, bool? Historical = null);

public static partial class RuntimeService
{
    private static readonly TimeSpan InventoryCommandTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan InventoryDeadline = TimeSpan.FromSeconds(12);

    /// <summary>List every native CLI found in PATH and supported installation locations; never select one.</summary>
    public static Task<JsonArray> ListAgentInstallationsAsync(string agent)
    {
        var deadline = Environment.TickCount64 + (long)InventoryDeadline.TotalMilliseconds;
        return ListAgentInstallationsAsync(agent, AgentInstallationCandidates(agent), (path, arguments) =>
        {
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0) throw new OperationCanceledException("CLI inventory timeout: refresh to retry the remaining installations.");
            return ProbeCommandWithTimeout(path, arguments, TimeSpan.FromMilliseconds(Math.Min(remaining, InventoryCommandTimeout.TotalMilliseconds)));
        }, ResolveAgentExecutablePath);
    }

    internal static async Task<JsonArray> ListAgentInstallationsAsync(string agent, IEnumerable<AgentInstallationCandidate> candidates,
        Func<string, IReadOnlyList<string>, Task<string>> probe, Func<string, string?> resolvePath, TimeSpan? commandTimeout = null)
    {
        if (!SupportedAgents.Contains(agent)) throw new ProtocolException("CLI_NOT_SUPPORTED", "Only Codex CLI and GitHub Copilot CLI are supported.");
        var groups = new List<(string Path, string? Resolved, List<string> Aliases, List<string> Sources, List<bool> Current, List<bool> Historical)>();
        var lookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (!Path.IsPathFullyQualified(candidate.Path) || !Path.GetExtension(candidate.Path).Equals(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate.Path)) continue;
            var original = Path.GetFullPath(candidate.Path);
            string? resolved;
            try { resolved = resolvePath(original); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception) { resolved = null; }
            if (resolved is not null && (!Path.IsPathFullyQualified(resolved) || !Path.GetExtension(resolved).Equals(".exe", StringComparison.OrdinalIgnoreCase))) resolved = null;
            if (resolved is not null) resolved = Path.GetFullPath(resolved);
            // Physical path, rather than a binary hash, identifies an installation and its helpers.
            var key = resolved ?? original;
            if (!lookup.TryGetValue(key, out var index))
            {
                index = groups.Count;
                lookup.Add(key, index);
                groups.Add((original, resolved, [], [], [], []));
            }
            var group = groups[index];
            if (!group.Aliases.Contains(original, StringComparer.OrdinalIgnoreCase)) group.Aliases.Add(original);
            if (resolved is not null && !group.Aliases.Contains(resolved, StringComparer.OrdinalIgnoreCase)) group.Aliases.Add(resolved);
            if (!group.Sources.Contains(candidate.Source, StringComparer.Ordinal)) group.Sources.Add(candidate.Source);
            if (candidate.IsCurrent is bool isCurrent) group.Current.Add(isCurrent);
            if (candidate.Historical is bool historical) group.Historical.Add(historical);
        }
        using var slots = new SemaphoreSlim(3);
        var deadline = Environment.TickCount64 + (long)InventoryDeadline.TotalMilliseconds;
        var rows = await Task.WhenAll(groups.Select(async group =>
        {
            await slots.WaitAsync();
            try
            {
                var row = await ProbeAgentExecutableAsync(agent, group.Resolved ?? group.Path, RunBounded);
                row["path"] = group.Path;
                row["resolvedPath"] = group.Resolved ?? group.Path;
                row["source"] = group.Sources.FirstOrDefault() ?? "PATH";
                row["sources"] = new JsonArray(group.Sources.Select(source => (JsonNode?)JsonValue.Create(source)).ToArray());
                row["aliases"] = new JsonArray(group.Aliases.Select(alias => (JsonNode?)JsonValue.Create(alias)).ToArray());
                if (group.Current.Count > 0) row["isCurrent"] = group.Current.Any(value => value);
                if (group.Historical.Count > 0) row["historical"] = !group.Current.Any(value => value) && group.Historical.Any(value => value);
                row["authentication"] = "not_probed";
                row["executionValidated"] = false;
                if (row["version"] is JsonValue version && version.TryGetValue<string>(out var versionText) && versionText.Length == 0) row["version"] = null;
                return (JsonNode?)row;
            }
            finally { slots.Release(); }
        }));
        return new JsonArray(rows);

        async Task<string> RunBounded(string path, IReadOnlyList<string> arguments)
        {
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0) throw new OperationCanceledException("CLI inventory timeout: refresh the list to retry installations that did not finish probing.");
            var limit = TimeSpan.FromMilliseconds(Math.Min(remaining, (commandTimeout ?? InventoryCommandTimeout).TotalMilliseconds));
            try { return await probe(path, arguments).WaitAsync(limit); }
            catch (TimeoutException) { throw new OperationCanceledException("CLI version or help probe timed out. Check this installation and refresh the list."); }
            catch (OperationCanceledException) { throw new OperationCanceledException("CLI version or help probe timed out. Check this installation and refresh the list."); }
        }
    }

    /// <summary>Resolve junction/symlink aliases using a read-only file handle; never execute the file.</summary>
    public static string? ResolveAgentExecutablePath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!OperatingSystem.IsWindows()) return Path.GetFullPath(path);
            var buffer = new StringBuilder(512);
            var length = GetFinalPathNameByHandle(file, buffer, (uint)buffer.Capacity, 0);
            if (length == 0) return null;
            if (length >= buffer.Capacity)
            {
                buffer.EnsureCapacity(checked((int)length + 1));
                length = GetFinalPathNameByHandle(file, buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity) return null;
            }
            var final = buffer.ToString();
            if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) final = @"\\" + final[8..];
            else if (final.StartsWith(@"\\?\", StringComparison.Ordinal)) final = final[4..];
            return Path.GetFullPath(final);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OverflowException) { return null; }
    }

    private static IEnumerable<AgentInstallationCandidate> AgentInstallationCandidates(string agent)
    {
        if (!SupportedAgents.Contains(agent)) throw new ProtocolException("CLI_NOT_SUPPORTED", "Only Codex CLI and GitHub Copilot CLI are supported.");
        var name = agent + ".exe";
        var found = new List<AgentInstallationCandidate>();
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var directory = entry.Trim().Trim('"');
            if (Path.IsPathFullyQualified(directory)) Add(Path.Combine(directory, name), "PATH");
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var modules = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules");
        Add(Path.Combine(local, "Microsoft", "WinGet", "Links", name), "WinGet");
        Children(Path.Combine(local, "Microsoft", "WinGet", "Packages"), agent == "codex" ? "OpenAI.Codex*" : "GitHub.Copilot*", "WinGet", name);
        if (agent == "codex")
        {
            var standaloneRoot = Path.Combine(profile, ".codex", "packages", "standalone");
            var current = Path.Combine(standaloneRoot, "current", "bin", name);
            var currentResolved = ResolveAgentExecutablePath(current);
            AddStandalone(Path.Combine(local, "Programs", "OpenAI", "Codex", "bin", name), false);
            AddStandalone(current, false);
            try
            {
                var releases = Path.Combine(standaloneRoot, "releases");
                if (Directory.Exists(releases))
                    foreach (var release in Directory.EnumerateDirectories(releases).Order(StringComparer.OrdinalIgnoreCase))
                        AddStandalone(Path.Combine(release, "bin", name), true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            void AddStandalone(string path, bool isRelease)
            {
                if (File.Exists(path)) found.Add(ClassifyStandaloneCandidate(path, currentResolved, isRelease, ResolveAgentExecutablePath));
            }
            Add(Path.Combine(local, "OpenAI", "Codex", "bin", name), "Desktop app");
            Children(Path.Combine(local, "OpenAI", "Codex", "bin"), "*", "Desktop app", name);
            Add(Path.Combine(modules, "@openai", "codex", "vendor", "x86_64-pc-windows-msvc", "codex", name), "npm");
            Add(Path.Combine(modules, "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "codex", name), "npm");
            Add(Path.Combine(modules, "@openai", "codex", "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "codex", name), "npm");
            Children(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"), "OpenAI.Codex_*", "Desktop app", "app", "resources", name);
        }
        else
        {
            Add(Path.Combine(local, "Programs", "GitHub Copilot", name), "Standalone");
            Add(Path.Combine(local, "Programs", "GitHub", "Copilot", name), "Standalone");
            Add(Path.Combine(profile, ".local", "bin", name), "Standalone");
            Add(Path.Combine(modules, "@github", "copilot", name), "npm");
            Add(Path.Combine(modules, "@github", "copilot-win32-x64", name), "npm");
            Add(Path.Combine(modules, "@github", "copilot", "node_modules", "@github", "copilot-win32-x64", name), "npm");
        }
        return found;

        void Add(string path, string source)
        {
            try { if (File.Exists(path)) found.Add(new AgentInstallationCandidate(Path.GetFullPath(path), source)); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { }
        }
        void Children(string directory, string pattern, string source, params string[] relative)
        {
            try
            {
                if (Directory.Exists(directory))
                    foreach (var child in Directory.EnumerateDirectories(directory, pattern).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                        Add(Path.Combine(new[] { child }.Concat(relative).ToArray()), source);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static AgentInstallationCandidate ClassifyStandaloneCandidate(string path, string? currentResolved, bool isRelease, Func<string, string?> resolvePath)
    {
        string? resolved;
        try { resolved = resolvePath(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception) { resolved = null; }
        if (currentResolved is not null && resolved is not null)
        {
            if (string.Equals(Path.GetFullPath(resolved), Path.GetFullPath(currentResolved), StringComparison.OrdinalIgnoreCase))
                return new AgentInstallationCandidate(path, "Standalone (current)", true, false);
            if (isRelease) return new AgentInstallationCandidate(path, "Standalone (previous release)", false, true);
        }
        return new AgentInstallationCandidate(path, isRelease ? "Standalone release" : "Standalone");
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
}
