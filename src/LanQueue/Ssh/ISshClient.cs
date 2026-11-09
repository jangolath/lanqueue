using LanQueue.Config;

namespace LanQueue.Ssh;

/// <param name="ExitCode">Remote exit code. 255 means ssh itself failed (unreachable, auth refused).</param>
/// <param name="Output">Combined stdout and stderr (for <see cref="ISshClient.RunWithInputAsync"/>: stdout only).</param>
/// <param name="TimedOut">True when lanq stopped the command because it ran past its time limit.</param>
public sealed record SshResult(int ExitCode, string Output, bool TimedOut = false)
{
    public bool Ok => ExitCode == 0;

    public bool IsSshFailure => ExitCode == 255;

    /// <summary>True when stdout went past the caller's limit; <see cref="Output"/> then holds only the first part.</summary>
    public bool OutputTruncated { get; init; }

    /// <summary>The start of stderr, bounded; set only by <see cref="ISshClient.RunWithInputAsync"/>.</summary>
    public string ErrorOutput { get; init; } = "";
}

/// <summary>Abstraction over ssh/scp so everything above it is testable without hosts.</summary>
public interface ISshClient
{
    Task<SshResult> RunAsync(Machine machine, string remoteCommand, Action<string>? onLine, TimeSpan timeout, CancellationToken ct);

    Task<SshResult> UploadAsync(Machine machine, string localPath, string remotePath, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// Runs <paramref name="remoteCommand"/> with <paramref name="input"/> written to its stdin, which is then closed (nothing is written to
    /// the worker's disk). Returns stdout kept apart from stderr, at most <paramref name="maxOutputChars"/> of it.
    /// </summary>
    Task<SshResult> RunWithInputAsync(Machine machine, string remoteCommand, ReadOnlyMemory<byte> input, int maxOutputChars, TimeSpan timeout, CancellationToken ct) =>
        throw new NotSupportedException("This ssh client cannot send input.");

    /// <summary>
    /// Like <see cref="RunAsync"/>, but stdout and stderr are kept apart: stdout lines go to <paramref name="onStdout"/> and stderr lines to
    /// <paramref name="onStderr"/>, each through the bounded line reader, so a stderr line can never land between two stdout lines of a framed
    /// stream. With <paramref name="input"/> it is written to the command's stdin, which is then closed (a script too long for a command line).
    /// </summary>
    Task<SshResult> RunSplitAsync(
        Machine machine, string remoteCommand, ReadOnlyMemory<byte>? input, Action<string> onStdout, Action<string> onStderr, TimeSpan timeout, CancellationToken ct) =>
        throw new NotSupportedException("This ssh client cannot keep stdout and stderr apart.");
}
