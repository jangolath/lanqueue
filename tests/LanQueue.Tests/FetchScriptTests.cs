using System.Text;
using LanQueue.Config;
using LanQueue.Fetch;
using LanQueue.Jobs;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// The worker side of --fetch, run by the real shells (sh, and Windows PowerShell 5.1) against a job folder in the temp folder, with the
/// output read by the real line reader and receiver. The hostile cases (links, loops, odd names, devices) need a real Linux file system.
/// </summary>
public sealed class FetchScriptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lanq-fetchsh-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');

    public FetchScriptTests() => Directory.CreateDirectory(Job);

    private string Job => _root + "/jobs/j1";

    private string Out => _root + "/out/j1";

    /// <summary>Never enumerate this tree recursively: the symlink loops some tests plant would make that walk effectively endless.</summary>
    public void Dispose() => TestTree.Delete(_root);

    private Machine Worker(MachineOs os)
    {
        var m = os == MachineOs.Windows ? T.Windows() : T.Linux();
        m.WorkRoot = _root;
        return m;
    }

    private void Write(string rel, byte[] data)
    {
        var p = Path.Combine(Job, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, data);
    }

    private static byte[] AllBytes(int n) => Enumerable.Range(0, n).Select(i => (byte)(i * 7 % 256)).ToArray();

    private async Task<FetchReport> Fetch(MachineOs os, string[] patterns, FetchLimits? limits = null, string dir = ".")
    {
        var m = Worker(os);
        var script = FetchScripts.Fetch(m, "j1", dir, patterns, limits ?? FetchLimits.Default);
        var (_, output) = Shells.Run(os == MachineOs.Windows ? "windows" : "linux", script);
        using var rx = new FetchReceiver(Out, patterns, limits ?? FetchLimits.Default, ignoreCase: os == MachineOs.Windows);
        await LineReader.PumpAsync(new StringReader(output), rx.OnLine);
        return rx.Finish();
    }

    private void AssertRoundTrip(FetchReport r, params string[] rels)
    {
        Assert.Null(r.Error);
        Assert.Equal(rels, r.Fetched.Select(f => f.Path));
        foreach (var rel in rels)
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(Job, rel)), File.ReadAllBytes(Path.Combine(Out, rel)));
        }
    }

    // ---- sh (Linux workers; on Windows the Git for Windows sh) ----

    [ShFact]
    public async Task ShFetchesMatchingFilesByteForByteInSortedOrder()
    {
        Write("TestResults/b/run.trx", AllBytes(5000));
        Write("TestResults/a.trx", AllBytes(1));
        Write("TestResults/empty.trx", []);
        Write("TestResults/notes.txt", AllBytes(3));
        Write("src/app.cs", AllBytes(3));

        var r = await Fetch(MachineOs.Linux, ["TestResults/**/*.trx"]);

        AssertRoundTrip(r, "TestResults/a.trx", "TestResults/b/run.trx", "TestResults/empty.trx");
        Assert.Empty(r.Skipped);
    }

    [ShFact]
    public async Task ShStarStaysInOneFolderAndPatternsAreRelativeToTheWorkingFolder()
    {
        Write("src/app/out/x.log", AllBytes(3));
        Write("src/app/out/deep/y.log", AllBytes(3));
        Write("src/z.log", AllBytes(3));

        var r = await Fetch(MachineOs.Linux, ["out/*.log"], dir: "src/app");

        Assert.Null(r.Error);
        Assert.Equal(["out/x.log"], r.Fetched.Select(f => f.Path));
        Assert.Equal(AllBytes(3), File.ReadAllBytes(Path.Combine(Out, "out", "x.log")));
    }

    [ShFact]
    public async Task ShCapsFetchWhatFitsInSortedOrderAndReportTheRest()
    {
        Write("o/a.bin", AllBytes(40));
        Write("o/b.bin", AllBytes(40));
        Write("o/c.bin", AllBytes(10));
        Write("o/d.bin", AllBytes(10));
        Write("o/e.bin", AllBytes(10));
        Write("o/huge.bin", AllBytes(200));

        var r = await Fetch(MachineOs.Linux, ["o/*"], new FetchLimits(3, 100, 100));

        Assert.Equal(["o/a.bin", "o/b.bin", "o/c.bin"], r.Fetched.Select(f => f.Path));
        Assert.Equal(["o/d.bin", "o/e.bin", "o/huge.bin"], r.Skipped.Select(s => s.Path));
        Assert.Equal("over the file count cap", r.Skipped[0].Reason);
    }

    [ShFact]
    public async Task ShReportsAMissingWorkingFolder()
    {
        var r = await Fetch(MachineOs.Linux, ["*.trx"], dir: "nope");
        Assert.Empty(r.Fetched);
        Assert.Contains("could not list", r.Error, StringComparison.Ordinal);
    }

    [LinuxFact]
    public async Task ShSkipsSymlinksToFilesOutsideAndNeverFollowsThem()
    {
        var outside = _root + "/outside.txt";
        File.WriteAllText(outside, "worker secret");
        Write("TestResults/real.trx", AllBytes(10));
        File.CreateSymbolicLink(Job + "/TestResults/passwd.trx", "/etc/passwd");
        File.CreateSymbolicLink(Job + "/TestResults/outside.trx", outside);
        File.CreateSymbolicLink(Job + "/TestResults/inside.trx", Job + "/TestResults/real.trx");

        var r = await Fetch(MachineOs.Linux, ["TestResults/*.trx"]);

        AssertRoundTrip(r, "TestResults/real.trx");
        Assert.Equal(["TestResults/inside.trx", "TestResults/outside.trx", "TestResults/passwd.trx"], r.Skipped.Select(s => s.Path));
        Assert.All(r.Skipped, s => Assert.Contains("symbolic link", s.Reason, StringComparison.Ordinal));
        Assert.False(File.Exists(Out + "/TestResults/passwd.trx"));
    }

    [LinuxFact]
    public async Task ShDoesNotFollowSymlinkedFoldersOrLoops()
    {
        Directory.CreateDirectory(_root + "/elsewhere");
        File.WriteAllText(_root + "/elsewhere/leak.trx", "worker secret");
        Write("out/a.trx", AllBytes(5));
        Directory.CreateSymbolicLink(Job + "/out/loop", Job + "/out");
        Directory.CreateSymbolicLink(Job + "/out/up", "..");
        Directory.CreateSymbolicLink(Job + "/out/elsewhere", _root + "/elsewhere");
        Directory.CreateSymbolicLink(Job + "/linked", _root + "/elsewhere");

        var r = await Fetch(MachineOs.Linux, ["**/*.trx", "linked/*.trx"]);

        AssertRoundTrip(r, "out/a.trx");
        Assert.DoesNotContain(r.Skipped, s => s.Path.Contains("leak", StringComparison.Ordinal));
        Assert.False(File.Exists(Out + "/out/elsewhere/leak.trx"));
    }

    [LinuxFact]
    public async Task ShRefusesAWalkRootThatIsASymlink()
    {
        Directory.CreateDirectory(_root + "/elsewhere");
        File.WriteAllText(_root + "/elsewhere/leak.trx", "worker secret");
        Directory.CreateSymbolicLink(Job + "/TestResults", _root + "/elsewhere");

        var r = await Fetch(MachineOs.Linux, ["TestResults/*.trx"]);

        Assert.Empty(r.Fetched);
        Assert.Equal("TestResults", Assert.Single(r.Skipped).Path);
    }

    [LinuxFact]
    public async Task ShSkipsNamesWithControlCharactersHardLinksAndFifos()
    {
        Write("o/ok.bin", AllBytes(5));
        Write("o/evil\nname.bin", AllBytes(5));
        Write("o/source.bin", AllBytes(5));
        Directory.CreateDirectory(Job + "/o/dir\nx");
        File.WriteAllText(Job + "/o/dir\nx/inner.bin", "x");
        var (code, output) = Shells.Exec("sh", ["-c", "cd \"$1\" && ln o/source.bin o/hard.bin && mkfifo o/pipe.bin", "sh", Job]);
        Assert.True(code == 0, output);

        var r = await Fetch(MachineOs.Linux, ["o/**"]);

        Assert.Equal(["o/ok.bin"], r.Fetched.Select(f => f.Path));
        Assert.Contains(r.Skipped, s => s.Path == "o/evil?name.bin" && s.Reason.Contains("control", StringComparison.Ordinal));
        Assert.Contains(r.Skipped, s => s.Path == "o/dir?x" && s.Reason.Contains("control", StringComparison.Ordinal));
        Assert.Contains(r.Skipped, s => s.Path == "o/hard.bin" && s.Reason.Contains("hard link", StringComparison.Ordinal));
        Assert.Contains(r.Skipped, s => s.Path == "o/source.bin" && s.Reason.Contains("hard link", StringComparison.Ordinal));
        Assert.Contains(r.Skipped, s => s.Path == "o/pipe.bin" && s.Reason.Contains("not a regular file", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Skipped, s => s.Path.Contains("inner", StringComparison.Ordinal));
    }

    [LinuxFact]
    public async Task ShRefusesAWorkingFolderThatResolvesOutsideTheJob()
    {
        Directory.CreateDirectory(_root + "/elsewhere");
        File.WriteAllText(_root + "/elsewhere/leak.trx", "worker secret");
        Directory.CreateSymbolicLink(Job + "/src", _root + "/elsewhere");

        var r = await Fetch(MachineOs.Linux, ["*.trx"], dir: "src");

        Assert.Empty(r.Fetched);
        Assert.Contains("outside", r.Error, StringComparison.Ordinal);
    }

    // ---- Windows PowerShell 5.1 ----

    [WindowsFact]
    public async Task PowerShellFetchesMatchingFilesByteForByteInSortedOrder()
    {
        Write("TestResults/b/run.trx", AllBytes(10000)); // more than one 3072-byte chunk
        Write("TestResults/a.trx", AllBytes(1));
        Write("TestResults/empty.trx", []);
        Write("TestResults/c.trx", AllBytes(3072));
        Write("TestResults/notes.txt", AllBytes(3));

        var r = await Fetch(MachineOs.Windows, ["TestResults/**/*.trx"]);

        AssertRoundTrip(r, "TestResults/a.trx", "TestResults/b/run.trx", "TestResults/c.trx", "TestResults/empty.trx");
        Assert.Empty(r.Skipped);
    }

    [WindowsFact]
    public async Task PowerShellMatchesIgnoringCaseAndStarStaysInOneFolder()
    {
        Write("src/Out/X.LOG", AllBytes(3));
        Write("src/Out/deep/y.log", AllBytes(3));

        var r = await Fetch(MachineOs.Windows, ["out/*.log"], dir: "src");

        Assert.Null(r.Error);
        Assert.Single(r.Fetched);
        Assert.Equal(AllBytes(3), File.ReadAllBytes(Path.Combine(Out, r.Fetched[0].Path)));
    }

    [WindowsFact]
    public async Task PowerShellCapsFetchWhatFitsInSortedOrder()
    {
        Write("o/a.bin", AllBytes(40));
        Write("o/b.bin", AllBytes(40));
        Write("o/c.bin", AllBytes(10));
        Write("o/d.bin", AllBytes(10));
        Write("o/huge.bin", AllBytes(200));

        var r = await Fetch(MachineOs.Windows, ["o/*"], new FetchLimits(3, 100, 100));

        Assert.Equal(["o/a.bin", "o/b.bin", "o/c.bin"], r.Fetched.Select(f => f.Path));
        Assert.Equal(["o/d.bin", "o/huge.bin"], r.Skipped.Select(s => s.Path));
    }

    [WindowsFact]
    public async Task PowerShellSkipsJunctionsAndNeverFollowsThem()
    {
        Directory.CreateDirectory(_root + "/elsewhere");
        File.WriteAllText(_root + "/elsewhere/leak.trx", "worker secret");
        Write("TestResults/real.trx", AllBytes(5));
        FetchReceiverTests.MakeDirLink(Job + "/TestResults/linked", _root + "/elsewhere");
        FetchReceiverTests.MakeDirLink(Job + "/Other", _root + "/elsewhere");

        var r = await Fetch(MachineOs.Windows, ["TestResults/**", "Other/*.trx"]);

        AssertRoundTrip(r, "TestResults/real.trx");
        Assert.Equal(["Other", "TestResults/linked"], r.Skipped.Select(s => s.Path).Order(StringComparer.Ordinal));
        Assert.All(r.Skipped, s => Assert.Contains("junction", s.Reason, StringComparison.Ordinal));
    }

    [WindowsFact]
    public async Task PowerShellRefusesAWorkingFolderThatIsAJunction()
    {
        Directory.CreateDirectory(_root + "/elsewhere");
        File.WriteAllText(_root + "/elsewhere/leak.trx", "worker secret");
        FetchReceiverTests.MakeDirLink(Job + "/src", _root + "/elsewhere");

        var r = await Fetch(MachineOs.Windows, ["*.trx"], dir: "src");

        Assert.Empty(r.Fetched);
        Assert.Contains("outside", r.Error, StringComparison.Ordinal);
    }

    /// <summary>The real transport for a Windows worker: the script goes in on stdin and the frames stream back line by line.</summary>
    [WindowsFact]
    public async Task TheWindowsScriptRunsFromStdinAndStreamsBack()
    {
        Write("TestResults/run.trx", AllBytes(20000));
        var m = Worker(MachineOs.Windows);
        string[] patterns = ["TestResults/*.trx"];
        var script = FetchScripts.Fetch(m, "j1", ".", patterns, FetchLimits.Default);
        var command = FetchScripts.WindowsStdinCommand;
        var inner = command[(command.IndexOf('"', StringComparison.Ordinal) + 1)..command.LastIndexOf('"')];
        using var rx = new FetchReceiver(Out, patterns, FetchLimits.Default, ignoreCase: true);

        var result = await ProcessSshClient.Execute(
            "powershell", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", inner], rx.OnLine, TimeSpan.FromMinutes(2), CancellationToken.None,
            FetchScripts.WindowsStdin(script));

        Assert.True(result.Ok, result.Output);
        AssertRoundTrip(rx.Finish(), "TestResults/run.trx");
    }

    [Fact]
    public void TheWindowsCommandLineStaysFarUnderTheCapWhateverThePatterns()
    {
        Assert.True(FetchScripts.WindowsStdinCommand.Length < 300);
        var many = Enumerable.Range(0, FetchPatterns.MaxPatterns).Select(i => "**/" + new string('x', 190) + i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var script = FetchScripts.Fetch(Worker(MachineOs.Windows), "j1", ".", many, FetchLimits.Default);
        Assert.True(FetchScripts.WindowsStdin(script).Length < ProcessSshClient.MaxInputBytes);
    }

    [Fact]
    public void ThePatternsReachTheScriptsOnlyAsQuotedData()
    {
        string[] patterns = ["a b/*.trx"];
        var linux = FetchScripts.Fetch(Worker(MachineOs.Linux), "j1", ".", patterns, FetchLimits.Default);
        Assert.Contains("LQ_RE='^((a b/[^/]*[.]trx))$'", linux, StringComparison.Ordinal);
        var windows = FetchScripts.Fetch(Worker(MachineOs.Windows), "j1", ".", patterns, FetchLimits.Default);
        Assert.Contains("'^((a b/[^/]*[.]trx))\\z'", windows, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => FetchScripts.Fetch(Worker(MachineOs.Linux), "j1", "../x", patterns, FetchLimits.Default));
    }
}
