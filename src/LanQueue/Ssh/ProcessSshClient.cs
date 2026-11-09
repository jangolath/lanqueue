using System.Diagnostics;
using System.Text;
using LanQueue.Config;

namespace LanQueue.Ssh;

/// <summary>Real implementation: shells out to the system ssh and scp binaries.</summary>
public sealed class ProcessSshClient(Func<string, string?> getEnv) : ISshClient
{
    public Task<SshResult> RunAsync(Machine machine, string remoteCommand, Action<string>? onLine, TimeSpan timeout, CancellationToken ct) =>
        Execute("ssh", SshArgs.Ssh(machine, ResolveKey(machine), remoteCommand), onLine, timeout, ct);

    public Task<SshResult> UploadAsync(Machine machine, string localPath, string remotePath, TimeSpan timeout, CancellationToken ct) =>
        Execute("scp", SshArgs.Scp(machine, ResolveKey(machine), localPath, remotePath), null, timeout, ct);

    /// <summary>Exit code for a command lanq stopped at its time limit (the same code coreutils timeout uses).</summary>
    public const int TimedOutExitCode = 124;

    internal static string Describe(TimeSpan t) => t.TotalMinutes >= 1
        ? t.TotalMinutes.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " min"
        : t.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s";

    internal string? ResolveKey(Machine machine)
    {
        if (string.IsNullOrEmpty(machine.KeyEnv))
        {
            return null;
        }

        var path = getEnv(machine.KeyEnv);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Machine '{machine.Name}': environment variable {machine.KeyEnv} must point to an existing private key file.");
        }

        return path;
    }

    internal static async Task<SshResult> Execute(string exe, List<string> args, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        var sb = new OutputCollector(keepTailOnly: onLine is not null);
        var gate = new object();
        void Handle(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (gate)
            {
                onLine?.Invoke(sb.Add(line));
            }
        }

        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => Handle(e.Data);
        proc.ErrorDataReceived += (_, e) => Handle(e.Data);
        try
        {
            proc.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new SshResult(255, $"Could not start {exe}: {ex.Message}");
        }

        proc.StandardInput.Close(); // never wait for interactive input
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            proc.Kill(entireProcessTree: true);
            ct.ThrowIfCancellationRequested();
            return new SshResult(TimedOutExitCode, sb + Environment.NewLine + "[lanq] timed out after " + Describe(timeout), TimedOut: true);
        }

        proc.WaitForExit(); // flush async readers
        return new SshResult(proc.ExitCode, sb.ToString());
    }
}

/// <summary>
/// Collects a command's output for the returned <see cref="SshResult"/>. A single line is capped at <see cref="MaxLineChars"/>. When the
/// output is also streamed to a callback (a job can run for hours), only the last <see cref="TailChars"/> are kept, so memory stays bounded.
/// </summary>
internal sealed class OutputCollector(bool keepTailOnly)
{
    internal const int TailChars = 64 * 1024;

    internal const int MaxLineChars = 16 * 1024;

    private const string Truncated = " [lanq: line truncated]";

    private readonly StringBuilder _sb = new();
    private bool _dropped;

    /// <summary>Records one line and returns it as it should be shown (truncated with a marker if overlong).</summary>
    public string Add(string line)
    {
        if (line.Length > MaxLineChars)
        {
            line = string.Concat(line.AsSpan(0, MaxLineChars), Truncated);
        }

        _sb.AppendLine(line);
        if (keepTailOnly && _sb.Length > 2 * TailChars)
        {
            _sb.Remove(0, _sb.Length - TailChars); // amortised: trims only once the buffer has doubled
            _dropped = true;
        }

        return line;
    }

    public override string ToString()
    {
        if (!keepTailOnly || (!_dropped && _sb.Length <= TailChars))
        {
            return _sb.ToString();
        }

        var start = Math.Max(0, _sb.Length - TailChars);
        return "[lanq] earlier output dropped; showing the last part" + Environment.NewLine + _sb.ToString(start, _sb.Length - start);
    }
}
