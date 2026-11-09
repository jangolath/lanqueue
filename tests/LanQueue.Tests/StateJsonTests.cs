using LanQueue.Jobs;
using LanQueue.Payload;
using Xunit;

namespace LanQueue.Tests;

/// <summary>The local state files written by 0.4 (reflection JSON) still read, and still write the same shape, with source-generated JSON.</summary>
public sealed class StateJsonTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-state-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void AJobLogWrittenBy04StillReads()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "jobs.jsonl"),
            "{\"id\":\"20260101-000000-000001\",\"machine\":\"build-01\",\"command\":\"dotnet test\",\"startedUtc\":\"2026-01-01T00:00:00+00:00\","
            + "\"wallSeconds\":12.5,\"exitCode\":1,\"status\":\"Failed\"}\n\n"
            + "{\"Id\":\"20260101-000000-000002\",\"Machine\":null,\"Command\":\"dotnet build\",\"StartedUtc\":\"2026-01-01T01:00:00Z\","
            + "\"WallSeconds\":\"3\",\"ExitCode\":0,\"Status\":\"Completed\"}\n");

        var all = new JobLog(_dir).ReadAll();
        Assert.Equal(2, all.Count);
        Assert.Equal(new JobRecord("20260101-000000-000001", "build-01", "dotnet test", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 12.5, 1, "Failed"), all[0]);
        Assert.Null(all[1].Machine);
        Assert.Equal(3, all[1].WallSeconds); // web defaults: case-insensitive names, numbers in strings
    }

    [Fact]
    public void AJobLogLineIsCamelCase()
    {
        var log = new JobLog(_dir);
        log.Append(new JobRecord("j1", null, "dotnet test", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 1.5, 0, "Completed"));
        Assert.Equal(
            "{\"id\":\"j1\",\"machine\":null,\"command\":\"dotnet test\",\"startedUtc\":\"2026-01-01T00:00:00+00:00\",\"wallSeconds\":1.5,\"exitCode\":0,\"status\":\"Completed\"}\n",
            File.ReadAllText(log.FilePath));
    }

    [Fact]
    public void BasesWrittenBy04StillRead()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "bases.json");
        var sha = new string('a', 40);
        File.WriteAllText(path, "{\"build-01\":{\"0123456789abcdef\":\"" + sha + "\"}}");
        var store = new BaseStore(path);
        Assert.Equal(sha, store.Get("build-01", "0123456789abcdef"));

        store.Set("Build-02", "R", new string('b', 40));
        Assert.Equal("{\"build-01\":{\"0123456789abcdef\":\"" + sha + "\"},\"Build-02\":{\"R\":\"" + new string('b', 40) + "\"}}", File.ReadAllText(path));
    }
}
