using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pulse.Host;

internal static partial class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) throw new ProtocolException("PLATFORM_UNSUPPORTED", "This Host supports Windows x64.");
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseExtension");
            var dataRootPosition = Array.IndexOf(args, "--data-root");
            if (dataRootPosition >= 0)
            {
                if (dataRootPosition + 1 >= args.Length || !Path.IsPathFullyQualified(args[dataRootPosition + 1])) throw new ProtocolException("INVALID_REQUEST", "--data-root requires an absolute path.");
                root = args[dataRootPosition + 1];
            }
            var store = new Store(root);
            if (args.Length > 0 && args[0] == "--worker")
            {
                if (args.Length < 2) throw new ProtocolException("INVALID_REQUEST", "A worker runId is required.");
                return await RuntimeService.RunWorkerAsync(store, Protocol.RunId(args[1]));
            }
            if (args.Length > 0 && args[0] == "--agent-test")
            {
                if (args.Length < 2) throw new ProtocolException("INVALID_REQUEST", "An agent test ID is required.");
                return await RuntimeService.RunAgentTestWorkerAsync(store, Protocol.RunId(args[1]));
            }
            if (args.Contains("--has-active"))
            {
                // Install/upgrade/uninstall takes the same admission lock before checking.
                using var held = store.AcquireLock("accept");
                if (WebActions.HasActive(store)) return 2;
                if (RuntimeService.HasActiveAgentTests(store)) return 2;
                foreach (var id in store.RunIds())
                {
                    store.RecoverCompletion(id);
                    RuntimeService.Reconcile(store, id);
                    if (Protocol.IsActive(store.ReadStatus(id)["state"]?.GetValue<string>())) return 2;
                    var operations = Path.Combine(store.RunDirectory(id), "operations");
                    if (Directory.Exists(operations) && Directory.EnumerateFiles(operations, "*.json").Any(path => store.ReadJson(path)?["status"]?.GetValue<string>() is "prepared" or "submitting")) return 2;
                }
                return 0;
            }
            var directStdio = args.Contains("--stdio");
            var installation = store.ReadJson(Path.Combine(root, "installation.json"));
            if (!directStdio)
            {
                var origin = args.FirstOrDefault(argument => argument.StartsWith("chrome-extension://", StringComparison.Ordinal));
                var allowed = installation?["extensionIds"] as JsonArray;
                if (origin is null || !ExtensionOrigin().IsMatch(origin) || allowed is null || !allowed.Any(id => origin == $"chrome-extension://{id?.GetValue<string>()}/"))
                    throw new ProtocolException("EXTENSION_NOT_ALLOWED", "This extension ID is not allowed by the Host installation.", "Register the actual Chrome and Edge extension IDs using the installer.");
            }
            var development = installation?["developmentOrigins"]?.GetValue<bool>() == true || (directStdio && args.Contains("--development"));
            var dispatcher = new Dispatcher(store, development);
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            while (await NativeFraming.ReadAsync(input) is JsonObject request)
                await NativeFraming.WriteAsync(output, await dispatcher.HandleAsync(request));
            // EOF closes this connection only. Workers have their own handles and lifetime.
            return 0;
        }
        catch (ProtocolException e) { await Console.Error.WriteLineAsync($"{e.Code}: {e.Message}"); return 3; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { await Console.Error.WriteLineAsync("HOST_IO_ERROR: Local records or the browser connection are unavailable."); return 3; }
        catch (Exception) { await Console.Error.WriteLineAsync("HOST_ERROR: The Host could not complete the operation. Preserve the task folder for diagnosis."); return 3; }
    }

    [GeneratedRegex(@"^chrome-extension://[a-p]{32}/$", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionOrigin();
}
