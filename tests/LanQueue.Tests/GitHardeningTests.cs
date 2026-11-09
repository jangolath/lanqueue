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
