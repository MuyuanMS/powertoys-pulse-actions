using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pulse.Host;

// All launches use an executable and an argument vector. No shell interprets task input.
internal sealed class WindowsProcess : IDisposable
{
    private const uint Suspended = 0x4, Detached = 0x8, UnicodeEnvironment = 0x400,
        ExtendedStartup = 0x80000, Breakaway = 0x01000000, NoWindow = 0x08000000;
    private IntPtr processHandle;
    private IntPtr thread;
    private IntPtr job;
    private bool unclaimedWorker;
    public StreamWriter? Input { get; private set; }
    public StreamReader? Output { get; private set; }
    public StreamReader? Error { get; private set; }
    public int Id { get; }
    public string StartTimeUtc { get; }
    public bool HasExited
    {
        get
        {
            ObjectDisposedException.ThrowIf(processHandle == IntPtr.Zero, this);
            return WaitForSingleObject(processHandle, 0) switch
            {
                0 => true,
                258 => false,
                _ => throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot query the CLI process state.")
            };
        }
    }
    public int ExitCode
    {
        get
        {
            if (!HasExited) throw new InvalidOperationException("The CLI process has not exited.");
            if (!GetExitCodeProcess(processHandle, out var code)) throw new Win32Exception();
            return unchecked((int)code);
        }
    }

    private WindowsProcess(IntPtr processHandle, int processId, IntPtr thread, IntPtr job)
    {
        if (!GetProcessTimes(processHandle, out var creation, out _, out _, out _)) throw new Win32Exception();
        this.processHandle = processHandle;
        Id = processId;
        StartTimeUtc = DateTime.FromFileTimeUtc(creation).ToString("O");
        this.thread = thread;
        this.job = job;
    }

