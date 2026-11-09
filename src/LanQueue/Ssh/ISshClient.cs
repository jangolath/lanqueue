using LanQueue.Config;

namespace LanQueue.Ssh;

/// <param name="ExitCode">Remote exit code. 255 means ssh itself failed (unreachable, auth refused).</param>
/// <param name="Output">Combined stdout and stderr.</param>
/// <param name="TimedOut">True when lanq stopped the command because it ran past its time limit.</param>
public sealed record SshResult(int ExitCode, string Output, bool TimedOut = false)
{
    public bool Ok => ExitCode == 0;

    public bool IsSshFailure => ExitCode == 255;
}

/// <summary>Abstraction over ssh/scp so everything above it is testable without hosts.</summary>
public interface ISshClient
{
    Task<SshResult> RunAsync(Machine machine, string remoteCommand, Action<string>? onLine, TimeSpan timeout, CancellationToken ct);

    Task<SshResult> UploadAsync(Machine machine, string localPath, string remotePath, TimeSpan timeout, CancellationToken ct);
}
