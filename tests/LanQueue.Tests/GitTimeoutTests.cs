using System.Diagnostics;
using LanQueue.Payload;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// 0.9.1: every local git call is bounded, reads no stdin, never prompts or pages, and a timeout kills it and fails as
/// <see cref="GitTimeoutException"/> (an InvalidOperationException, so the existing InfraError / Rejected paths report it). The runner is
/// driven with a stand-in for git (a shell that sleeps, reads stdin or prints its environment), so the tests stay fast.
/// </summary>
public class GitTimeoutTests
{
    private static readonly string Dir = Path.GetTempPath();

    private static (string Exe, string[] Args) Shell(string powershell, string sh) => OperatingSystem.IsWindows()
        ? ("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", powershell])
        : ("sh", ["-c", sh]);

    private static Task<(int Code, string Output, string Error)> Run((string Exe, string[] Args) cmd, TimeSpan timeout, CancellationToken ct = default) =>
        GitPayloadBuilder.RunAsync(cmd.Exe, Dir, null, timeout, ct, cmd.Args);

    [Fact]
    public async Task AHungGitIsKilledAtItsLimitAndFailsAsATimeout()
    {
        var clock = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<GitTimeoutException>(() => Run(Shell("Start-Sleep -Seconds 60", "sleep 60"), TimeSpan.FromSeconds(1)));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"took {clock.Elapsed}");
        Assert.IsAssignableFrom<InvalidOperationException>(ex); // the snapshot, bundle and --baseline paths catch InvalidOperationException
        Assert.Contains("did not finish within 1 s and was stopped", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CtrlCStillCancelsAndIsNotReportedAsATimeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var clock = Stopwatch.StartNew();

        // A cancellation, not a GitTimeoutException (an InvalidOperationException), and the process is killed at once, not at its 5-min limit.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(Shell("Start-Sleep -Seconds 60", "sleep 60"), TimeSpan.FromMinutes(5), cts.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"took {clock.Elapsed}");
    }

    /// <summary>The hang seen under PowerShell Start-Job: git inherited a stdin nobody closes. Now stdin is closed at once, so a read ends.</summary>
    [Fact]
    public async Task StdinIsClosedSoAReadNeverWaits()
    {
        var (code, output, _) = await Run(Shell("[Console]::In.ReadToEnd() | Out-Null; 'read'", "cat >/dev/null; echo read"), TimeSpan.FromSeconds(30));
        Assert.Equal(0, code);
        Assert.Equal("read", output.Trim());
    }

    [Fact]
    public async Task GitCanNeitherPromptNorPage()
    {
        var (_, output, _) = await Run(
            Shell("\"$env:GIT_TERMINAL_PROMPT|$env:GCM_INTERACTIVE|$env:GIT_PAGER\"", "echo \"$GIT_TERMINAL_PROMPT|$GCM_INTERACTIVE|$GIT_PAGER\""),
            TimeSpan.FromSeconds(30));
        Assert.Equal("0|never|cat", output.Trim());
    }

    /// <summary>Both pipes are read at once: a git that fills stderr while lanq reads stdout cannot deadlock.</summary>
    [Fact]
    public async Task LargeStdoutAndStderrAreReadTogether()
    {
        var (code, output, error) = await Run(
            Shell("[Console]::Error.Write('b' * 300000); [Console]::Out.Write('a' * 300000)", "head -c 300000 /dev/zero | tr '\\0' b >&2; head -c 300000 /dev/zero | tr '\\0' a"),
            TimeSpan.FromSeconds(30));
        Assert.Equal(0, code);
        Assert.Equal(300000, output.Length);
        Assert.Equal(300000, error.Length);
    }

    [Theory]
    [InlineData("rev-parse", 60)]
    [InlineData("show-ref", 60)]
    [InlineData("merge-base", 600)]
    [InlineData("rev-list", 600)]
    [InlineData("cat-file", 60)]
    [InlineData("add", 600)]
    [InlineData("diff", 600)]
    [InlineData("bundle", 1800)]
    public void EachGitCallHasALimitByWhatItDoes(string subcommand, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), GitPayloadBuilder.TimeoutFor(subcommand));

    /// <summary>The archive's long-lived cat-file --batch stream has its own limit: on expiry it fails as a timeout and leaves no archive.</summary>
    [Fact]
    public async Task TheArchiveStreamTimesOutAndLeavesNoArchive()
    {
        using var repo = new GitTestRepo();
        File.WriteAllText(Path.Combine(repo.Dir, "a.txt"), "one");
        repo.Commit();
        var tree = repo.Git("rev-parse", "HEAD^{tree}").Trim();
        var archive = Path.Combine(repo.Dir, ".out", "j.tar.gz");
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);

        var ex = await Assert.ThrowsAsync<GitTimeoutException>(() => GitPayloadBuilder.WriteArchiveAsync(repo.Dir, tree, archive, CancellationToken.None, TimeSpan.Zero));
        Assert.Equal("cat-file", ex.Subcommand);
        Assert.False(File.Exists(archive));
    }
}

