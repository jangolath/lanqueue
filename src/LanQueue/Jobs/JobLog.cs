using System.Text.Json;

namespace LanQueue.Jobs;

public sealed record JobRecord(
    string Id,
    string? Machine,
    string Command,
    DateTimeOffset StartedUtc,
    double WallSeconds,
    int ExitCode,
    string Status);

/// <summary>Local append-only JSONL of finished jobs, kept in a gitignored folder. Nothing leaves the machine.</summary>
public sealed class JobLog(string folder)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string FilePath => Path.Combine(folder, "jobs.jsonl");

    public string LogPathFor(string jobId) => Path.Combine(folder, "logs", jobId + ".log");

    public void Append(JobRecord record)
    {
        Directory.CreateDirectory(folder);
        File.AppendAllText(FilePath, JsonSerializer.Serialize(record, Json) + "\n");
    }

    public IReadOnlyList<JobRecord> ReadAll()
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        return File.ReadLines(FilePath)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<JobRecord>(l, Json)!)
            .ToList();
    }
}
