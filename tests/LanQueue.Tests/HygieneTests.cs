using System.Diagnostics;
using LanQueue.Config;
using LanQueue.Jobs;
using LanQueue.Policy;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// 0.13.0 D1: hygiene: [dotnet|dotnet-lean] profiles, the Linux leftover sweep scoped to the job's own marker that never stops a shared
/// MSBuild node or compiler server unless the profile makes it private (review H3, M12), and the doctor WARN (owner decision).
/// </summary>
[Collection(nameof(RealJobScripts))]
public sealed class HygieneTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lanq-hygiene-" + Guid.NewGuid().ToString("N"));

    public HygieneTests() => Directory.CreateDirectory(_root);

    public void Dispose() => TestTree.Delete(_root);

    private static FleetConfig Load(string machineKeys, string os = "linux", string root = "/srv/lanq") => ConfigLoader.Parse($"""
        policy:
          allowed_commands: ["dotnet test"]
        machines:
          - name: build-01
            os: {os}
            ssh: user@10.20.30.11
            work_root: {root}
            capabilities: [dotnet]
            max_jobs: 3
        {machineKeys}
        """);

    [Fact]
    public void ProfilesParseAndUnknownOrRepeatedWordsAreRefused()
    {
        Assert.Equal(["dotnet"], Load("    hygiene: [dotnet]").Machines[0].Hygiene!);
        Assert.Equal(["dotnet-lean"], Load("    hygiene: [dotnet-lean]").Machines[0].Hygiene!);
        Assert.Contains("hygiene must list profiles from: dotnet, dotnet-lean", Assert.Throws<ConfigException>(() => Load("    hygiene: [java]")).Message, StringComparison.Ordinal);
        Assert.Throws<ConfigException>(() => Load("    hygiene: [dotnet, dotnet]"));
    }

    [Fact]
    public void ProfilesExpandAndAnExplicitEnvWins()
    {
        Assert.Equal(
            [("MSBUILDDISABLENODEREUSE", "1"), ("DOTNET_CLI_TELEMETRY_OPTOUT", "1"), ("DOTNET_NOLOGO", "1")],
            Hygiene.EnvFor(Load("    hygiene: [dotnet]").Machines[0]));
        Assert.Contains(("UseSharedCompilation", "false"), Hygiene.EnvFor(Load("    hygiene: [dotnet-lean]").Machines[0]));
        var own = Load("    hygiene: [dotnet]\n    env: { DOTNET_NOLOGO: \"0\" }").Machines[0];
        Assert.DoesNotContain(Hygiene.EnvFor(own), v => v.Name == "DOTNET_NOLOGO");
        Assert.Contains("'DOTNET_NOLOGO=0'", RemoteScripts.JobEnvironmentPosix(own), StringComparison.Ordinal);
        Assert.Empty(Hygiene.EnvFor(Load("").Machines[0]));
    }

    [Fact]
    public void AJobsEnvYieldsNoConflictWithAProfileAndWins()
    {
        // L7: a profile's name is not the machine's env:, so a job's --env DOTNET_NOLOGO=0 is not an env conflict, and it is set later.
        var m = Load("    hygiene: [dotnet]").Machines[0];
        var env = new List<JobEnvVar> { new("DOTNET_NOLOGO", "0") };
        Assert.Null(JobRunner.EnvExclusion(m, env, windowsUnsafe: false));
        var words = RemoteScripts.JobEnvironmentPosix(m, "j1", env: env);
        Assert.True(words.IndexOf("'DOTNET_NOLOGO=1'", StringComparison.Ordinal) < words.IndexOf("'DOTNET_NOLOGO=0'", StringComparison.Ordinal));
        Assert.True(words.IndexOf("'MSBUILDDISABLENODEREUSE=1'", StringComparison.Ordinal) >= 0);
    }

    [Fact]
    public void TheWindowsCommandLengthCountsTheProfile()
    {
        var plain = Load("", "windows", "D:/lanq").Machines[0];
        var lean = Load("    hygiene: [dotnet-lean]", "windows", "D:/lanq").Machines[0];
        Assert.True(RemoteScripts.WorstCaseWindowsCommandLength(lean) > RemoteScripts.WorstCaseWindowsCommandLength(plain));
        Assert.Contains("$env:MSBUILDDISABLENODEREUSE = '1'", RemoteScripts.JobEnvironmentPowerShell(lean), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "0 0")]
    [InlineData("    hygiene: [dotnet]", "1 0")]
    [InlineData("    hygiene: [dotnet-lean]", "1 1")]
    public void CleanupAndStopSweepWithTheMachinesExemptions(string keys, string flags)
    {
        var m = Load(keys).Machines[0];
        var call = "sweep_marked \"$ID\" " + flags;
        Assert.Contains(call, RemoteScripts.Cleanup(m, "j1", "1", T.RepoId), StringComparison.Ordinal);
        Assert.Contains(call, RemoteScripts.StopJob(m, "j1"), StringComparison.Ordinal);
        m.Transfer = TransferMode.Archive;
        Assert.Contains(call, RemoteScripts.Cleanup(m, "j1", "1"), StringComparison.Ordinal);
        // A stale slot's job (stop_job, in every claim): the machine's hygiene is not known there, so both shared kinds are kept.
        Assert.Contains("sweep_marked \"$1\" 0 0", RemoteScripts.LinuxSlotFunctions, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSweepMatchesOnlyTheExactMarkerOfThisUidAndChecksAgainBeforeEachSignal()
    {
        var s = RemoteScripts.LinuxSweepFunction;
        Assert.Contains("grep -qxF \"LANQ_JOB=$1\"", s, StringComparison.Ordinal); // exact line match, never a prefix
        Assert.Contains("grep -qxF \"LANQ_JOB=$3\"", s, StringComparison.Ordinal);
        Assert.Contains("$(id -u)", s, StringComparison.Ordinal);
        Assert.Contains("case \"$1\" in ''|*[!A-Za-z0-9_-]*) return 0 ;; esac", s, StringComparison.Ordinal);
        Assert.Contains("*MSBuild.dll*nodemode:*", s, StringComparison.Ordinal);
        Assert.Contains("*VBCSCompiler*", s, StringComparison.Ordinal);
        // M12: start time, marker and uid are checked again right before the TERM and again before the KILL.
        Assert.Equal(2, s.Split("sweep_alive \"${se%%:*}\"").Length - 1);
        Assert.DoesNotContain("build-server", RemoteScripts.Cleanup(Load("    hygiene: [dotnet-lean]").Machines[0], "j1", "1"), StringComparison.Ordinal);
    }

    /// <summary>Starts a detached "sleep 60" whose command line is <paramref name="name"/> and whose environment carries the marker.</summary>
    private Process Marked(string jobId, string name, string arg = "x")
    {
        var script = Path.Combine(_root, name);
        File.WriteAllText(script, "while :; do sleep 1; done\n"); // the sh lives on when a sweep stops one of its sleeps (they carry the marker too)
        var psi = new ProcessStartInfo("setsid") { UseShellExecute = false };
        foreach (var a in new[] { "sh", script, arg })
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["LANQ_JOB"] = jobId;
        return Process.Start(psi)!;
    }

    [LinuxFact]
    public void TheRealSweepStopsTheJobsOwnLeftoversAndKeepsSharedServers()
    {
        const string id = "20261125-120000-0a0b0c";
        var leftover = Marked(id, "leftover.sh");
        var node = Marked(id, "MSBuild.dll", "/nodemode:1");
        var server = Marked(id, "VBCSCompiler.dll");
        var other = Marked(id + "x", "other.sh"); // a marker that only starts with the id: not this job
        try
        {
            Thread.Sleep(300);
            var (code, output) = Shells.Run("linux", RemoteScripts.LinuxSweepFunction + "\nsweep_marked " + id + " 0 0");
            Assert.True(code == 0, output);
            Assert.True(leftover.WaitForExit(5000));
            Assert.False(node.HasExited);
            Assert.False(server.HasExited);
            Assert.False(other.HasExited);

            (code, output) = Shells.Run("linux", RemoteScripts.LinuxSweepFunction + "\nsweep_marked " + id + " 1 1");
            Assert.True(code == 0, output);
            Assert.True(node.WaitForExit(5000));
            Assert.True(server.WaitForExit(5000));
            Assert.False(other.HasExited);
        }
        finally
        {
            foreach (var p in new[] { leftover, node, server, other })
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // already gone
                }
            }
        }
    }

    [Fact]
    public void DoctorWarnsAboutAParallelDotnetMachineWithoutHygiene()
    {
        var warn = Cli.HygieneWarning(Load("").Machines[0]);
        Assert.Equal(("hygiene", "warn"), (warn!.Name, warn.Level));
        Assert.Contains("hygiene: [dotnet]", warn.Fix, StringComparison.Ordinal);
        Assert.Null(Cli.HygieneWarning(Load("    hygiene: [dotnet]").Machines[0]));
        var one = Load("").Machines[0];
        one.MaxJobs = 1;
        Assert.Null(Cli.HygieneWarning(one));
    }
}