/// <summary>A throw-away git repository with one commit helper; its own git calls never inherit a GIT_DIR a test sets.</summary>
internal sealed class GitTestRepo : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "lanq-repo-" + Guid.NewGuid().ToString("N"));

    public GitTestRepo()
    {
        Directory.CreateDirectory(Dir);
        Git("init", "-q", "-b", "main");
    }

    public void Commit()
    {
        Git("add", "-A");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "c");
    }

    public string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = Dir, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var name in GitPayloadBuilder.InheritedGitVariables)
        {
            psi.Environment.Remove(name);
        }

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEndAsync();
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err.Result);
        return o;
    }

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(Dir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(f, FileAttributes.Normal);
        }

        Directory.Delete(Dir, recursive: true);
    }
}

/// <summary>Tests that change lanq's own process environment: run alone, after every parallel test, so no other test's git sees the change.</summary>
[CollectionDefinition(nameof(ProcessEnvironment), DisableParallelization = true)]
public sealed class ProcessEnvironment;

[Collection(nameof(ProcessEnvironment))]
public sealed class InheritedGitEnvironmentTests
{
    /// <summary>0.9.1: a GIT_DIR (or GIT_WORK_TREE, GIT_INDEX_FILE, ...) lanq inherits never points its snapshot at another repository.</summary>
    [Fact]
    public async Task AnInheritedGitDirDoesNotRedirectTheSnapshot()
    {
        using var mine = new GitTestRepo();
        using var other = new GitTestRepo();
        File.WriteAllText(Path.Combine(mine.Dir, "a.txt"), "one");
        mine.Commit();
        File.WriteAllText(Path.Combine(mine.Dir, "a.txt"), "two"); // uncommitted
        File.WriteAllText(Path.Combine(other.Dir, "b.txt"), "other");
        other.Commit();
        var head = mine.Git("rev-parse", "HEAD").Trim();

        string[] names = ["GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE"];
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("GIT_DIR", Path.Combine(other.Dir, ".git"));
            Environment.SetEnvironmentVariable("GIT_WORK_TREE", other.Dir);
            Environment.SetEnvironmentVariable("GIT_INDEX_FILE", Path.Combine(other.Dir, ".git", "index"));
            var p = await new GitPayloadBuilder().BuildAsync("j", mine.Dir, Path.Combine(mine.Dir, ".out"), PayloadKinds.None, CancellationToken.None);

            Assert.Equal(head, p.Head);
            Assert.Equal(["a.txt"], p.ChangedFiles);
            Assert.Contains("+two", p.OverlayDiff, StringComparison.Ordinal);
            Assert.DoesNotContain("b.txt", p.CommittedFiles ?? []);
        }
        finally
        {
            foreach (var (n, v) in saved)
            {
                Environment.SetEnvironmentVariable(n, v);
            }
        }
    }
}
