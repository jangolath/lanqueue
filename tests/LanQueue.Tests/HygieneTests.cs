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
public sealed class HygieneTests : IDisposable
{
    // Unique job ids per test (review B-L6): a real cleanup stops every process of this uid that carries LANQ_JOB=<id>.
    private readonly string _j1 = T.NewJobId();

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
        var words = RemoteScripts.JobEnvironmentPosix(m, _j1, env: env);
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
    [InlineData("")]
    [InlineData("    hygiene: [dotnet]")]
    [InlineData("    hygiene: [dotnet-lean]")]
    [InlineData("    hygiene: [dotnet]\n    env: { MSBUILDDISABLENODEREUSE: \"0\" }")] // B-M2 case 1: env: turns node reuse back on
    [InlineData("    hygiene: [dotnet-lean]\n    env: { UseSharedCompilation: \"true\" }")] // B-M2 case 4: the compiler server is back
    public void TheSweepNeverTakesItsExemptionsFromTheMachinesConfig(string keys)
    {
        // B-M2: whatever hygiene:, env: or a job's --env say, the sweep is the same call; it judges each process by its own command line.
        var m = Load(keys).Machines[0];
        const string call = "sweep_marked \"$ID\"\n";
        Assert.Contains(call, RemoteScripts.Cleanup(m, _j1, "1", T.RepoId) + "\n", StringComparison.Ordinal);
        Assert.Contains(call, RemoteScripts.StopJob(m, _j1), StringComparison.Ordinal);
        m.Transfer = TransferMode.Archive;
        Assert.Contains(call, RemoteScripts.Cleanup(m, _j1, "1") + "\n", StringComparison.Ordinal);
        Assert.Contains("sweep_marked \"$1\"\n", RemoteScripts.LinuxSlotFunctions, StringComparison.Ordinal);
        Assert.DoesNotContain("sweep_marked \"$ID\" ", RemoteScripts.Cleanup(m, _j1, "1"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSweepMatchesOnlyTheExactMarkerOfThisUidAndChecksAgainBeforeEachSignal()
    {
        var s = RemoteScripts.LinuxSweepFunction;
        Assert.Contains("grep -qzxF \"LANQ_JOB=$2\"", s, StringComparison.Ordinal); // exact NUL-delimited entry, never a prefix (B-L5)
        Assert.Contains("grep -lzxF \"LANQ_JOB=$1\" /proc/[0-9]*/environ", s, StringComparison.Ordinal); // one process for the scan
        Assert.Contains("grep -qxF \"LANQ_JOB=$2\"", s, StringComparison.Ordinal); // the fallback without grep -z
        Assert.Contains("sweep_env \"$1\" \"$3\"", s, StringComparison.Ordinal);
        Assert.Contains("$(id -u)", s, StringComparison.Ordinal);
        Assert.Contains("case \"$1\" in ''|*[!A-Za-z0-9_-]*) return 0 ;; esac", s, StringComparison.Ordinal);
        Assert.Contains("*MSBuild.dll*nodemode:*", s, StringComparison.Ordinal);
        Assert.Contains("*VBCSCompiler*", s, StringComparison.Ordinal);
        Assert.Contains("*rzc.dll*server*", s, StringComparison.Ordinal); // the Razor build server of older SDKs (final review L7)
        // M12: start time, marker and uid are checked again right before the TERM and again before the KILL.
        Assert.Equal(2, s.Split("sweep_alive \"${se%%:*}\"").Length - 1);
        Assert.DoesNotContain("build-server", RemoteScripts.Cleanup(Load("    hygiene: [dotnet-lean]").Machines[0], _j1, "1"), StringComparison.Ordinal);
    }

    /// <summary>Starts a detached loop whose command line is <paramref name="name"/> and its args, and whose environment carries the marker.</summary>
    private Process Marked(string jobId, string name, params string[] args)
    {
        var script = Path.Combine(_root, name);
        File.WriteAllText(script, "while :; do sleep 1; done\n"); // the sh lives on when a sweep stops one of its sleeps (they carry the marker too)
        var psi = new ProcessStartInfo("setsid") { UseShellExecute = false };
        foreach (var a in (string[])["sh", script, .. args.Length == 0 ? ["x"] : args])
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["LANQ_JOB"] = jobId;
        return Process.Start(psi)!;
    }

    [LinuxFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void TheRealSweepStopsTheJobsOwnLeftoversAndKeepsSharedServers()
    {
        // B-M2, per process: a node started with nodeReuse:false is the job's own; one with nodeReuse:true (machine env: or the repository
        // turned reuse back on), one with no nodeReuse word, the MSBuild server (nodemode:8) and the compiler server are spared.
        var id = T.NewJobId();
        var leftover = Marked(id, "leftover.sh");
        var privateNode = Marked(id, "MSBuild.dll", "/nologo", "/nodemode:1", "/nodeReuse:false");
        var reusable = Marked(id, "MSBuild.dll", "-nodemode:1", "-nodeReuse:true");
        var unknown = Marked(id, "MSBuild.exe", "/nodemode:1");
        var server = Marked(id, "MSBuild.dll", "/nodemode:8", "/nodeReuse:false");
        var compiler = Marked(id, "VBCSCompiler.dll", "-pipename:x");
        var razor = Marked(id, "rzc.dll", "server", "-p", "x"); // final review L7: shared like the compiler server
        Directory.CreateDirectory(Path.Combine(_root, "once"));
        var razorOnce = Marked(id, Path.Combine("once", "rzc.dll"), "generate", "-s", "x"); // a one-off Razor compile is the job's own (its own script file)
        var other = Marked(id + "x", "other.sh"); // a marker that only starts with the id: not this job
        var again = (Process?)null;
        Process[] spared = [reusable, unknown, server, compiler, razor, other];
        try
        {
            Thread.Sleep(300);
            var (code, output) = Shells.Run("linux", RemoteScripts.LinuxSweepFunction + "\nsweep_marked " + id);
            Assert.True(code == 0, output);
            Assert.True(leftover.WaitForExit(5000));
            Assert.True(privateNode.WaitForExit(5000));
            Assert.True(razorOnce.WaitForExit(5000));
            Assert.All(spared, p => Assert.False(p.HasExited));

            // The same through the loop, where grep has no -z: a grep on PATH that refuses it.
            var bin = Path.Combine(_root, "nogrepz");
            Directory.CreateDirectory(bin);
            var fake = Path.Combine(bin, "grep");
            File.WriteAllText(fake, "#!/bin/sh\nfor a in \"$@\"; do case \"$a\" in -*z*) exit 2 ;; esac; done\nif [ -x /usr/bin/grep ]; then exec /usr/bin/grep \"$@\"; fi\nexec /bin/grep \"$@\"\n");
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            again = Marked(id, "leftover2.sh");
            Thread.Sleep(300);
            (code, output) = Shells.Run("linux", "PATH=" + bin + ":$PATH\n" + RemoteScripts.LinuxSweepFunction + "\nsweep_marked " + id);
            Assert.True(code == 0, output);
            Assert.True(again.WaitForExit(5000));
            Assert.All(spared, p => Assert.False(p.HasExited));
        }
        finally
        {
            foreach (var p in (Process?[])[leftover, privateNode, razorOnce, again, .. spared])
            {
                try
                {
                    p?.Kill(entireProcessTree: true);
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

    [Theory]
    [InlineData("/", true)]
    [InlineData("C:/", true)]
    [InlineData("D:\\", true)]
    [InlineData("/root", true)]
    [InlineData("/home/build", true)]
    [InlineData("/home/build/", true)]
    [InlineData("C:/Users/build", true)]
    [InlineData("//server/share", true)]
    [InlineData("/srv/lanq", false)]
    [InlineData("/home/build/lanq", false)]
    [InlineData("C:/lanq", false)]
    [InlineData("C:/Users/build/lanq", false)]
    public void DoctorWarnsAboutAWorkRootThatIsARootOrAHomeFolder(string workRoot, bool warns)
    {
        // B-L9 (design review M11): prune refuses such a work_root; doctor says so for every machine.
        var m = Load("").Machines[0];
        m.WorkRoot = workRoot;
        var warn = Cli.WorkRootWarning(m);
        Assert.Equal(warns, warn is not null);
        if (warn is not null)
        {
            Assert.Equal(("work_root_scope", "warn"), (warn.Name, warn.Level));
        }
    }

    [Fact]
    public void TheWindowsDoctorScriptSaysWhetherCleanupHasAJobObject()
    {
        // B-L9 (the D1 note): doctor --remote reports "leftover processes possible (no job object)" when the helper exists but did not load.
        var script = RemoteScripts.Doctor(Load("", "windows", "D:/lanq").Machines[0]);
        Assert.Contains("'jobobject=none'", script, StringComparison.Ordinal);
        Assert.Contains("'jobobject=unknown'", script, StringComparison.Ordinal); // a fresh worker without the helper yet: no warning
    }
}
