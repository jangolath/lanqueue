using System.Diagnostics;
using LanQueue.Jobs;
using Xunit;

namespace LanQueue.Tests;

public class ScriptSafetyTests
{
    [Fact]
    public void ExecuteUsesTheNormalizedDirectoryNotTheRawOne()
    {
        var linux = RemoteScripts.Execute(T.Linux(), "job1", "./src//app\\x", ["dotnet", "test"]);
        Assert.Contains("'/srv/lanq/jobs/job1/src/app/x'", linux, StringComparison.Ordinal);
        Assert.DoesNotContain("./src", linux, StringComparison.Ordinal);
        var win = RemoteScripts.Execute(T.Windows(), "job1", "./src//app", ["dotnet", "test"]);
        Assert.Contains("'D:/lanq/jobs/job1/src/app'", win, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("/etc")]
    [InlineData("C:/x")]
    public void ExecuteRefusesUnnormalizableDirectories(string dir)
    {
        Assert.Throws<ArgumentException>(() => RemoteScripts.Execute(T.Linux(), "j", dir, ["dotnet", "test"]));
        Assert.Throws<ArgumentException>(() => RemoteScripts.Execute(T.Windows(), "j", dir, ["dotnet", "test"]));
    }

    [Fact]
    public void LinuxExecuteResolvesSymlinksAndChecksTheRealPathStaysInsideTheWorktree()
    {
        var s = RemoteScripts.Execute(T.Linux(), "job1", "src", ["dotnet", "test"]);
        Assert.Contains("cd -P", s, StringComparison.Ordinal);
        Assert.Contains("pwd -P", s, StringComparison.Ordinal);
        Assert.Contains("exit 126", s, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsExecuteChecksResolvedDirectoryStaysInsideTheWorktree()
    {
        var s = RemoteScripts.Execute(T.Windows(), "job1", "src", ["dotnet", "test"]);
        Assert.Contains("Resolve-Path", s, StringComparison.Ordinal);
        Assert.Contains("exit 126", s, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxExecuteRunsInItsOwnProcessGroupAndRecordsItForCleanup()
    {
        var s = RemoteScripts.Execute(T.Linux(), "job1", "src", ["dotnet", "test"]);
        Assert.Contains("setsid -w", s, StringComparison.Ordinal);
        Assert.Contains("locks/job-job1.pid", s, StringComparison.Ordinal);
        var c = RemoteScripts.Cleanup(T.Linux(), "job1", "1", T.RepoId);
        Assert.Contains("locks/job-$ID.pid", c, StringComparison.Ordinal);
        Assert.Contains("kill -s TERM", c, StringComparison.Ordinal);
        Assert.Contains("kill -s KILL", c, StringComparison.Ordinal);
        Assert.True(
            c.IndexOf("kill -s KILL", StringComparison.Ordinal) < c.IndexOf("worktree remove", StringComparison.Ordinal),
            "the job must be killed before its worktree is removed");
    }

    [Fact]
    public void WindowsExecuteRecordsItsPidAndCleanupKillsTheTreeBeforeRemovingTheWorktree()
    {
        var s = RemoteScripts.Execute(T.Windows(), "job1", "src", ["dotnet", "test"]);
        Assert.Contains("$PID", s, StringComparison.Ordinal);
        Assert.Contains("locks/job-job1.pid", s, StringComparison.Ordinal);
        var c = RemoteScripts.Cleanup(T.Windows(), "job1", "1", T.RepoId);
        Assert.Contains("taskkill", c, StringComparison.Ordinal);
        Assert.Contains("/T", c, StringComparison.Ordinal);
        Assert.True(c.IndexOf("taskkill", StringComparison.Ordinal) < c.IndexOf("worktree remove", StringComparison.Ordinal));
    }

    [LinuxFact]
    public void LinuxCleanupKillsTheRunningJobProcessGroup()
    {
        var root = Path.Combine(Path.GetTempPath(), "lanq-kill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "jobs", "jk", "src"));
        Directory.CreateDirectory(Path.Combine(root, "locks"));
        try
        {
            var m = T.Linux();
            m.WorkRoot = root;
            var marker = Path.Combine(root, "survivor");
            var script = RemoteScripts.Execute(m, "jk", "src", ["sh", "-c", "sleep 30 & wait; touch " + marker]);
            using var run = Process.Start(new ProcessStartInfo("sh") { ArgumentList = { "-c", script }, UseShellExecute = false })!;
            var pidFile = Path.Combine(root, "locks", "job-jk.pid");
            for (var i = 0; i < 100 && !File.Exists(pidFile); i++)
            {
                Thread.Sleep(50);
            }

            Assert.True(File.Exists(pidFile));
            Assert.Equal(0, Run("sh", "-c", RemoteScripts.Cleanup(m, "jk", "1", T.RepoId)));
            Assert.True(run.WaitForExit(15000));
            Assert.False(File.Exists(pidFile));
            Assert.False(File.Exists(marker));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WindowsCommandThatWouldExceedTheCommandLineLimitIsRefused()
    {
        var huge = new string('a', 20000);
        var script = RemoteScripts.Execute(T.Windows(), "j", ".", ["dotnet", "test", huge]);
        Assert.Throws<InvalidOperationException>(() => RemoteScripts.Wrap(T.Windows(), script));
    }

    [Fact]
    public void ShortWindowsCommandsWrapFine() =>
        Assert.NotEmpty(RemoteScripts.Wrap(T.Windows(), RemoteScripts.Execute(T.Windows(), "j", ".", ["dotnet", "test"])));

    /// <summary>
    /// Every fixed Windows worker script (with a realistic job id, work_root, env and path_prepend) leaves room under the 8000-character
    /// encoded-command cap (800 characters for a longer work_root, job arguments and env), so a growing script fails here instead of on a worker.
    /// </summary>
    [Fact]
    public void EveryWindowsWorkerScriptLeavesRoomUnderTheCommandLineCap()
    {
        var m = WithJobEnvironment(T.Windows());
        m.Capabilities = [.. RemoteScripts.ToolChecks.Keys];
        const string Job = "20261109-221503-a1b2c3";
        var scripts = new Dictionary<string, string>
        {
            ["probe"] = RemoteScripts.Probe(m),
            ["acquire"] = RemoteScripts.AcquireSlot(m, Job, TimeSpan.FromHours(4)),
            ["prepare"] = RemoteScripts.Prepare(m, Job, T.RepoId),
            ["execute"] = RemoteScripts.Execute(m, Job, "src", ["dotnet", "test"], TimeSpan.FromHours(4)),
            ["cleanup"] = RemoteScripts.Cleanup(m, Job, "1", T.RepoId),
            ["unlock"] = RemoteScripts.Unlock(m, all: false),
            ["doctor"] = RemoteScripts.Doctor(m),
            ["archive-prepare"] = RemoteScripts.Prepare(Archive(m), Job),
            ["archive-cleanup"] = RemoteScripts.Cleanup(Archive(m), Job, "1"),
        };
        foreach (var (name, script) in scripts)
        {
            var length = RemoteScripts.Wrap(m, script).Length;
            Assert.True(length <= 7200, $"the Windows '{name}' script is {length} characters once encoded; keep each under 7200");
        }
    }

    [ShFact]
    public void GeneratedLinuxScriptsPassShSyntaxCheck()
    {
        var m = T.Linux();
        var scripts = new Dictionary<string, string>
        {
            ["probe"] = RemoteScripts.Probe(m),
            ["acquire"] = RemoteScripts.AcquireSlot(m, "j1", TimeSpan.FromMinutes(5)),
            ["prepare"] = RemoteScripts.Prepare(m, "j1", T.RepoId),
            ["incoming"] = RemoteScripts.EnsureIncoming(m),
            ["execute"] = RemoteScripts.Execute(m, "j1", "src", ["dotnet", "test", "--filter", "a b; rm -rf /", "it's"]),
            ["cleanup"] = RemoteScripts.Cleanup(m, "j1", "2", T.RepoId),
            ["archive-probe"] = RemoteScripts.Probe(Archive(m)),
            ["archive-prepare"] = RemoteScripts.Prepare(Archive(m), "j1"),
            ["archive-cleanup"] = RemoteScripts.Cleanup(Archive(m), "j1", "2"),
            ["execute-env"] = RemoteScripts.Execute(WithJobEnvironment(m), "j1", "src", ["dotnet", "test"]),
        };
        foreach (var (name, script) in scripts)
        {
            var file = Path.Combine(Path.GetTempPath(), "lanq-sh-" + Guid.NewGuid().ToString("N") + ".sh");
            File.WriteAllText(file, script.ReplaceLineEndings("\n"));
            try
            {
                Assert.True(Run("sh", "-n", file) == 0, $"sh -n rejected the generated '{name}' script");
            }
            finally
            {
                File.Delete(file);
            }
        }
    }

    [PowerShellFact]
    public void GeneratedPowerShellScriptsParseCleanly()
    {
        var host = Shells.PowerShell!;
        var m = T.Windows();
        var scripts = new Dictionary<string, string>
        {
            ["probe"] = RemoteScripts.Probe(m),
            ["acquire"] = RemoteScripts.AcquireSlot(m, "j1", TimeSpan.FromMinutes(5)),
            ["prepare"] = RemoteScripts.Prepare(m, "j1", T.RepoId),
            ["incoming"] = RemoteScripts.EnsureIncoming(m),
            ["execute"] = RemoteScripts.Execute(m, "j1", "src", ["dotnet", "test", "--filter", "a b; rm -rf /", "it's"]),
            ["cleanup"] = RemoteScripts.Cleanup(m, "j1", "2", T.RepoId),
            ["archive-probe"] = RemoteScripts.Probe(Archive(m)),
            ["archive-prepare"] = RemoteScripts.Prepare(Archive(m), "j1"),
            ["archive-cleanup"] = RemoteScripts.Cleanup(Archive(m), "j1", "2"),
            ["execute-env"] = RemoteScripts.Execute(WithJobEnvironment(m), "j1", "src", ["dotnet", "test"]),
        };
        var checker = Path.Combine(Path.GetTempPath(), "lanq-ps-check-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(
            checker,
            "param($f)\n$e=$null\n[void][System.Management.Automation.Language.Parser]::ParseFile($f,[ref]$null,[ref]$e)\nif($e){ $e | ForEach-Object { Write-Output $_.Message }; exit 1 }\nexit 0\n");
        try
        {
            foreach (var (name, script) in scripts)
            {
                var file = Path.Combine(Path.GetTempPath(), "lanq-ps-" + Guid.NewGuid().ToString("N") + ".ps1");
                File.WriteAllText(file, "\ufeff" + script);
                try
                {
                    Assert.True(
                        Run(host, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", checker, file) == 0,
                        $"PowerShell parser rejected the generated '{name}' script");
                }
                finally
                {
                    File.Delete(file);
                }
            }
        }
        finally
        {
            File.Delete(checker);
        }
    }

    /// <summary>The same machine with env:, path_prepend: and priority: low, so the extra Execute lines are syntax-checked too.</summary>
    private static LanQueue.Config.Machine WithJobEnvironment(LanQueue.Config.Machine m)
    {
        m.Env["NODE_OPTIONS"] = "--max-old-space-size=2048 'x' $(y)";
        m.PathPrepend.Add(m.Os == LanQueue.Config.MachineOs.Windows ? "C:/tools/bin" : "/opt/tools/bin");
        m.Priority = LanQueue.Config.JobPriority.Low;
        return m;
    }

    private static LanQueue.Config.Machine Archive(LanQueue.Config.Machine m) => new()
    {
        Name = m.Name,
        Os = m.Os,
        Ssh = m.Ssh,
        WorkRoot = m.WorkRoot,
        MaxJobs = m.MaxJobs,
        Transfer = LanQueue.Config.TransferMode.Archive,
    };

    private static int Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    private static bool OnPath(string exe)
    {
        var names = OperatingSystem.IsWindows() ? new[] { exe + ".exe", exe + ".cmd" } : new[] { exe };
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(d => names.Any(n => File.Exists(Path.Combine(d, n))));
    }
}
