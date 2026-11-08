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

    private static async Task<SshResult> Execute(string exe, List<string> args, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
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

        var sb = new StringBuilder();
        var gate = new object();
        void Handle(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (gate)
            {
                sb.AppendLine(line);
                onLine?.Invoke(line);
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
            return new SshResult(255, sb + "\n[lanq] timed out");
        }

        proc.WaitForExit(); // flush async readers
        return new SshResult(proc.ExitCode, sb.ToString());
    }
}
