using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LanQueue.Config;

namespace LanQueue.Ssh;

/// <summary>
/// 0.13.0 B3: the transport of the fallback entry (local: true). It runs the very scripts a worker gets (probe, slot claim, prepare, execute,
/// fetch, cleanup) as local processes: on Windows the same "powershell ... -EncodedCommand" line, on Linux "/bin/sh -c" with the same text ssh
/// would hand to the worker's shell. An upload is a file copy into work_root/incoming. So slots, the job object or process group, the fetch
/// and the cleanup are the worker path. No sshd is needed.
/// <para>
/// Two things differ from ssh and are handled here (review H2, M8). The environment is built from scratch from a fixed allowlist
/// (<see cref="ChildEnvironment"/>): sshd gives a worker a clean login environment, while a child of lanq would inherit the developer's shell
/// (SSH_AUTH_SOCK, tokens, MSBuild variables of an outer build, an outer LANQ_JOB). And on Windows every child joins a lanq-owned job object
/// that kills it when lanq exits, so closing the terminal does not leave a job running on the PC.
/// </para>
/// </summary>
public sealed class LocalProcessClient : ISshClient
{
    private readonly Func<IDictionary> _environment;
    private readonly bool _windows;
    private readonly Action<string> _warn;
    private int _warnedJobObject;

    /// <param name="environment">lanq's own environment (tests pass their own); null reads the process environment.</param>
    /// <param name="windows">Whether this computer runs Windows; null asks the OS.</param>
    /// <param name="warn">Where the one-time warning goes when a child could not join lanq's job object; null writes to stderr.</param>
    public LocalProcessClient(Func<IDictionary>? environment = null, bool? windows = null, Action<string>? warn = null)
    {
        _environment = environment ?? Environment.GetEnvironmentVariables;
        _windows = windows ?? OperatingSystem.IsWindows();
        _warn = warn ?? (line => Console.Error.WriteLine(line));
    }

