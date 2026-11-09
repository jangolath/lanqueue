using System.Diagnostics;
using System.Globalization;
using System.Text;
using LanQueue.Config;
using LanQueue.Jobs;
using Xunit;

namespace LanQueue.Tests;

/// <summary>Runs the real slot-claim script (sh and/or PowerShell, whichever this machine has) against prepared slot files.</summary>
public sealed class StaleSlotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lanq-slots-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');

    public StaleSlotTests() => Directory.CreateDirectory(Path.Combine(_root, "locks"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    public static TheoryData<string> Shells() => ["linux", "windows"];

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private string Slot(int i) => Path.Combine(_root, "locks", "slot-" + i.ToString(CultureInfo.InvariantCulture));

    private void WriteSlot(int i, string content) => File.WriteAllText(Slot(i), content);

    private static string Lines(params object[] parts) => string.Join('\n', parts.Select(p => Convert.ToString(p, CultureInfo.InvariantCulture))) + "\n";

    /// <returns>The script output, or null when this machine has no such shell.</returns>
    private string? Acquire(string shell, string jobId, TimeSpan timeout)
    {
        var m = new Machine
        {
            Name = "w",
            Os = shell == "windows" ? MachineOs.Windows : MachineOs.Linux,
            WorkRoot = _root,
            MaxJobs = 2,
        };
        var script = RemoteScripts.AcquireSlot(m, jobId, timeout);
        if (shell == "linux")
        {
            return OnPath("sh") ? Exec("sh", "-c", script) : null;
        }

        var host = OnPath("pwsh") ? "pwsh" : OnPath("powershell") ? "powershell" : null;
        if (host is null)
        {
            return null;
        }

        var ps1 = Path.Combine(Path.GetTempPath(), "lanq-slot-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(ps1, script, new UTF8Encoding(true));
        try
        {
            return Exec(host, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", ps1);
        }
        finally
        {
            File.Delete(ps1);
        }
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public void SlotPastItsDeadlinePlusGraceIsReclaimed(string shell)
    {
        WriteSlot(1, Lines("crashed-job", Now - RemoteScripts.StaleSlotGraceSeconds - 60));
        WriteSlot(2, Lines("live-job", Now + 3600));

        var output = Acquire(shell, "new-job", TimeSpan.FromMinutes(10));
        if (output is null)
        {
            return; // no such shell on this machine
        }

        Assert.Equal("1", JobRunner.ParseSlot(output));
        Assert.StartsWith("new-job\n", File.ReadAllText(Slot(1)).Replace("\r", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(Lines("live-job", Now + 3600)[..9], File.ReadAllText(Slot(2))[..9]);
        Assert.Equal(["slot-1", "slot-2"], Directory.GetFiles(Path.Combine(_root, "locks")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public void SlotWithinItsGraceOrWithoutADeadlineIsNeverReclaimed(string shell)
    {
        var overdue = Lines("slow-job", Now - 600); // past its deadline, but inside the grace period
        var legacy = "job-from-an-older-lanq"; // no deadline recorded
        WriteSlot(1, overdue);
        WriteSlot(2, legacy);

        var output = Acquire(shell, "new-job", TimeSpan.FromMinutes(10));
        if (output is null)
        {
            return;
        }

        Assert.Null(JobRunner.ParseSlot(output));
        Assert.Contains("SLOT=FULL", output, StringComparison.Ordinal);
        Assert.Equal(overdue, File.ReadAllText(Slot(1)));
        Assert.Equal(legacy, File.ReadAllText(Slot(2)));
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public void NewSlotRecordsItsOwnDeadline(string shell)
    {
        var before = Now;
        var output = Acquire(shell, "new-job", TimeSpan.FromMinutes(10));
        if (output is null)
        {
            return;
        }

        Assert.Equal("1", JobRunner.ParseSlot(output));
        var lines = File.ReadAllText(Slot(1)).Replace("\r", "", StringComparison.Ordinal).Split('\n');
        Assert.Equal("new-job", lines[0]);
        var deadline = long.Parse(lines[1], CultureInfo.InvariantCulture);
        var lease = (long)(TimeSpan.FromMinutes(10) + RemoteScripts.SlotTransferAllowance).TotalSeconds;
        Assert.InRange(deadline, before + lease - 5, Now + lease + 5);
    }

    [Fact]
    public void RunnerLeasesTheSlotForTheJobTimeoutPlusTheTransferAllowance()
    {
        var script = RemoteScripts.AcquireSlot(T.Linux(), "j", TimeSpan.FromMinutes(30));
        var seconds = (long)(TimeSpan.FromMinutes(30) + RemoteScripts.SlotTransferAllowance).TotalSeconds;
        Assert.Contains("+ " + seconds.ToString(CultureInfo.InvariantCulture), script, StringComparison.Ordinal);
        Assert.Contains("+ " + seconds.ToString(CultureInfo.InvariantCulture), RemoteScripts.AcquireSlot(T.Windows(), "j", TimeSpan.FromMinutes(30)), StringComparison.Ordinal);
    }

    private static string Exec(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output + err.Result;
    }

    private static bool OnPath(string exe)
    {
        var names = OperatingSystem.IsWindows() ? new[] { exe + ".exe", exe + ".cmd" } : new[] { exe };
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(d => names.Any(n => File.Exists(Path.Combine(d, n))));
    }
}
