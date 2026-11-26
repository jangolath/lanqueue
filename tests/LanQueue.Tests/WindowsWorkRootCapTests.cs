using LanQueue.Config;
using LanQueue.Jobs;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// 0.13.0 review M1: a Windows work_root is capped at load (<see cref="ConfigLoader.MaxWindowsWorkRootLength"/>) so that every script lanq
/// sends to a Windows worker, and the job object named after a job's pid file, still fit at the longest work_root that loads.
/// </summary>
public sealed class WindowsWorkRootCapTests
{
    /// <summary>A job id as lanq makes it (yyyyMMdd-HHmmss-6 hex digits): every real pid file and job object name has this length.</summary>
    private const string Job = "20261109-221503-a1b2c3";

    /// <summary>A work_root of exactly <paramref name="length"/> characters, in folders as a person would write it.</summary>
    internal static string RootOf(int length)
    {
        var root = "D:/" + string.Join('/', Enumerable.Repeat("lanq-work-folder", 30));
        return root[..length].TrimEnd('/').PadRight(length, 'x');
    }

    /// <summary>MAX_PATH less the terminating null: the longest kernel object name CreateJobObjectW takes, and the longest classic path.</summary>
    private const int MaxName = 259;

    /// <summary>build-02 with this work_root, loaded through ConfigLoader (with hygiene: dotnet-lean), then given every doctor tool check and 64 slots.</summary>
    private static Machine Loaded(string root)
    {
        var yaml = T.Yaml.Replace("work_root: D:/lanq", "work_root: " + root, StringComparison.Ordinal)
            .Replace("    allowed_hours:", "    hygiene: [dotnet-lean]\n    allowed_hours:", StringComparison.Ordinal);
        var m = ConfigLoader.Parse(yaml).Machines[1];
        Assert.Equal([Hygiene.DotnetLean], m.Hygiene);
        m.Capabilities = [.. RemoteScripts.ToolChecks.Keys];
        m.MaxJobs = 64;
        return m;
    }

    /// <summary>Every script lanq builds for a Windows worker on the command line (the ones sent on stdin, prune, fetch and the toolchain search, have no such cap).</summary>
    internal static Dictionary<string, string> CommandLineScripts(Machine m)
    {
        var archive = Loaded("D:/lanq");
        archive.WorkRoot = m.WorkRoot;
        archive.Transfer = TransferMode.Archive;
        var wide = new ShardInfo(ShardInfo.MaxShards, ShardInfo.MaxShards);
        var plan = new string('x', LanQueue.Plan.PlanFileLoader.MaxNameLength);
        var nonce = new string('0', 16);
        return new Dictionary<string, string>
        {
            ["probe"] = RemoteScripts.Probe(m),
            ["archive-probe"] = RemoteScripts.Probe(archive),
            ["acquire"] = RemoteScripts.AcquireSlot(m, Job, TimeSpan.FromHours(4)),
            // A job size caps the claim (0.13.0): it adds the count of every live slot, the largest claim (0.13.0 review 2 L1).
            ["acquire-capped"] = RemoteScripts.AcquireSlot(m, Job, TimeSpan.FromHours(4), maxSlots: 1),
            ["acquire-exclusive"] = RemoteScripts.AcquireAll(m, Job, TimeSpan.FromHours(4)),
            ["release-claim"] = RemoteScripts.ReleaseClaim(m, Job),
            ["upload (EnsureIncoming)"] = RemoteScripts.EnsureIncoming(m),
            ["prepare"] = RemoteScripts.Prepare(m, Job, T.RepoId),
            ["prepare-shared-bundle"] = RemoteScripts.Prepare(m, Job, T.RepoId, "20261109-221500-d4e5f6"),
            ["archive-prepare"] = RemoteScripts.Prepare(archive, Job),
            ["job"] = RemoteScripts.Execute(m, Job, ".", ["x", "|"], JobRequest.DefaultTimeout, wide, plan, nonce),
            ["job-in-dir"] = RemoteScripts.Execute(m, Job, "src", ["x", "|"], JobRequest.DefaultTimeout, wide, plan, nonce),
            ["post"] = RemoteScripts.Execute(m, Job, ".", ["x", "|"], JobRequest.DefaultTimeout, wide, plan, nonce, pidKind: "post"),
            ["post-in-dir"] = RemoteScripts.Execute(m, Job, "src", ["x", "|"], JobRequest.DefaultTimeout, wide, plan, nonce, pidKind: "post"),
            ["stop"] = RemoteScripts.StopJob(m, Job),
            ["cleanup (sweep)"] = RemoteScripts.Cleanup(m, Job, "1", T.RepoId),
            ["archive-cleanup"] = RemoteScripts.Cleanup(archive, Job, "1"),
            ["unlock"] = RemoteScripts.Unlock(m, all: false),
            ["unlock-all"] = RemoteScripts.Unlock(m, all: true),
            ["doctor"] = RemoteScripts.Doctor(m),
        };
    }