    /// <summary>
    /// Windows PowerShell by its full path under the system folder (review A-L11): a bare "powershell.exe" is searched in lanq's current folder
    /// (the repository) before System32, so a repository could plant its own.
    /// </summary>
    internal static string WindowsPowerShellPath() =>
        Path.Combine(OperatingSystem.IsWindows() ? Environment.SystemDirectory : @"C:\Windows\System32", "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>Windows: the variables a local child keeps (names compared ignoring case; a trailing * is a prefix).</summary>
    internal static readonly string[] WindowsAllowed =
    [
        "SystemRoot", "windir", "SystemDrive", "ComSpec", "PATHEXT", "PATH", "TEMP", "TMP", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA",
        "LOCALAPPDATA", "ProgramData", "ProgramFiles*", "CommonProgramFiles*", "USERNAME", "USERDOMAIN", "COMPUTERNAME", "NUMBER_OF_PROCESSORS",
        "PROCESSOR_*", "OS", "ProgramW6432", "CommonProgramW6432", "ALLUSERSPROFILE", "PUBLIC",
    ];

    /// <summary>Linux: the variables a local child keeps (exact names; a trailing * is a prefix).</summary>
    internal static readonly string[] LinuxAllowed = ["PATH", "HOME", "USER", "LOGNAME", "SHELL", "LANG", "LC_*", "TMPDIR"];

    /// <summary>
    /// The environment of every local child: only the allowlisted variables of <paramref name="parent"/>, everything else dropped (SSH_AUTH_SOCK,
    /// GH_TOKEN, cloud credentials, NUGET_*, NODE_OPTIONS, MSBuild* and DOTNET_HOST_PATH of an outer build, an outer LANQ_JOB). The machine's
    /// env: and path_prepend: are then applied by the job script itself, exactly as on a worker.
    /// </summary>
    internal static Dictionary<string, string> ChildEnvironment(IDictionary parent, bool windows)
    {
        var allowed = windows ? WindowsAllowed : LinuxAllowed;
        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var kept = new Dictionary<string, string>(windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DictionaryEntry e in parent)
        {
            if (e.Key is string name && e.Value is string value && Allowed(name))
            {
                kept[name] = value;
            }
        }

        return kept;

        bool Allowed(string name) => allowed.Any(a => a.EndsWith('*')
            ? name.StartsWith(a[..^1], comparison)
            : string.Equals(name, a, comparison));
    }

    /// <summary>
    /// The local program and arguments for a remote command line. Windows: lanq's command lines are always "powershell ..." with plain words
    /// (the encoded scripts, <see cref="Jobs.RemoteScripts.WindowsCommand"/>) or one double-quoted -Command (the stdin runners), split as cmd.exe
    /// would (<see cref="WindowsWords"/>). Linux: the line is what ssh hands to the worker's shell, so /bin/sh -c parses it the same way.
    /// </summary>
    internal (string Exe, List<string> Args) CommandOf(string remoteCommand)
    {
        if (!_windows)
        {
            return ("/bin/sh", ["-c", remoteCommand]);
        }

        var words = WindowsWords(remoteCommand);
        if (words is null || words.Count < 2 || !string.Equals(words[0], "powershell", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The local runner only runs lanq's own PowerShell command lines.", nameof(remoteCommand));
        }

        return (WindowsPowerShellPath(), [.. words.Skip(1)]);
    }

    /// <summary>
    /// The words of one of lanq's Windows command lines as cmd.exe would pass them on: split on spaces, a "double-quoted" part kept as one word
    /// without its quotes (the stdin runners' -Command "..."). Null for anything else (an unbalanced or embedded quote).
    /// </summary>
    internal static List<string>? WindowsWords(string line)
    {
        var words = new List<string>();
        var i = 0;
        while (i < line.Length)
        {
            if (line[i] == ' ')
            {
                i++;
                continue;
            }

            if (line[i] == '"')
            {
                var end = line.IndexOf('"', i + 1);
                if (end < 0 || (end + 1 < line.Length && line[end + 1] != ' '))
                {
                    return null;
                }

                words.Add(line[(i + 1)..end]);
                i = end + 1;
                continue;
            }

            var stop = line.IndexOf(' ', i);
            var word = stop < 0 ? line[i..] : line[i..stop];
            if (word.Contains('"', StringComparison.Ordinal))
            {
                return null;
            }

            words.Add(word);
            i = stop < 0 ? line.Length : stop;
        }

        return words;
    }

    /// <summary>
    /// How every local child starts: the allowlisted environment; the machine's work_root as its current folder (never lanq's own, the
    /// repository; the system temp folder until the work_root exists); no console of its own, so Ctrl+C at lanq's terminal and a tool that
    /// opens CONOUT$ never reach the developer's terminal (review A-L12; lanq's cancel path stops the child); on Windows, lanq's job object.
    /// </summary>
    internal ProcessHooks Hooks(Machine machine) => new(
        psi =>
        {
            psi.Environment.Clear();
            foreach (var (name, value) in ChildEnvironment(_environment(), _windows))
            {
                psi.Environment[name] = value;
            }

            psi.WorkingDirectory = Directory.Exists(machine.WorkRoot) ? Path.GetFullPath(machine.WorkRoot) : Path.GetTempPath();
            psi.CreateNoWindow = true;
        },
        proc =>
        {
            if (_windows && OperatingSystem.IsWindows() && !LocalJobObject.Assign(proc) && Interlocked.Exchange(ref _warnedJobObject, 1) == 0)
            {
                _warn("[lanq] warning: a fallback process could not join lanq's job object, so it may outlive lanq if lanq is killed (best effort; the job's own cleanup still runs)");
            }
        });

    public Task<SshResult> RunAsync(Machine machine, string remoteCommand, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
    {
        var (exe, args) = CommandOf(remoteCommand);
        return ProcessSshClient.Execute(exe, args, onLine, timeout, ct, hooks: Hooks(machine));
    }

    public Task<SshResult> RunWithInputAsync(Machine machine, string remoteCommand, ReadOnlyMemory<byte> input, int maxOutputChars, TimeSpan timeout, CancellationToken ct)
    {
        var (exe, args) = CommandOf(remoteCommand);
        return ProcessSshClient.ExecuteWithInput(exe, args, input, maxOutputChars, timeout, ct, Hooks(machine));
    }

    public Task<SshResult> RunSplitAsync(
        Machine machine, string remoteCommand, ReadOnlyMemory<byte>? input, Action<string> onStdout, Action<string> onStderr, TimeSpan timeout, CancellationToken ct)
    {
        var (exe, args) = CommandOf(remoteCommand);
        return ProcessSshClient.Execute(exe, args, onStdout, timeout, ct, input, onStderr, Hooks(machine));
    }

    /// <summary>The "upload": a copy into work_root/incoming (the folder the claim script made), never anywhere outside the machine's work_root.</summary>
    public async Task<SshResult> UploadAsync(Machine machine, string localPath, string remotePath, TimeSpan timeout, CancellationToken ct)
    {
        var root = Path.GetFullPath(machine.WorkRoot);
        var target = Path.GetFullPath(remotePath);
        if (!target.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, _windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return new SshResult(1, $"refused: {remotePath} is outside the work_root of {machine.Name}");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var source = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await using (source.ConfigureAwait(false))
            {
                var dest = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await using (dest.ConfigureAwait(false))
                {
                    await source.CopyToAsync(dest, cts.Token).ConfigureAwait(false);
                }
            }

            return new SshResult(0, "");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SshResult(ProcessSshClient.TimedOutExitCode, "copy timed out", TimedOut: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SshResult(1, "copy failed: " + ex.Message);
        }
    }

    /// <summary>
    /// True when lanq runs with rights a job must never inherit: an elevated (administrator) Windows token, or root on Linux. The fallback then
    /// refuses to run (0.13.0 B3: it would hand those rights to the repository's code).
    /// </summary>
    public static bool IsElevated()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return identity.IsSystem || new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }

        return Geteuid() == 0;
    }

    [DllImport("libc", EntryPoint = "geteuid")]
#pragma warning disable SYSLIB1054 // a blittable call with no marshalling: DllImport is AOT-safe here and needs no unsafe code
    private static extern uint Geteuid();
#pragma warning restore SYSLIB1054
}

/// <summary>
/// Windows only (0.13.0 B3, review M8): one job object per lanq process with KILL_ON_JOB_CLOSE. Every local child joins it, so when lanq ends
/// for any reason (Ctrl+C, a closed terminal, a crash) Windows ends the children too. Nested job objects (the job script's own) work on
/// Windows 8 and later.
/// </summary>
internal static class LocalJobObject
{
    private static readonly Lazy<IntPtr> Job = new(Create);

    /// <summary>Puts the child in lanq's job object; false when that failed (the job object could not be made, or the child could not join).</summary>
    public static bool Assign(Process proc)
    {
        var job = Job.Value;
        return job != IntPtr.Zero && AssignProcessToJobObject(job, proc.Handle); // best effort: a child that cannot join still runs, as on a worker
    }

    private static IntPtr Create()
    {
        var job = CreateJobObjectW(IntPtr.Zero, IntPtr.Zero);
        if (job == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var info = new ExtendedLimitInformation { BasicLimitInformation = new BasicLimitInformation { LimitFlags = KillOnJobClose } };
        var size = (uint)Marshal.SizeOf<ExtendedLimitInformation>();
        return SetInformationJobObject(job, ExtendedLimitInformationClass, ref info, size) ? job : IntPtr.Zero;
    }

    private const uint KillOnJobClose = 0x2000;
    private const int ExtendedLimitInformationClass = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

#pragma warning disable SYSLIB1054 // blittable signatures with no marshalling: DllImport is AOT-safe here and needs no unsafe code
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, IntPtr name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInformation info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
#pragma warning restore SYSLIB1054
}

/// <summary>
/// 0.13.0 B3: sends the fallback entry (local: true) to <see cref="LocalProcessClient"/> and every worker to ssh, so everything above the
/// transport (health probe, runner, unlock) works on both unchanged.
/// </summary>
public sealed class RoutingSshClient(ISshClient remote, ISshClient local) : ISshClient
{
    private ISshClient For(Machine m) => m.Local ? local : remote;

    public Task<SshResult> RunAsync(Machine machine, string remoteCommand, Action<string>? onLine, TimeSpan timeout, CancellationToken ct) =>
        For(machine).RunAsync(machine, remoteCommand, onLine, timeout, ct);

    public Task<SshResult> UploadAsync(Machine machine, string localPath, string remotePath, TimeSpan timeout, CancellationToken ct) =>
        For(machine).UploadAsync(machine, localPath, remotePath, timeout, ct);

    public Task<SshResult> RunWithInputAsync(Machine machine, string remoteCommand, ReadOnlyMemory<byte> input, int maxOutputChars, TimeSpan timeout, CancellationToken ct) =>
        For(machine).RunWithInputAsync(machine, remoteCommand, input, maxOutputChars, timeout, ct);

    public Task<SshResult> RunSplitAsync(
        Machine machine, string remoteCommand, ReadOnlyMemory<byte>? input, Action<string> onStdout, Action<string> onStderr, TimeSpan timeout, CancellationToken ct) =>
        For(machine).RunSplitAsync(machine, remoteCommand, input, onStdout, onStderr, timeout, ct);
}
