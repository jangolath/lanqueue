using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using LanQueue.Config;
using LanQueue.Jobs;
using Xunit;

namespace LanQueue.Tests;

/// <summary>Opt-in: only runs when LANQ_TAR_TESTS=1 (it launches real tar, PowerShell and optionally docker).</summary>
public sealed class OptInTarTheoryAttribute : TheoryAttribute
{
    public OptInTarTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("LANQ_TAR_TESTS") != "1")
        {
            Skip = "Set LANQ_TAR_TESTS=1 to run the generated prepare scripts against hostile archives on this machine's tar.";
        }
    }
}

public sealed class OptInTarFactAttribute : FactAttribute
{
    public OptInTarFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("LANQ_TAR_TESTS") != "1")
        {
            Skip = "Set LANQ_TAR_TESTS=1 to run the generated prepare scripts against hostile archives on this machine's tar.";
        }
    }
}

/// <summary>
/// Runs the real generated probe, prepare and cleanup scripts against crafted archives. Hosts: "local" (Windows tar.exe through
/// PowerShell on Windows, the system GNU tar through sh elsewhere), and docker images named by LANQ_TAR_DOCKER_GNU and
/// LANQ_TAR_DOCKER_BUSYBOX (the busybox one must be REFUSED by the probe and by prepare).
/// </summary>
public sealed class TarHostileArchiveTests : IDisposable
{
    private const string Id = "20260101-120000-abcdef";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-tar-" + Guid.NewGuid().ToString("N"));

