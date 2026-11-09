using LanQueue.Config;
using LanQueue.Jobs;
using Xunit;

namespace LanQueue.Tests;

/// <summary>priority: low (default) runs the job below normal priority; normal leaves it alone.</summary>
public sealed class JobPriorityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lanq-prio-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');

    public JobPriorityTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "jobs", "j1"));
        Directory.CreateDirectory(Path.Combine(_root, "locks"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string WithPriority(string value) => T.Yaml.Replace("max_jobs: 2", "max_jobs: 2" + (char)10 + "    priority: " + value, StringComparison.Ordinal);

    [Fact]
    public void PriorityDefaultsToLowAndAcceptsNormal()
    {
        Assert.All(T.Config().Machines, m => Assert.Equal(JobPriority.Low, m.Priority));
        Assert.Equal(JobPriority.Normal, ConfigLoader.Parse(WithPriority("normal")).Machines[0].Priority);
        Assert.Equal(JobPriority.Low, ConfigLoader.Parse(WithPriority("Low")).Machines[0].Priority);
    }

    [Theory]
    [InlineData("high")]
    [InlineData("1")]
    [InlineData("realtime")]
    [InlineData("\"\"")]
    public void UnknownPriorityIsRefused(string value)
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(WithPriority(value)));
        Assert.Contains("'low' or 'normal'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxLowPriorityWrapsTheJobWithNiceAndIoniceWhenTheyWork()
    {
        var s = RemoteScripts.Execute(T.Linux(), "j1", ".", ["dotnet", "test"]);
        Assert.Contains("if nice -n 10 true >/dev/null 2>&1; then N=\"nice -n 10\"; fi", s, StringComparison.Ordinal);
        Assert.Contains("if ionice -c3 true >/dev/null 2>&1; then I=\"ionice -c3\"; fi", s, StringComparison.Ordinal);
        Assert.Contains("$S $N $I sh -c", s, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxNormalPriorityHasNoNice()
    {
        var m = T.Linux();
        m.Priority = JobPriority.Normal;
        var s = RemoteScripts.Execute(m, "j1", ".", ["dotnet", "test"]);
        Assert.DoesNotContain("nice", s, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsLowPriorityLowersThePowerShellHostBeforeTheJobStarts()
    {
        var s = RemoteScripts.Execute(T.Windows(), "j1", ".", ["dotnet", "test"]);
        Assert.Contains("(Get-Process -Id $PID).PriorityClass = 'BelowNormal'", s, StringComparison.Ordinal);
        Assert.True(s.IndexOf("BelowNormal", StringComparison.Ordinal) < s.IndexOf("& 'dotnet' 'test'", StringComparison.Ordinal));
        var m = T.Windows();
        m.Priority = JobPriority.Normal;
        Assert.DoesNotContain("PriorityClass", RemoteScripts.Execute(m, "j1", ".", ["dotnet", "test"]), StringComparison.Ordinal);
    }

    /// <summary>A real Windows PowerShell: the job's child process inherits BelowNormal.</summary>
    [Fact]
    public void RealPowerShellJobChildRunsBelowNormal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var m = T.Windows();
        m.WorkRoot = _root;
        var script = RemoteScripts.Execute(m, "j1", ".", ["powershell", "-NoProfile", "-NonInteractive", "-Command", "[Console]::Out.Write((Get-Process -Id $PID).PriorityClass)"]);
        var file = Path.Combine(Path.GetTempPath(), "lanq-prio-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(file, "\ufeff" + script);
        try
        {
            Assert.Equal("BelowNormal", WorkerHealthTests.Capture("powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file).Trim());
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>A real sh (Linux CI leg): the job runs at niceness 10.</summary>
    [Fact]
    public void RealShJobRunsNiced()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var m = T.Linux();
        m.WorkRoot = _root;
        var before = int.Parse(WorkerHealthTests.Capture("nice").Trim(), System.Globalization.CultureInfo.InvariantCulture);
        var output = WorkerHealthTests.Capture("sh", "-c", RemoteScripts.Execute(m, "j1", ".", ["nice"]));
        Assert.Equal(Math.Min(before + 10, 19), int.Parse(output.Trim(), System.Globalization.CultureInfo.InvariantCulture));
    }
}