    public static WindowsProcess DetachedWorker(string executable, IReadOnlyList<string> arguments)
    {
        EnsureWindows();
        var startup = new StartupInfoEx { StartupInfo = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() } };
        var flags = Detached | Breakaway;
        var command = CommandLine(executable, arguments);
        if (!CreateProcess(executable, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, false,
                flags, IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out var info))
        {
            // Some launchers do not permit explicit breakaway, but use silent breakaway or no job.
            // The worker waits for its persisted identity before executing a CLI. Verify job escape
            // before publishing that identity; an unclaimed worker exits after its startup deadline.
            var firstError = Marshal.GetLastWin32Error();
            if (firstError is not (5 or 87) || !CreateProcess(executable, new StringBuilder(command),
                    IntPtr.Zero, IntPtr.Zero, false, Detached, IntPtr.Zero,
                    Path.GetDirectoryName(executable), ref startup, out info))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot start the detached task worker.");
        }
        try
        {
            if (!IsProcessInJob(info.Process, IntPtr.Zero, out var inJob)) throw new Win32Exception();
            if (inJob) throw new ProtocolException("BACKGROUND_JOB_RESTRICTED",
                "Windows process policy prevents the task worker from leaving its launcher's Job, so execution after the browser closes cannot be guaranteed.",
                "The CLI was not started. Check Native Messaging and process policies in the installed Chrome or Edge environment, then start a new run after resolving the restriction.");
            var worker = new WindowsProcess(info.Process, (int)info.ProcessId, IntPtr.Zero, IntPtr.Zero)
                { unclaimedWorker = true };
            info.Process = IntPtr.Zero; // The wrapper retains the original process handle through exit.
            CloseHandle(info.Thread);
            return worker;
        }
        catch
        {
            TerminateProcess(info.Process, 1);
            CloseHandle(info.Thread);
            throw;
        }
        finally { if (info.Process != IntPtr.Zero) CloseHandle(info.Process); }
    }

    // Diagnostics may end with the browser/launcher Job. They do not promise the detached
    // lifetime of accepted tasks, but still publish an exact identity before starting a CLI.
    public static WindowsProcess DiagnosticWorker(string executable, IReadOnlyList<string> arguments)
    {
        EnsureWindows();
        var startup = new StartupInfoEx { StartupInfo = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() } };
        if (!CreateProcess(executable, new StringBuilder(CommandLine(executable, arguments)), IntPtr.Zero,
                IntPtr.Zero, false, NoWindow, IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot start the agent diagnostic worker.");
        try
        {
            var worker = new WindowsProcess(info.Process, (int)info.ProcessId, IntPtr.Zero, IntPtr.Zero) { unclaimedWorker = true };
            info.Process = IntPtr.Zero;
            return worker;
        }
        catch { TerminateProcess(info.Process, 1); throw; }
        finally
        {
            CloseHandle(info.Thread);
            if (info.Process != IntPtr.Zero) CloseHandle(info.Process);
        }
    }

    public static WindowsProcess Cli(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        IDictionary<string, string>? environmentOverrides = null)
    {
        EnsureWindows();
        IntPtr inputRead = IntPtr.Zero, inputWrite = IntPtr.Zero, outputRead = IntPtr.Zero,
            outputWrite = IntPtr.Zero, errorRead = IntPtr.Zero, errorWrite = IntPtr.Zero;
        IntPtr attributes = IntPtr.Zero, handles = IntPtr.Zero, environment = IntPtr.Zero, job = IntPtr.Zero;
        ProcessInformation info = default;
        var created = false;
        var attributesInitialized = false;
        WindowsProcess? child = null;
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
            if (!CreatePipe(out inputRead, out inputWrite, ref security, 0) ||
                !CreatePipe(out outputRead, out outputWrite, ref security, 0) ||
                !CreatePipe(out errorRead, out errorWrite, ref security, 0)) throw new Win32Exception();
            foreach (var parent in new[] { inputWrite, outputRead, errorRead })
                if (!SetHandleInformation(parent, 1, 0)) throw new Win32Exception();

            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception();
            attributesInitialized = true;
            handles = Marshal.AllocHGlobal(3 * IntPtr.Size);
            Marshal.WriteIntPtr(handles, 0, inputRead);
            Marshal.WriteIntPtr(handles, IntPtr.Size, outputWrite);
            Marshal.WriteIntPtr(handles, 2 * IntPtr.Size, errorWrite);
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20002, handles,
                    (IntPtr)(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                    Input = inputRead, Output = outputWrite, Error = errorWrite },
                AttributeList = attributes
            };
            // An inherited COPILOT_ALLOW_ALL must not silently override the saved local policy.
            var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
                variables[(string)entry.Key] = entry.Value?.ToString() ?? "";
            if (environmentOverrides is not null)
                foreach (var pair in environmentOverrides)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Contains('=') || pair.Key.Contains('\0') || pair.Value.Contains('\0'))
                        throw new ArgumentException("Invalid child process environment override.", nameof(environmentOverrides));
                    variables[pair.Key] = pair.Value;
                }
            variables["COPILOT_ALLOW_ALL"] = "false";
            environment = Marshal.StringToHGlobalUni(string.Join('\0', variables.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}={pair.Value}")) + "\0\0");
            job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new Win32Exception();
            var limits = new JobExtendedLimits { Basic = new JobBasicLimits { LimitFlags = 0x2000 } };
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobExtendedLimits>()))
                throw new Win32Exception();
            if (!CreateProcess(executable, new StringBuilder(CommandLine(executable, arguments)), IntPtr.Zero,
                    IntPtr.Zero, true, Suspended | NoWindow | ExtendedStartup | UnicodeEnvironment, environment,
                    workingDirectory, ref startup, out info)) throw new Win32Exception();
            created = true;
            // The CLI is still suspended, so it cannot spawn an unmanaged descendant before assignment.
            if (!AssignProcessToJobObject(job, info.Process)) throw new Win32Exception();
            child = new WindowsProcess(info.Process, (int)info.ProcessId, info.Thread, job);
            info.Process = IntPtr.Zero;
            info.Thread = IntPtr.Zero;
            job = IntPtr.Zero;
            child.Input = new StreamWriter(new FileStream(new SafeFileHandle(inputWrite, true), FileAccess.Write), new UTF8Encoding(false));
            inputWrite = IntPtr.Zero;
            child.Output = new StreamReader(new FileStream(new SafeFileHandle(outputRead, true), FileAccess.Read), Encoding.UTF8);
            outputRead = IntPtr.Zero;
            child.Error = new StreamReader(new FileStream(new SafeFileHandle(errorRead, true), FileAccess.Read), Encoding.UTF8);
            errorRead = IntPtr.Zero;
            return child;
        }
        catch
        {
            child?.Dispose();
            if (created && info.Process != IntPtr.Zero) TerminateProcess(info.Process, 1);
            throw;
        }
        finally
        {
            foreach (var handle in new[] { inputRead, inputWrite, outputRead, outputWrite, errorRead, errorWrite, info.Process, info.Thread, job })
                if (handle != IntPtr.Zero) CloseHandle(handle);
            if (attributesInitialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
        }
    }

    public void Resume()
    {
        unclaimedWorker = false;
        if (thread == IntPtr.Zero) return;
        if (ResumeThread(thread) == uint.MaxValue) throw new Win32Exception();
        CloseHandle(thread);
        thread = IntPtr.Zero;
    }

    public async Task WaitAsync(CancellationToken token = default)
    {
        while (!HasExited) await Task.Delay(50, token);
    }

    public async Task StopAsync()
    {
        // CLI-specific interactive cancellation cannot be carried over a closed stdin. Offer EOF
        // and WM_CLOSE where supported, then enforce a bounded grace period for headless CLIs.
        try { Input?.Close(); } catch (IOException) { } catch (ObjectDisposedException) { }
        if (HasExited) return;
        try { using var windowOwner = Process.GetProcessById(Id); windowOwner.CloseMainWindow(); }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
        var exited = WaitAsync();
        if (await Task.WhenAny(exited, Task.Delay(1500)) != exited) Kill();
    }

    public void Kill()
    {
        if (job != IntPtr.Zero) TerminateJobObject(job, 1);
        else if (!HasExited) TerminateProcess(processHandle, 1);
    }

    public void Dispose()
    {
        if (processHandle == IntPtr.Zero) return;
        // A suspended process must never survive a failed state-file write.
        if (unclaimedWorker) { Kill(); unclaimedWorker = false; }
        if (thread != IntPtr.Zero) { Kill(); CloseHandle(thread); thread = IntPtr.Zero; }
        if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; }
        CloseHandle(processHandle);
        processHandle = IntPtr.Zero;
        Input?.Dispose(); Output?.Dispose(); Error?.Dispose();
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Pulse Host requires Windows 11 x64.");
    }

    internal static string CommandLine(string executable, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote));

    // CommandLineToArgvW/CRT quoting, including trailing backslashes and embedded quotes.
    private static string Quote(string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("NUL is not permitted in arguments.");
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public string? Reserved; public string? Desktop; public string? Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size; public IntPtr Reserved2, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct JobBasicLimits
    {
        public long ProcessTime, JobTime; public uint LimitFlags; public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct JobExtendedLimits
    {
        public JobBasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool inJob);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref JobExtendedLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
}
