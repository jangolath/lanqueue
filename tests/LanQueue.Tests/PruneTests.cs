using System.Text;
using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// 0.13.0 D2: lanq prune. Lists unless --yes; removes only lanq's own leftovers inside work_root (the job id shape, review M11), never through
/// a link, never an id a slot file or a running process names; repository caches only with no slot in use, while prune holds every slot
/// (review M10).
/// </summary>
public sealed class PruneTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lanq-prune-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');

    public PruneTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        TestTree.Delete(_root);
    }

    private const string OldId = "20261101-101010-0a0b0c";
    private const string LinkId = "20261101-101010-0d0e0f";
    private const string FreshId = "20261125-101010-111111";
    private const string LiveId = "20261101-101010-222222";
    private const string StaleId = "20261101-101010-333333";

    [Theory]
    [InlineData("2d", 172800)]
    [InlineData("1h", 3600)]
    [InlineData("365d", 31536000)]
    public void OlderThanTakesHoursOrDays(string text, long seconds) => Assert.Equal(seconds, Cli.ParseAge(text));

    [Theory]
    [InlineData("0h")]
    [InlineData("366d")]
    [InlineData("2w")]
    [InlineData("d")]
    [InlineData("-1d")]
    public void OlderThanOutOfRangeIsRefused(string text) => Assert.Throws<ArgumentException>(() => Cli.ParseAge(text));

    [Fact]
    public void ArgumentsParse()
    {
        var p = ParsedArgs.Parse(["prune", "build-01", "--older-than", "3d", "--caches", "--yes", "--json"]);
        Assert.Equal(("build-01", 259200L, true, true), (p.Target, p.OlderThanSeconds!.Value, p.Caches, p.Yes));
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["run", "--caches", "--", "dotnet", "test"]));
        Assert.Throws<ArgumentException>(() => ParsedArgs.Parse(["prune", "--older-than", "1w"]));
    }

    [Fact]
    public void OnlyLanqsIdShapeIsEverPruned()
    {
        Assert.Matches(RemoteScripts.PruneIdRegex(), OldId);
        foreach (var bad in new[] { "backup", "j1", "20261101-101010-0A0B0C", "20261101-101010-0a0b0c\n", "../evil", "20261101-101010-0a0b0", "x20261101-101010-0a0b0c" })
        {
            Assert.DoesNotMatch(RemoteScripts.PruneIdRegex(), bad);
        }
    }

    [Fact]
    public void TheScriptsGuardPathsAndLinks()
    {
        var linux = new Machine { Name = "l", Os = MachineOs.Linux, WorkRoot = "/srv/lq" };
        var windows = new Machine { Name = "w", Os = MachineOs.Windows, WorkRoot = "D:/lq" };
        var lf = RemoteScripts.Prune(linux, PrunePhase.Files, 3600, true);
        Assert.Contains("if [ -L \"$RR/$d\" ]; then echo \"REFUSED $d is a link\"", lf, StringComparison.Ordinal);
        Assert.Contains("REFUSED work_root is the filesystem root or the home folder", lf, StringComparison.Ordinal);
        Assert.Contains("REFUSED no locks folder", lf, StringComparison.Ordinal);
        Assert.Contains("if [ -L \"$3\" ]; then rm -f \"$3\"; else rm -rf \"$3\"; fi", lf, StringComparison.Ordinal);
        var wf = RemoteScripts.Prune(windows, PrunePhase.Files, 3600, true);
        Assert.Contains("[IO.Directory]::Delete($p, $false)", wf, StringComparison.Ordinal);
        Assert.Contains("cmd /c rd /s /q \"\\\\?\\$p\"", wf, StringComparison.Ordinal);
        Assert.Contains("REFUSED work_root is a drive root, a share root or the home folder", wf, StringComparison.Ordinal);
        Assert.Contains(@"$x -match '^\\\\[^\\]+\\[^\\]+\z'", wf, StringComparison.Ordinal); // B-L7: \\server\share too
        Assert.Contains("[IO.Path]::Combine($wi.Parent.FullName, @($wi.Target)[0])", wf, StringComparison.Ordinal); // a relative link target
        // The cache phase names only ids of lanq's shape; anything else given is dropped.
        var lc = RemoteScripts.Prune(linux, PrunePhase.Caches, 3600, true, prunedIds: [OldId, "x;rm -rf /"]);
        Assert.Contains("PIDS=\"" + OldId + "\"", lc, StringComparison.Ordinal);
        Assert.DoesNotContain("rm -rf /", lc, StringComparison.Ordinal);
        Assert.Contains("worktree prune", RemoteScripts.Prune(windows, PrunePhase.Caches, 3600, true), StringComparison.Ordinal);
    }

    // ---- the real script on this OS (Linux runner: symlinks; Windows runner: junctions) ----

    private string Work => _root + "/wr";

    private string Outside => _root + "/outside";

    private static void Age(string path) => Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-3));

    private void Link(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(0, Shells.Exec("cmd", ["/c", "mklink", "/J", link.Replace('/', '\\'), target.Replace('/', '\\')]).Code);
        }
        else
        {
            File.CreateSymbolicLink(link, target);
        }
    }

    private void BuildWorkRoot()
    {
        foreach (var d in new[] { "locks", "incoming", "repos", "jobs/" + OldId, "jobs/" + FreshId, "jobs/" + LiveId, "jobs/" + StaleId, "jobs/backup" })
        {
            Directory.CreateDirectory(Work + "/" + d);
        }

        File.WriteAllText(Work + "/jobs/" + OldId + "/out.txt", new string('x', 5000));
        Directory.CreateDirectory(Outside);
        File.WriteAllText(Outside + "/keep.txt", "keep");
        Link(Work + "/jobs/" + LinkId, Outside);
        File.WriteAllText(Work + "/locks/slot-1", LiveId + "\n" + (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600) + "\n");
        File.WriteAllText(Work + "/locks/slot-2", StaleId + "\n1000\n"); // stale: still never pruned (lanq unlock decides)
        File.WriteAllText(Work + "/incoming/" + OldId + ".bundle", "b");
        File.WriteAllText(Work + "/incoming/notes.bundle", "b");
        foreach (var d in new[] { OldId, LiveId, StaleId, "backup" })
        {
            Age(Work + "/jobs/" + d);
        }

        File.SetLastWriteTimeUtc(Work + "/incoming/" + OldId + ".bundle", DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(Work + "/incoming/notes.bundle", DateTime.UtcNow.AddDays(-3));
    }

    private Machine Here => new() { Name = "here", Os = OperatingSystem.IsWindows() ? MachineOs.Windows : MachineOs.Linux, WorkRoot = Work };

    private (int Code, string Output) Run(string script) => Shells.Run(OperatingSystem.IsWindows() ? "windows" : "linux", script);

    [Fact]
    public void TheRealScriptListsThenRemovesOnlyOldUnnamedIdsAndNeverFollowsALink()
    {
        BuildWorkRoot();
        var (code, listed) = Run(RemoteScripts.Prune(Here, PrunePhase.Files, 3600, remove: false));
        Assert.True(code == 0, listed);
        Assert.Contains("SLOTFILES=2", listed, StringComparison.Ordinal);
        Assert.Contains("WOULD job " + OldId + " ", listed, StringComparison.Ordinal);
        Assert.Contains("WOULD incoming " + OldId + ".bundle ", listed, StringComparison.Ordinal);
        Assert.Contains("SKIP " + LiveId + " live", listed, StringComparison.Ordinal);
        Assert.Contains("SKIP " + StaleId + " live", listed, StringComparison.Ordinal);
        Assert.DoesNotContain(FreshId, listed, StringComparison.Ordinal);
        Assert.DoesNotContain("backup", listed, StringComparison.Ordinal);
        Assert.DoesNotContain("notes", listed, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Work + "/jobs/" + OldId)); // listing removes nothing

        // Age 0 so the link (whose own time cannot be set portably) counts as old too: it goes as a link, the folder it names stays.
        var (code2, removed) = Run(RemoteScripts.Prune(Here, PrunePhase.Files, 0, remove: true));
        Assert.True(code2 == 0, removed);
        Assert.Contains("REMOVED job " + OldId + " ", removed, StringComparison.Ordinal);
        Assert.Contains("REMOVED job " + LinkId + " 0", removed, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Work + "/jobs/" + OldId));
        Assert.False(Directory.Exists(Work + "/jobs/" + LinkId));
        Assert.True(File.Exists(Outside + "/keep.txt"));
        Assert.True(Directory.Exists(Work + "/jobs/" + LiveId));
        Assert.True(Directory.Exists(Work + "/jobs/" + StaleId));
        Assert.True(Directory.Exists(Work + "/jobs/backup"));
        Assert.True(File.Exists(Work + "/incoming/notes.bundle"));
    }

    [Fact]
    public void ALinkedJobsFolderOrAMissingLocksFolderIsRefused()
    {
        Directory.CreateDirectory(Work);
        Directory.CreateDirectory(Outside);
        File.WriteAllText(Outside + "/keep.txt", "keep");
        var (_, noLocks) = Run(RemoteScripts.Prune(Here, PrunePhase.Files, 0, remove: true));
        Assert.Contains("REFUSED no locks folder", noLocks, StringComparison.Ordinal);

        Directory.CreateDirectory(Work + "/locks");
        Link(Work + "/jobs", Outside);
        var (_, linked) = Run(RemoteScripts.Prune(Here, PrunePhase.Files, 0, remove: true));
        Assert.Contains("REFUSED jobs is a link", linked, StringComparison.Ordinal);
        Assert.True(File.Exists(Outside + "/keep.txt"));
    }

    [Fact]
    public void ARelativeLinkWorkRootIsResolvedFromItsOwnFolder()
    {
        // B-L7: work_root is a link with a relative target ("real", next to it). The script works through the link and resolves the target
        // against the link's own folder, never the session's current folder.
        Directory.CreateDirectory(_root + "/real/locks");
        Directory.CreateDirectory(_root + "/real/jobs/" + OldId);
        Age(_root + "/real/jobs/" + OldId);
        var link = _root + "/wr";
        if (OperatingSystem.IsWindows())
        {
            if (Shells.Exec("cmd", ["/c", "mklink", "/D", link.Replace('/', '\\'), "real"]).Code != 0)
            {
                return; // no right to make a directory symlink here (not elevated, no developer mode): the Windows CI runner covers it
            }
        }
        else
        {
            Directory.CreateSymbolicLink(link, "real");
        }

        var (code, listed) = Run(RemoteScripts.Prune(Here, PrunePhase.Files, 3600, remove: false));
        Assert.True(code == 0, listed);
        Assert.DoesNotContain("REFUSED", listed, StringComparison.Ordinal);
        Assert.Contains("WOULD job " + OldId + " ", listed, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRealCachesPhaseWorksOnARealBareRepository()
    {
        // B-L10 (and B-L8): a real cache whose only worktree belongs to an old job, and a fresh cache. The listing names the old cache as one
        // that would go (its worktree belongs to a job the same listing names); --yes removes the job, prunes the worktree, deletes
        // refs/lanq/<id> and the old cache, and keeps the fresh one.
        string Git(string dir, params string[] args)
        {
            var (c, o) = Shells.Exec("git", ["-C", dir, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "-c", "commit.gpgsign=false", .. args]);
            Assert.True(c == 0, string.Join(' ', args) + ": " + o);
            return o;
        }

        var src = _root + "/src";
        Directory.CreateDirectory(src);
        Git(src, "init", "-q");
        File.WriteAllText(src + "/a.txt", "a");
        Git(src, "add", "a.txt");
        Git(src, "commit", "-q", "-m", "a");
        Directory.CreateDirectory(Work + "/locks");
        Directory.CreateDirectory(Work + "/jobs");
        const string Old = "0123456789abcdef.git";
        const string Fresh = "fedcba9876543210.git";
        foreach (var c in new[] { Old, Fresh })
        {
            Git(_root, "clone", "-q", "--bare", src, Work + "/repos/" + c);
        }

        var cache = Work + "/repos/" + Old;
        Git(cache, "worktree", "add", "-q", "--detach", Work + "/jobs/" + OldId, "HEAD");
        Git(cache, "update-ref", "refs/lanq/" + OldId, "HEAD");
        Age(Work + "/jobs/" + OldId);
        foreach (var f in new[] { "packed-refs", "FETCH_HEAD" })
        {
            File.WriteAllText(cache + "/" + f, File.Exists(cache + "/" + f) ? File.ReadAllText(cache + "/" + f) : "");
            File.SetLastWriteTimeUtc(cache + "/" + f, DateTime.UtcNow.AddDays(-40));
        }

        var (code, listed) = Run(RemoteScripts.Prune(Here, PrunePhase.Caches, 3600, false, caches: true, cacheAgeSeconds: 86400, prunedIds: [OldId]));
        Assert.True(code == 0, listed);
        Assert.Contains("WOULD cache " + Old, listed, StringComparison.Ordinal);
        Assert.DoesNotContain(Fresh, listed, StringComparison.Ordinal);
        Assert.True(Directory.Exists(cache)); // a listing removes nothing

        var (code2, files) = Run(RemoteScripts.Prune(Here, PrunePhase.Files, 3600, remove: true));
        Assert.True(code2 == 0, files);
        Assert.False(Directory.Exists(Work + "/jobs/" + OldId));
        var (code3, removed) = Run(RemoteScripts.Prune(Here, PrunePhase.Caches, 3600, true, caches: true, cacheAgeSeconds: 86400, prunedIds: [OldId]));
        Assert.True(code3 == 0, removed);
        Assert.Contains("REMOVED cache " + Old, removed, StringComparison.Ordinal);
        Assert.False(Directory.Exists(cache));
        Assert.True(Directory.Exists(Work + "/repos/" + Fresh));
        Assert.DoesNotContain(Fresh, removed, StringComparison.Ordinal);
    }

    // ---- the command ----

    private const string Fleet = """
        policy:
          allowed_commands: ["dotnet test"]
        machines:
          - name: build-01
            os: linux
            ssh: user@10.20.30.11
            work_root: /srv/lanq
            capabilities: [dotnet]
            max_jobs: 2
        """;

    private static FakeSsh Ssh(int slotFiles, bool paused = false) => new()
    {
        Handler = (_, cmd) => cmd.Contains("disk_free_gb", StringComparison.Ordinal)
            ? new SshResult(0, $"load=0.1\ndisk_free_gb=50\nslots_used=0\ntool=ok\npaused={(paused ? 1 : 0)}")
            : cmd.Contains("SLOTS=", StringComparison.Ordinal) ? new SshResult(0, "SLOTS=1,2") : new SshResult(0, ""),
        InputHandler = (_, _, input) =>
        {
            var script = Encoding.UTF8.GetString(input);
            return script.Contains("worktree prune", StringComparison.Ordinal)
                ? new SshResult(0, "WOULD cache 0123456789abcdef.git 4096\nREMOVED cache 0123456789abcdef.git 4096\n")
                : new SshResult(0, $"SLOTFILES={slotFiles}\n{(script.Contains("DO=1", StringComparison.Ordinal) ? "REMOVED" : "WOULD")} job {OldId} 8192\nSKIP {LiveId} live\n");
        },
    };

    private static async Task<(int Code, JsonElement Json, FakeSsh Ssh)> Prune(FakeSsh ssh, params string[] args)
    {
        var (o, e) = (new StringWriter(), new StringWriter());
        var code = await Cli.Prune(ParsedArgs.Parse(["prune", .. args, "--json"]), ConfigLoader.Parse(Fleet), ssh, new HealthProbe(ssh, TimeProvider.System), o, e, CancellationToken.None);
        return (code, JsonDocument.Parse(o.ToString()).RootElement, ssh);
    }

    [Fact]
    public async Task WithoutYesItOnlyListsAndNeverClaims()
    {
        var (code, json, ssh) = await Prune(Ssh(0), "build-01");
        Assert.Equal(0, code);
        Assert.Equal("Listed", json.GetProperty("status").GetString());
        var row = json.GetProperty("machines")[0];
        Assert.Equal(OldId, row.GetProperty("wouldRemove")[0].GetProperty("name").GetString());
        Assert.False(row.TryGetProperty("removed", out _));
        Assert.DoesNotContain(ssh.Calls, c => c.Command.Contains("SLOTS=", StringComparison.Ordinal));
        Assert.Equal("ask_developer", json.GetProperty("nextSteps")[0].GetProperty("action").GetString());
    }

    [Fact]
    public async Task WithYesCachesAreTouchedOnlyWhileHoldingEverySlot()
    {
        var (code, json, ssh) = await Prune(Ssh(0), "build-01", "--yes", "--caches");
        Assert.Equal(0, code);
        var row = json.GetProperty("machines")[0];
        Assert.Contains(row.GetProperty("removed").EnumerateArray(), e => e.GetProperty("kind").GetString() == "cache");
        var claim = ssh.Calls.FindIndex(c => c.Command.Contains("SLOTS=", StringComparison.Ordinal));
        var release = ssh.Calls.FindIndex(c => c.Command.Contains(RemoteScripts.ReleaseClaimMarker, StringComparison.Ordinal));
        Assert.True(claim >= 0 && release > claim);
        Assert.Contains(ssh.InputRuns, r => Encoding.UTF8.GetString(r.Input).Contains("update-ref -d \"refs/lanq/$id\"", StringComparison.Ordinal)
            && Encoding.UTF8.GetString(r.Input).Contains(OldId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASlotInUseLeavesTheCachesAlone()
    {
        var (code, json, ssh) = await Prune(Ssh(1), "build-01", "--yes", "--caches");
        Assert.Equal(0, code);
        Assert.Contains(json.GetProperty("machines")[0].GetProperty("skipped").EnumerateArray(), s => s.GetProperty("name").GetString() == "repos" && s.GetProperty("why").GetString() == "busy");
        Assert.DoesNotContain(ssh.Calls, c => c.Command.Contains("SLOTS=", StringComparison.Ordinal));
        Assert.DoesNotContain(ssh.InputRuns, r => Encoding.UTF8.GetString(r.Input).Contains("worktree prune", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AHeldMachineIsSkippedUnlessForced()
    {
        var (code, json, _) = await Prune(Ssh(0, paused: true), "--all");
        Assert.Equal(75, code);
        Assert.Contains("--force", json.GetProperty("machines")[0].GetProperty("note").GetString(), StringComparison.Ordinal);
        (code, _, _) = await Prune(Ssh(0, paused: true), "--all", "--force");
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task NeitherOrBothTargetsIsAnArgumentError()
    {
        var (code, json, _) = await Prune(Ssh(0));
        Assert.Equal(2, code);
        Assert.Equal("Rejected", json.GetProperty("status").GetString());
    }
}