    public TarHostileArchiveTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "root", "incoming"));
        Directory.CreateDirectory(Path.Combine(_dir, "outside"));
        File.WriteAllText(Path.Combine(_dir, "outside", "marker.txt"), "orig");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort: a container may have left root-owned files
        }
    }

    public static IEnumerable<object[]> Hosts()
    {
        yield return ["local"];
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LANQ_TAR_DOCKER_GNU")))
        {
            yield return ["gnu"];
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LANQ_TAR_DOCKER_BUSYBOX")))
        {
            yield return ["busybox"];
        }
    }

    private sealed record HostInfo(string Kind)
    {
        public bool IsWindows => Kind == "local" && OperatingSystem.IsWindows();

        public bool IsBusybox => Kind == "busybox";
    }

    private string RootFor(HostInfo h) => h.Kind is "gnu" or "busybox" ? "/w/root" : (_dir + "/root").Replace('\\', '/');

    private string OutsideFor(HostInfo h) => h.Kind is "gnu" or "busybox" ? "/w/outside" : (_dir + "/outside").Replace('\\', '/');

    private Machine MachineFor(HostInfo h)
    {
        var m = h.IsWindows ? T.Windows() : T.Linux();
        m.Transfer = TransferMode.Archive;
        m.WorkRoot = RootFor(h);
        return m;
    }

    private (int Code, string Output) RunScript(HostInfo h, string script)
    {
        if (h.IsWindows)
        {
            var ps1 = Path.Combine(_dir, "run-" + Guid.NewGuid().ToString("N") + ".ps1");
            File.WriteAllText(ps1, "$ProgressPreference='SilentlyContinue'\n" + script, new UTF8Encoding(true));
            return Run("powershell", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", ps1], null);
        }

        if (h.Kind == "local")
        {
            return Run("sh", ["-c", script], null);
        }

        var image = Environment.GetEnvironmentVariable(h.IsBusybox ? "LANQ_TAR_DOCKER_BUSYBOX" : "LANQ_TAR_DOCKER_GNU")!;
        return Run("docker", ["run", "--rm", "-v", _dir + ":/w", image, "sh", "-c", script], null);
    }

    private static (int Code, string Output) Run(string file, string[] args, string? pathPrepend)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        if (pathPrepend is not null)
        {
            psi.Environment["PATH"] = pathPrepend + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        }

        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o + err.GetAwaiter().GetResult());
    }

    private static byte[] Build(params TarEntry[] entries)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var tw = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: false))
        {
            foreach (var e in entries)
            {
                tw.WriteEntry(e);
            }
        }

        return ms.ToArray();
    }

    private static PaxTarEntry Reg(string name, string content = "x") =>
        new(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) };

    private static PaxTarEntry Dir(string name) => new(TarEntryType.Directory, name);

    private static PaxTarEntry Link(TarEntryType type, string name, string target) => new(type, name) { LinkName = target };

    private (int Code, string Output) Prepare(HostInfo h, byte[] archive)
    {
        File.WriteAllBytes(Path.Combine(_dir, "root", "incoming", Id + ".tar.gz"), archive);
        return RunScript(h, RemoteScripts.Prepare(MachineFor(h), Id));
    }

    private string JobPath(string relative) => Path.Combine(_dir, "root", "jobs", Id, relative);

    private void AssertNothingEscaped(string output)
    {
        Assert.Equal("orig", File.ReadAllText(Path.Combine(_dir, "outside", "marker.txt")));
        Assert.Equal("marker.txt", Assert.Single(Directory.GetFileSystemEntries(Path.Combine(_dir, "outside")).Select(Path.GetFileName)));
        Assert.False(File.Exists(Path.Combine(_dir, "pwned")), output);
    }

    private void AssertRefused(HostInfo h, byte[] archive)
    {
        var (code, output) = Prepare(h, archive);
        Assert.True(code != 0, "hostile archive must be refused. Output: " + output);
        Assert.False(Directory.Exists(Path.Combine(_dir, "root", "jobs", Id)), "refused before anything is created. Output: " + output);
        AssertNothingEscaped(output);
    }

    [OptInTarTheory]
    [MemberData(nameof(Hosts))]
    public void ProbeAcceptsGnuOrBsdtarAndRefusesBusybox(string kind)
    {
        var h = new HostInfo(kind);
        var (code, output) = RunScript(h, RemoteScripts.Probe(MachineFor(h)));
        Assert.Equal(0, code);
        Assert.Contains(h.IsBusybox ? "tool=missing" : "tool=ok", output, StringComparison.Ordinal);
    }

    [OptInTarTheory]
    [MemberData(nameof(Hosts))]
    public void CleanArchiveExtractsOnSupportedTarAndIsRefusedOnBusybox(string kind)
    {
        var h = new HostInfo(kind);
        var (code, output) = Prepare(h, Build(Dir("src"), Reg("src/a.txt", "hello"), Reg("b.txt", "bee")));
        if (h.IsBusybox)
        {
            Assert.NotEqual(0, code);
            Assert.Contains("unsupported tar", output, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(_dir, "root", "jobs", Id)));
            return;
        }

        Assert.True(code == 0, output);
        Assert.Equal("hello", File.ReadAllText(JobPath("src/a.txt")));
        Assert.Equal("bee", File.ReadAllText(JobPath("b.txt")));
    }

    [OptInTarTheory]
    [MemberData(nameof(Hosts))]
    public void AbsolutePathEntryIsRefused(string kind)
    {
        var h = new HostInfo(kind);
        AssertRefused(h, Build(Reg(OutsideFor(h) + "/marker.txt", "PWNED")));
    }

    [OptInTarTheory]
    [MemberData(nameof(Hosts))]
    public void DotSlashDotDotEntryIsRefused(string kind)
    {
        AssertRefused(new HostInfo(kind), Build(Reg("./../../../outside/marker.txt", "PWNED")));
        AssertRefused(new HostInfo(kind), Build(Reg("a/../../../../outside/marker.txt", "PWNED")));
    }

    [OptInTarTheory]
    [MemberData(nameof(Hosts))]
    public void BackslashEntryIsRefused(string kind)
    {
        AssertRefused(new HostInfo(kind), Build(Reg(@"..\..\..\outside\marker.txt", "PWNED")));
        AssertRefused(new HostInfo(kind), Build(Reg(@"a\b", "x")));
    }

    [OptInTarTheory]
    [MemberData(nameof(Hosts))]
    public void HardLinkToAFileOutsideThenWritingThroughItIsRefused(string kind)
    {
        var h = new HostInfo(kind);
        AssertRefused(h, Build(Link(TarEntryType.HardLink, "h", OutsideFor(h) + "/marker.txt"), Reg("h", "PWNED")));
        AssertRefused(h, Build(Link(TarEntryType.HardLink, "h2", "../../../outside/marker.txt"), Reg("h2", "PWNED")));
    }

    [OptInTarTheory]
    [MemberData(nameof(Hosts))]
    public void SymlinkOutsideIsRefusedAndSoIsSymlinkThenFileThroughIt(string kind)
    {
        var h = new HostInfo(kind);
        AssertRefused(h, Build(Link(TarEntryType.SymbolicLink, "s", OutsideFor(h))));
        AssertRefused(h, Build(Link(TarEntryType.SymbolicLink, "d", "../../../outside"), Reg("d/marker.txt", "PWNED")));
    }

    [OptInTarTheory]
    [MemberData(nameof(Hosts))]
    public void FifoAndDeviceEntriesAreRefused(string kind)
    {
        var h = new HostInfo(kind);
        AssertRefused(h, Build(new PaxTarEntry(TarEntryType.Fifo, "pipe")));
        AssertRefused(h, Build(new PaxTarEntry(TarEntryType.CharacterDevice, "tty") { DeviceMajor = 1, DeviceMinor = 3 }));
    }

    [OptInTarTheory]
    [MemberData(nameof(Hosts))]
    public void NewlineInANameCannotSmuggleAPastTheCheck(string kind)
    {
        var h = new HostInfo(kind);
        AssertRefused(h, Build(Reg("ok\n" + OutsideFor(h) + "/marker.txt", "PWNED")));
        AssertRefused(h, Build(Reg("a\nb", "x")));
    }

    [OptInTarTheory]
    [MemberData(nameof(Hosts))]
    public void DuplicateEntriesExtractHarmlesslyLastOneWins(string kind)
    {
        var h = new HostInfo(kind);
        var (code, output) = Prepare(h, Build(Reg("f", "one"), Reg("f", "two")));
        if (h.IsBusybox)
        {
            Assert.NotEqual(0, code);
            return;
        }

        Assert.True(code == 0, output);
        Assert.Equal("two", File.ReadAllText(JobPath("f")));
        AssertNothingEscaped(output);
    }

    [OptInTarTheory]
    [MemberData(nameof(Hosts))]
    public void PaxLongNamesAreAcceptedAndExtracted(string kind)
    {
        var h = new HostInfo(kind);
        var name = "deep/" + new string('d', 90) + "/" + new string('f', 120) + ".txt";
        var (code, output) = Prepare(h, Build(Reg(name, "long")));
        if (h.IsBusybox)
        {
            Assert.NotEqual(0, code);
            return;
        }

        Assert.True(code == 0, output);
        Assert.Equal("long", File.ReadAllText(JobPath(name)));
    }

    [OptInTarTheory]
    [MemberData(nameof(Hosts))]
    public void WindowsHostileNamesAreRefusedOnWindowsAndLeaveNoJobFolder(string kind)
    {
        var h = new HostInfo(kind);
        if (!h.IsWindows && !h.IsBusybox)
        {
            return; // valid names on Linux; lanq refuses to build such archives anyway
        }

        foreach (var bad in new[] { "...", "x/.. /y", "CON", "aux.txt", "a:b", "dir./f", "trail /f", "a<b", "q?", "NUL.tar.gz", "src/com3/x" })
        {
            AssertRefused(h, Build(Reg(bad, "x")));
        }
    }

    [OptInTarFact]
    public void WindowsPrepareIgnoresAGnuTarEarlierOnPath()
    {
        var h = new HostInfo("local");
        if (!h.IsWindows)
        {
            return;
        }

        var fake = Path.Combine(_dir, "fakebin");
        Directory.CreateDirectory(fake);
        File.WriteAllText(Path.Combine(fake, "tar.cmd"), "@echo off\r\necho FAKE-TAR-WAS-USED\r\nexit /b 9\r\n");
        File.WriteAllBytes(Path.Combine(_dir, "root", "incoming", Id + ".tar.gz"), Build(Reg("a.txt", "ok")));
        var ps1 = Path.Combine(_dir, "run-path.ps1");
        File.WriteAllText(ps1, RemoteScripts.Prepare(MachineFor(h), Id), new UTF8Encoding(true));
        var (code, output) = Run("powershell", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", ps1], fake);

        Assert.True(code == 0, output);
        Assert.DoesNotContain("FAKE-TAR", output, StringComparison.Ordinal);
        Assert.Equal("ok", File.ReadAllText(JobPath("a.txt")));
    }

    [OptInTarFact]
    public void WindowsCleanupRemovesAFolderWhoseNamesWin32CannotAddress()
    {
        var h = new HostInfo("local");
        if (!h.IsWindows)
        {
            return;
        }

        var job = Path.Combine(_dir, "root", "jobs", Id);
        var extended = @"\\?\" + job;
        Directory.CreateDirectory(extended + @"\...");
        Directory.CreateDirectory(extended + @"\x\.. ");
        File.WriteAllText(extended + @"\x\.. \f.txt", "left behind");
        Directory.CreateDirectory(extended + @"\CON");

        var (code, output) = RunScript(h, RemoteScripts.Cleanup(MachineFor(h), Id, "1"));

        Assert.True(code == 0, output);
        Assert.False(Directory.Exists(extended), "the job folder must be really gone. Output: " + output);
    }
}
