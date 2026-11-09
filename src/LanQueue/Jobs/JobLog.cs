using System.Text.Json;
using System.Text.Json.Serialization;

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
    public string FilePath => Path.Combine(folder, "jobs.jsonl");

    public string LogPathFor(string jobId) => Path.Combine(folder, "logs", jobId + ".log");

    public void Append(JobRecord record)
    {
        Directory.CreateDirectory(folder);
        File.AppendAllText(FilePath, JsonSerializer.Serialize(record, StateJson.Default.JobRecord) + "\n");
    }

    public IReadOnlyList<JobRecord> ReadAll()
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        return File.ReadLines(FilePath)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize(l, StateJson.Default.JobRecord)!)
            .ToList();
    }
}

/// <summary>
/// Source-generated serialization (no reflection, so it works trimmed and NativeAOT) for lanq's own local state: the job log uses the web
/// defaults it always had (camelCase, case-insensitive), and the bases file is a plain dictionary whose keys no naming policy touches.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(JobRecord))]
[JsonSerializable(typeof(Dictionary<string, Dictionary<string, string>>))]
internal sealed partial class StateJson : JsonSerializerContext;
