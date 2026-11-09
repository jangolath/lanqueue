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

    public Task<SshResult> RunWithInputAsync(Machine machine, string remoteCommand, ReadOnlyMemory<byte> input, int maxOutputChars, TimeSpan timeout, CancellationToken ct) =>
        ExecuteWithInput("ssh", SshArgs.Ssh(machine, ResolveKey(machine), remoteCommand), input, maxOutputChars, timeout, ct);

    public Task<SshResult> RunSplitAsync(
        Machine machine, string remoteCommand, ReadOnlyMemory<byte>? input, Action<string> onStdout, Action<string> onStderr, TimeSpan timeout, CancellationToken ct) =>
        Execute("ssh", SshArgs.Ssh(machine, ResolveKey(machine), remoteCommand), onStdout, timeout, ct, input, onStderr);

    /// <summary>The most lanq ever sends on stdin (a script); a bigger input is a bug, not something to stream.</summary>
    internal const int MaxInputBytes = 1024 * 1024;

    /// <summary>
    /// Writes <paramref name="input"/> to the process's stdin and closes it. Stdout is read raw (no line splitting) and only its first
    /// <paramref name="maxOutputChars"/> are kept, the rest is read and dropped; stderr is kept apart, bounded like a probe's output.
    /// </summary>
    internal static async Task<SshResult> ExecuteWithInput(string exe, List<string> args, ReadOnlyMemory<byte> input, int maxOutputChars, TimeSpan timeout, CancellationToken ct)
    {
        if (input.Length > MaxInputBytes)
        {
            throw new ArgumentException("Input too large.", nameof(input));
        }

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

        var gate = new object();
        var stdout = new StringBuilder();
        var truncated = false;
        var errors = new OutputCollector(keepTailOnly: false);
        var closed = false;
        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new SshResult(255, "") { ErrorOutput = $"Could not start {exe}: {ex.Message}" };
        }

        async Task ReadStdout()
        {
            var buffer = new char[4096];
            try
            {
                int n;
                while ((n = await proc.StandardOutput.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
                {
                    lock (gate)
                    {
                        var room = closed ? 0 : Math.Max(0, maxOutputChars - stdout.Length);
                        stdout.Append(buffer, 0, Math.Min(room, n));
                        truncated |= n > room;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // killed or disposed: stop reading
            }
        }

        async Task WriteInput()
        {
            try
            {
                var stream = proc.StandardInput.BaseStream;
                await stream.WriteAsync(input, CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // the other side stopped reading (exited early or was killed); its exit code tells the rest
            }
            finally
            {
                try
                {
                    proc.StandardInput.Close();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // already broken
                }
            }
        }

        void OnError(string line)
        {
            lock (gate)
            {
                if (!closed)
                {
                    errors.Add(line);
                }
            }
        }

        var pumps = Task.WhenAll(ReadStdout(), LineReader.PumpAsync(proc.StandardError, OnError), WriteInput());

        SshResult Finish(int exitCode, bool timedOut)
        {
            lock (gate)
            {
                closed = true;
                var err = errors.ToString();
                return new SshResult(exitCode, stdout.ToString(), timedOut)
                {
                    OutputTruncated = truncated,
                    ErrorOutput = timedOut ? err + "[lanq] timed out after " + Describe(timeout) : err,
                };
            }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // it exited on its own in the meantime
            }

            await Drain(pumps).ConfigureAwait(false);
            var timedOut = Finish(TimedOutExitCode, timedOut: true);
            ct.ThrowIfCancellationRequested();
            return timedOut;
        }

        await Drain(pumps).ConfigureAwait(false);
        return Finish(proc.ExitCode, timedOut: false);
    }

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

    /// <param name="input">Written to stdin, which is then closed; null closes stdin at once (never wait for interactive input).</param>
    /// <param name="onError">When given, stderr lines go only here (not to <paramref name="onLine"/> or the result's output).</param>
    internal static async Task<SshResult> Execute(
        string exe, List<string> args, Action<string>? onLine, TimeSpan timeout, CancellationToken ct, ReadOnlyMemory<byte>? input = null, Action<string>? onError = null)
    {
        if (input is { Length: > MaxInputBytes })
        {
            throw new ArgumentException("Input too large.", nameof(input));
        }

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

        var collected = new OutputCollector(keepTailOnly: onLine is not null);
        var gate = new object();
        var closed = false;
        void Handle(string line)
        {
            lock (gate)
            {
                if (closed)
                {
                    return; // the result was already built: a straggling line is dropped, never delivered late
                }

                collected.Add(line);
                onLine?.Invoke(line);
            }
        }

        void HandleError(string line)
        {
            lock (gate)
            {
                if (!closed)
                {
                    onError!(line);
                }
            }
        }

        SshResult Finish(int exitCode, string? suffix, bool timedOut)
        {
            lock (gate)
            {
                closed = true;
                var text = collected.ToString();
                return new SshResult(exitCode, suffix is null ? text : text + Environment.NewLine + suffix, timedOut);
            }
        }

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new SshResult(255, $"Could not start {exe}: {ex.Message}");
        }

        var writer = input is { } bytes ? WriteAndClose(proc, bytes) : CloseInput(proc);
        var pumps = Task.WhenAll(
            LineReader.PumpAsync(proc.StandardOutput, Handle),
            LineReader.PumpAsync(proc.StandardError, onError is null ? Handle : HandleError),
            writer);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // it exited on its own in the meantime
            }

            await Drain(pumps).ConfigureAwait(false);
            var timedOut = Finish(TimedOutExitCode, "[lanq] timed out after " + Describe(timeout), timedOut: true);
            ct.ThrowIfCancellationRequested();
            return timedOut;
        }

        await Drain(pumps).ConfigureAwait(false);
        return Finish(proc.ExitCode, null, timedOut: false);
    }

    private static Task CloseInput(Process proc)
    {
        proc.StandardInput.Close(); // never wait for interactive input
        return Task.CompletedTask;
    }

    private static async Task WriteAndClose(Process proc, ReadOnlyMemory<byte> input)
    {
        try
        {
            var stream = proc.StandardInput.BaseStream;
            await stream.WriteAsync(input, CancellationToken.None).ConfigureAwait(false);
            await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // the other side stopped reading (exited early or was killed); its exit code tells the rest
        }
        finally
        {
            try
            {
                proc.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // already broken
            }
        }
    }

    /// <summary>Lets the readers finish what the process already wrote, but never waits forever (a grandchild may hold the pipe).</summary>
    private static async Task Drain(Task pumps)
    {
        try
        {
            await pumps.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // whatever arrives later is dropped by the closed gate
        }
    }
}