    /// <summary>
    /// At the longest work_root that loads (with hygiene: dotnet-lean, every doctor tool check and 64 slots), every Windows script lanq sends
    /// on the command line stays under the 8000-character cap, at its largest (the largest shard, the longest plan job name, the native .exe
    /// check), and the job object names and helper paths stay under MAX_PATH. A script that grows past the cap here must shrink, or the cap
    /// must come down (and the CHANGELOG with it).
    /// </summary>
    [Fact]
    public void EveryWindowsScriptFitsAtTheLongestWorkRootThatLoads()
    {
        var cap = ConfigLoader.MaxWindowsWorkRootLength;
        var m = Loaded(RootOf(cap));
        Assert.Equal(cap, m.WorkRoot.Length);
        var sizes = CommandLineScripts(m).ToDictionary(s => s.Key, s => RemoteScripts.WindowsCommand(s.Value).Length);
        var all = string.Join(", ", sizes.Select(s => $"{s.Key} {s.Value}"));
        foreach (var (name, script) in CommandLineScripts(m))
        {
            Assert.True(sizes[name] <= RemoteScripts.MaxWindowsCommandLength, $"the Windows '{name}' script is {sizes[name]} characters once encoded at a {cap}-character work_root ({all})");
            Assert.Equal(RemoteScripts.WindowsCommand(script), RemoteScripts.Wrap(m, script));
        }

        Assert.True(RemoteScripts.WorstCaseWindowsCommandLength(m) <= RemoteScripts.MaxWindowsCommandLength);

        // The job object: "lanq-" plus the pid file path (LqName keeps its length), for a job and for a post: step.
        foreach (var kind in new[] { "job", "post" })
        {
            var name = "lanq-" + m.WorkRoot + "/locks/" + kind + "-" + Job + ".pid";
            Assert.True(name.Length <= MaxName, $"the job object name is {name.Length} characters");
        }

        // The helper and its temporary file ("$h.$PID", a pid of up to 10 digits), and the largest upload.
        Assert.True(RemoteScripts.WindowsHelperPath(m).Length + 11 <= MaxName);
        Assert.True((m.WorkRoot + "/incoming/" + Job + ".tar.gz").Length <= MaxName);
    }

    /// <summary>
    /// The job's child PowerShell starts with -NoProfile -NonInteractive spelled out, as in 0.12 (0.13.0 review 2 L2: EDR rules key on
    /// -nop -noni); only the post: step's child uses the short forms, which it needs to fit at the cap.
    /// </summary>
    [Fact]
    public void TheJobChildSpellsOutItsSwitchesAndOnlyThePostStepShortensThem()
    {
        var scripts = CommandLineScripts(Loaded(RootOf(ConfigLoader.MaxWindowsWorkRootLength)));
        foreach (var name in new[] { "job", "job-in-dir" })
        {
            Assert.Contains("$si.Arguments='-NoProfile -NonInteractive -ep Bypass -ec '", scripts[name], StringComparison.Ordinal);
            Assert.DoesNotContain("-nop", scripts[name], StringComparison.Ordinal);
        }

        foreach (var name in new[] { "post", "post-in-dir" })
        {
            Assert.Contains("$si.Arguments='-nop -noni -ep Bypass -ec '", scripts[name], StringComparison.Ordinal);
        }
    }

    /// <summary>The cap is checked at load, before anything is sent, and the message names the cap and the work_root's length.</summary>
    [Fact]
    public void AWindowsWorkRootOverTheCapIsRefusedAtLoadNamingTheCapAndItsLength()
    {
        var cap = ConfigLoader.MaxWindowsWorkRootLength;
        Assert.Equal(cap, Loaded(RootOf(cap)).WorkRoot.Length);
        var ex = Assert.Throws<ConfigException>(() => Loaded(RootOf(cap + 1)));
        Assert.Contains("build-02", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"at most {cap} characters; this one is {cap + 1}", ex.Message, StringComparison.Ordinal);
        Assert.Contains("MAX_PATH", ex.Message, StringComparison.Ordinal);

        // A Linux work_root has no such cap (no job object, no encoded command line).
        var linux = "/" + RootOf(cap + 50)[3..];
        Assert.Equal(linux, ConfigLoader.Parse(T.Yaml.Replace("work_root: /srv/lanq", "work_root: " + linux, StringComparison.Ordinal)).Machines[0].WorkRoot);
    }

    /// <summary>The upload script (EnsureIncoming) writes work_root once, like the job script, and builds incoming/, bin/ and the helper path from it.</summary>
    [Fact]
    public void TheWindowsUploadScriptWritesTheWorkRootOnce()
    {
        var root = RootOf(ConfigLoader.MaxWindowsWorkRootLength);
        var script = RemoteScripts.EnsureIncoming(Loaded(root));
        Assert.Equal(1, script.Split(root).Length - 1);
        Assert.StartsWith("$rt='" + root + "'\n", script, StringComparison.Ordinal);
        Assert.Contains("-Path ($rt+'/incoming')", script, StringComparison.Ordinal);
        Assert.Contains("-Path ($rt+'/bin')", script, StringComparison.Ordinal);
        Assert.Contains("$h = $rt+'" + RemoteScripts.WindowsHelperRelativePath + "'", script, StringComparison.Ordinal);
        Assert.Equal(root + RemoteScripts.WindowsHelperRelativePath, RemoteScripts.WindowsHelperPath(Loaded(root)));
    }
}
