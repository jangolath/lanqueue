using System.Text.RegularExpressions;
using LanQueue.Config;
using LanQueue.Jobs;
using LanQueue.Payload;
using Xunit;

namespace LanQueue.Tests;

public sealed partial class GitHardeningTests
{
    private const string Job = "20260101-120000-bbbbbb";

    [GeneratedRegex(@"(?<![\w./$-])git\s", RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex GitCall();

    public static TheoryData<string, string> GitScripts()
    {
        var data = new TheoryData<string, string>();
        foreach (var m in new[] { T.Linux(), T.Windows() })
        {
            data.Add(m.Os + " prepare", RemoteScripts.Prepare(m, "j", T.RepoId));
            data.Add(m.Os + " cleanup", RemoteScripts.Cleanup(m, "j", "1", T.RepoId));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(GitScripts))]
    public void EveryGitCallGoesThroughTheHardenedWrapper(string name, string script)
    {
        var calls = GitCall().Matches(script).Select(m => script[m.Index..].Split('\n')[0]).ToList();
        var nul = name.StartsWith(nameof(MachineOs.Windows), StringComparison.Ordinal) ? "NUL" : "/dev/null";
        Assert.True(calls.Count == 1, $"{name}: git must be called only from its wrapper, found: {string.Join(" | ", calls)}");
        Assert.Contains("-c core.hooksPath=" + nul, calls[0], StringComparison.Ordinal);
        Assert.Contains("-c core.fsmonitor=false", calls[0], StringComparison.Ordinal);
        Assert.Contains("-c protocol.allow=never", calls[0], StringComparison.Ordinal);
        Assert.Contains("-c protocol.file.allow=never", calls[0], StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(GitScripts))]
    public void OnlyTheBundleFetchMayReadALocalFile(string name, string script)
    {
        foreach (var line in script.Split('\n').Where(l => l.Contains(" fetch ", StringComparison.Ordinal)))
        {
            Assert.Contains("-c protocol.file.allow=always", line, StringComparison.Ordinal);
            Assert.Contains("/incoming/", line, StringComparison.Ordinal);
        }

        Assert.Equal(
            name.EndsWith("prepare", StringComparison.Ordinal) ? 1 : 0,
            script.Split('\n').Count(l => l.Contains("protocol.file.allow=always", StringComparison.Ordinal)));
    }

    [Theory]
    [MemberData(nameof(GitScripts))]
    public void EveryGitCallIgnoresSystemAndGlobalConfigAndAttributeFiles(string name, string script)
    {
        var windows = name.StartsWith(nameof(MachineOs.Windows), StringComparison.Ordinal);
        var call = GitCall().Matches(script).Select(m => script[m.Index..].Split('\n')[0]).Single();
        Assert.Contains("-c core.attributesFile=" + (windows ? "NUL" : "/dev/null"), call, StringComparison.Ordinal);
        if (windows)
        {
            Assert.Contains("$env:GIT_CONFIG_NOSYSTEM = '1'", script, StringComparison.Ordinal);
            Assert.Contains("$env:GIT_CONFIG_GLOBAL = 'NUL'", script, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("GIT_CONFIG_NOSYSTEM=1 GIT_CONFIG_GLOBAL=/dev/null git ", script, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// git follows a "commondir" (or "gitdir") file even in a main bare repository and then takes its config, info/attributes and objects
    /// from the directory it names: such a file planted in the cache must make prepare recreate the cache, and nothing may run.
    /// </summary>
    [Theory]
    [InlineData("commondir", true)] // the evil repo's config defines the filter, its info/attributes applies it
    [InlineData("commondir", false)] // only the evil repo's info/attributes; the filter comes from its config as well
    [InlineData("gitdir", true)]
    public async Task ACommonDirPlantedInTheCacheNeverRuns(string file, bool filterInConfig)
    {
        using var repo = new TempRepo();
        using var worker = new LocalWorker();
        var marker = Path.Combine(worker.Root, "commondir-ran").Replace('\\', '/');
        var evil = Path.Combine(worker.Root, "evil.git").Replace('\\', '/');
        repo.Git("init", "-q", "--bare", evil);
        repo.Git("config", "--file", evil + "/config", "filter.evil.smudge", Smudge(marker));
        if (filterInConfig)
        {
            repo.Git("config", "--file", evil + "/config", "filter.evil.required", "false");
        }

        Directory.CreateDirectory(evil + "/info");
        File.WriteAllText(evil + "/info/attributes", "* filter=evil\n");

        var (code, output, cache) = await PrepareWithPlantedCache(repo, worker, c => File.WriteAllText(c + "/" + file, evil + "\n"));

        Assert.True(code == 0, output);
        Assert.False(File.Exists(marker), "a filter reached through a planted " + file + " ran");
        Assert.Contains("unexpected", output, StringComparison.Ordinal);
        Assert.False(File.Exists(cache + "/" + file));
    }

    [Fact]
    public async Task AnUnknownTopLevelEntryInTheCacheRecreatesIt()
    {
        using var repo = new TempRepo();
        using var worker = new LocalWorker();
        var (code, output, cache) = await PrepareWithPlantedCache(repo, worker, c => File.WriteAllText(c + "/planted", "x"));
        Assert.True(code == 0, output);
        Assert.Contains("unexpected", output, StringComparison.Ordinal);
        Assert.False(File.Exists(cache + "/planted"));
    }

    /// <summary>The cache's own worktrees/*/commondir files (written by git for every job worktree) are legitimate and keep the cache.</summary>
    /// <summary>
    /// A cache with the reftable ref backend (git 2.45+, the planned git 3.0 default) has a top-level reftable/ directory and
    /// extensions.refstorage: it is a legitimate cache and must survive prepare, or incremental bundles stop working.
    /// </summary>
    [ReftableGitFact]
    public async Task AReftableCacheIsKept()
    {
        using var repo = new TempRepo();
        using var worker = new LocalWorker();
        var (code, output, cache) = await PrepareWithPlantedCache(repo, worker, c =>
        {
            Directory.Delete(c, recursive: true);
            repo.Git("init", "-q", "--bare", "--ref-format=reftable", c);
            File.WriteAllText(c + "/ORIG_HEAD", new string('0', 40) + "\n"); // also written by git itself in some flows
        });

        Assert.True(code == 0, output);
        Assert.DoesNotContain("unexpected", output, StringComparison.Ordinal);
        Assert.True(Directory.Exists(cache + "/reftable"));
    }

    [Fact]
    public async Task ACacheWithAJobWorktreeIsKept()
    {
        using var repo = new TempRepo();
        using var worker = new LocalWorker();
        var (code, output, cache) = await PrepareWithPlantedCache(repo, worker, _ => { });
        Assert.True(code == 0, output);
        Assert.True(Directory.EnumerateFiles(cache + "/worktrees", "commondir", SearchOption.AllDirectories).Any());

        const string Second = "20260101-120000-cccccc";
        var sha = repo.Git("rev-parse", "HEAD").Trim();
        File.Copy((await new GitPayloadBuilder().BuildBundleAsync(Second, repo.Dir, repo.Out, sha, null, CancellationToken.None)).Path, Path.Combine(worker.Root, "incoming", Second + ".bundle"));
        (code, output) = Shells.Run(OperatingSystem.IsWindows() ? "windows" : "linux", RemoteScripts.Prepare(worker.Machine, Second, Path.GetFileName(cache)[..16]));
        Assert.True(code == 0, output);
        Assert.DoesNotContain("unexpected", output, StringComparison.Ordinal);
    }

    /// <summary>Prepares a worker cache for <paramref name="repo"/>'s snapshot, lets <paramref name="plant"/> tamper with it, runs prepare.</summary>
    private static async Task<(int Code, string Output, string Cache)> PrepareWithPlantedCache(
        TempRepo repo, LocalWorker worker, Action<string> plant, IReadOnlyDictionary<string, string>? env = null)
    {
        File.WriteAllText(Path.Combine(repo.Dir, "b.txt"), "content");
        repo.Commit("b.txt", "content", "file for checkout");
        var p = await new GitPayloadBuilder().BuildAsync(Job, repo.Dir, repo.Out, PayloadKinds.Bundle, CancellationToken.None);
        var cache = Path.Combine(worker.Root, "repos", p.RepoId + ".git").Replace('\\', '/');
        repo.Git("init", "-q", "--bare", cache);
        plant(cache);
        File.Copy((await new GitPayloadBuilder().BuildBundleAsync(Job, repo.Dir, repo.Out, p.Sha, null, CancellationToken.None)).Path, Path.Combine(worker.Root, "incoming", Job + ".bundle"));
        var (code, output) = Shells.Run(OperatingSystem.IsWindows() ? "windows" : "linux", RemoteScripts.Prepare(worker.Machine, Job, p.RepoId), env);
        return (code, output, cache);
    }

    private static string Smudge(string marker) => "touch '" + marker + "'; cat";

    [Fact]
    public async Task ASmudgeFilterPlantedInTheCacheConfigAndInfoAttributesNeverRuns()
    {
        using var repo = new TempRepo();
        using var worker = new LocalWorker();
        var marker = Path.Combine(worker.Root, "filter-ran").Replace('\\', '/');

        var (code, output, cache) = await PrepareWithPlantedCache(repo, worker, c =>
        {
            repo.Git("config", "--file", c + "/config", "filter.evil.smudge", Smudge(marker));
            Directory.CreateDirectory(c + "/info");
            File.WriteAllText(c + "/info/attributes", "* filter=evil\n");
        });

        Assert.True(code == 0, output);
        Assert.False(File.Exists(marker), "a smudge filter planted in the worker cache ran");
        Assert.Contains("unexpected config keys", output, StringComparison.Ordinal);
        Assert.DoesNotContain("filter.", repo.Git("config", "--file", cache + "/config", "--name-only", "--list"), StringComparison.Ordinal);
        Assert.Equal("content", File.ReadAllText(Path.Combine(worker.Root, "jobs", Job, "b.txt")));
    }

    [Fact]
    public async Task AnInfoAttributesFileAloneIsRemoved()
    {
        using var repo = new TempRepo();
        using var worker = new LocalWorker();

        var (code, output, cache) = await PrepareWithPlantedCache(repo, worker, c =>
        {
            Directory.CreateDirectory(c + "/info");
            File.WriteAllText(c + "/info/attributes", "* -text\n");
        });

        Assert.True(code == 0, output);
        Assert.DoesNotContain("unexpected config keys", output, StringComparison.Ordinal); // a clean config keeps its cache
        Assert.False(File.Exists(cache + "/info/attributes"));
    }

    [Fact]
    public async Task AnIncludePathPlantedInTheCacheConfigNeverRuns()
    {
        using var repo = new TempRepo();
        using var worker = new LocalWorker();
        var marker = Path.Combine(worker.Root, "include-ran").Replace('\\', '/');
        var evil = Path.Combine(worker.Root, "evil.config").Replace('\\', '/');
        var attributes = Path.Combine(worker.Root, "evil.attributes").Replace('\\', '/');
        File.WriteAllText(attributes, "* filter=evil\n");
        repo.Git("config", "--file", evil, "filter.evil.smudge", Smudge(marker));
        repo.Git("config", "--file", evil, "core.attributesFile", attributes);

        var (code, output, _) = await PrepareWithPlantedCache(repo, worker, c => repo.Git("config", "--file", c + "/config", "include.path", evil));

        Assert.True(code == 0, output);
        Assert.False(File.Exists(marker), "a filter from an include.path planted in the worker cache ran");
        Assert.Contains("unexpected config keys", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheWorkerUsersGlobalAndSystemConfigAreNotRead()
    {
        using var repo = new TempRepo();
        using var worker = new LocalWorker();
        var marker = Path.Combine(worker.Root, "global-ran").Replace('\\', '/');
        var global = Path.Combine(worker.Root, "global.gitconfig").Replace('\\', '/');
        var attributes = Path.Combine(worker.Root, "global.attributes").Replace('\\', '/');
        File.WriteAllText(attributes, "* filter=evil\n");
        repo.Git("config", "--file", global, "filter.evil.smudge", Smudge(marker));
        repo.Git("config", "--file", global, "core.attributesFile", attributes);

        var (code, output, _) = await PrepareWithPlantedCache(
            repo, worker, _ => { }, new Dictionary<string, string> { ["GIT_CONFIG_GLOBAL"] = global, ["GIT_CONFIG_SYSTEM"] = global });

        Assert.True(code == 0, output);
        Assert.False(File.Exists(marker), "a filter from the worker user's global or system git config ran");
    }

    [Fact]
    public async Task HooksPlantedInTheWorkerCacheNeverRun()
    {
        using var repo = new TempRepo();
        using var worker = new LocalWorker();
        var p = await new GitPayloadBuilder().BuildAsync(Job, repo.Dir, repo.Out, PayloadKinds.Bundle, CancellationToken.None);
        var cache = Path.Combine(worker.Root, "repos", p.RepoId + ".git");
        var marker = Path.Combine(worker.Root, "hook-ran").Replace('\\', '/');
        repo.Git("init", "-q", "--bare", cache);
        foreach (var hook in new[] { "post-checkout", "reference-transaction", "pre-auto-gc" })
        {
            var file = Path.Combine(cache, "hooks", hook);
            File.WriteAllText(file, $"#!/bin/sh\necho {hook} >> '{marker}'\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        File.Copy((await new GitPayloadBuilder().BuildBundleAsync(Job, repo.Dir, repo.Out, p.Sha, null, CancellationToken.None)).Path, Path.Combine(worker.Root, "incoming", Job + ".bundle"));
        var (code, output) = worker.Run(RemoteScripts.Prepare(worker.Machine, Job, p.RepoId));
        Assert.True(code == 0, output);
        (code, output) = worker.Run(RemoteScripts.Cleanup(worker.Machine, Job, "1", p.RepoId));
        Assert.True(code == 0, output);

        Assert.True(Directory.Exists(Path.Combine(cache, "refs")));
        Assert.False(File.Exists(marker), "a hook in the worker cache ran: " + (File.Exists(marker) ? File.ReadAllText(marker) : ""));
    }
}