/// <summary>
/// Splits a stream into lines without ever holding more than <see cref="OutputCollector.MaxLineChars"/> of one line: the rest of an
/// overlong line is discarded and the line ends with a marker. A newline-free flood therefore costs a fixed amount of memory. Like
/// TextReader.ReadLine, LF, CR and CRLF each end a line, so a progress bar redrawn with CR shows as successive lines.
/// </summary>
internal static class LineReader
{
    internal const string Truncated = " [lanq: line truncated]";

    public static async Task PumpAsync(TextReader reader, Action<string> onLine)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        var truncated = false;
        var afterCr = false; // a CR just ended a line, so an LF right after it (even in the next read) is the same line end

        void Emit()
        {
            if (truncated)
            {
                if (char.IsHighSurrogate(line[^1]))
                {
                    line.Length--; // never split a surrogate pair
                }

                line.Append(Truncated);
            }

            onLine(line.ToString());
            line.Clear();
            truncated = false;
        }

        try
        {
            int n;
            while ((n = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            {
                for (var i = 0; i < n; i++)
                {
                    var c = buffer[i];
                    if (c == (char)10 && afterCr)
                    {
                        afterCr = false;
                        continue;
                    }

                    afterCr = c == (char)13;
                    if (c is (char)10 or (char)13)
                    {
                        Emit(); // LF, CR (progress bars) and CRLF all end a line
                    }
                    else if (line.Length < OutputCollector.MaxLineChars)
                    {
                        line.Append(c);
                    }
                    else
                    {
                        truncated = true;
                    }
                }
            }

            if (line.Length > 0)
            {
                Emit();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // the process was killed or disposed: stop reading
        }
    }
}

/// <summary>
/// Collects a command's output for the returned <see cref="SshResult"/>, always bounded. When the output is also streamed to a callback (a
/// job can run for hours) only the last <see cref="TailChars"/> are kept. Otherwise (slot claim, health probe) only the first
/// <see cref="HeadChars"/> are kept: the values lanq reads come first, and a worker that floods more is cut off. Not thread-safe; the
/// caller serialises access.
/// </summary>
internal sealed class OutputCollector(bool keepTailOnly)
{
    internal const int TailChars = 64 * 1024;

    internal const int HeadChars = 64 * 1024;

    internal const int MaxLineChars = 16 * 1024;

    private readonly StringBuilder _sb = new();
    private bool _dropped;

    public void Add(string line)
    {
        if (!keepTailOnly)
        {
            if (_dropped || _sb.Length + line.Length + Environment.NewLine.Length > HeadChars)
            {
                _dropped = true;
                return;
            }

            _sb.AppendLine(line);
            return;
        }

        _sb.AppendLine(line);
        if (_sb.Length > 2 * TailChars)
        {
            _sb.Remove(0, _sb.Length - TailChars); // amortised: trims only once the buffer has doubled
            _dropped = true;
        }
    }

    public override string ToString()
    {
        if (!keepTailOnly)
        {
            return _dropped ? _sb + "[lanq] later output dropped" : _sb.ToString();
        }

        if (!_dropped && _sb.Length <= TailChars)
        {
            return _sb.ToString();
        }

        var start = Math.Max(0, _sb.Length - TailChars);
        return "[lanq] earlier output dropped; showing the last part" + Environment.NewLine + _sb.ToString(start, _sb.Length - start);
    }
}
