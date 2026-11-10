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

    /// <summary>Shards of one run can finish at the same moment: one append at a time within this lanq (parallel appends can fail on Windows).</summary>
    private static readonly Lock AppendGate = new();

    public void Append(JobRecord record)
    {
        lock (AppendGate)
        {
            Directory.CreateDirectory(folder);
            File.AppendAllText(FilePath, JsonSerializer.Serialize(record, StateJson.Default.JobRecord) + "\n");
        }
    }

    /// <summary>The most bytes <see cref="Tail"/> reads from the end of a log, whatever it is asked for.</summary>
    public const int TailMaxBytes = 256 * 1024;

    /// <summary>The longest line <see cref="Tail"/> returns; longer lines are cut.</summary>
    public const int TailMaxLineChars = 4000;

    /// <summary>
    /// The last <paramref name="lines"/> lines of a job log (lanq run --log-tail), read backwards from the end in blocks and never more than
    /// <see cref="TailMaxBytes"/>, so a huge log costs no more than a small one; when the bound cuts a line, that partial line is left out and
    /// fewer lines come back. Each line is cut to <see cref="TailMaxLineChars"/> and sanitised again (it may be read back by an agent or a
    /// terminal). A missing or unreadable log gives an empty list.
    /// </summary>
    public static IReadOnlyList<string> Tail(string path, int lines)
    {
        if (lines < 1)
        {
            return [];
        }

        byte[] buffer;
        int from;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = fs.Length;
            var start = Math.Max(0, length - TailMaxBytes);
            buffer = new byte[length - start];
            const int Block = 16 * 1024;
            var pos = length;
            var breaks = 0;

            // lines + 1 line breaks found: the lines after the first of them are whole (the last break may only end the file).
            while (pos > start && breaks <= lines)
            {
                var blockStart = Math.Max(start, pos - Block);
                fs.Seek(blockStart, SeekOrigin.Begin);
                fs.ReadExactly(buffer, (int)(blockStart - start), (int)(pos - blockStart));
                for (var i = pos - 1; i >= blockStart; i--)
                {
                    if (buffer[i - start] == (byte)'\n')
                    {
                        breaks++;
                    }
                }

                pos = blockStart;
            }

            from = (int)(pos - start);
            if (pos > 0)
            {
                // Started inside a line: skip to the first line break (also skips a UTF-8 sequence cut in two).
                var nl = Array.IndexOf(buffer, (byte)'\n', from);
                from = nl < 0 ? buffer.Length : nl + 1;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var text = System.Text.Encoding.UTF8.GetString(buffer, from, buffer.Length - from);
        if (text.Length == 0)
        {
            return [];
        }

        var all = text.Split('\n');
        var count = text.EndsWith('\n') ? all.Length - 1 : all.Length;
        return [.. all[..count].Skip(Math.Max(0, count - lines)).Select(l => JobRunner.SafeLine(Cap(l.TrimEnd('\r'))))];

        static string Cap(string line) =>
            line.Length <= TailMaxLineChars ? line : line[..(char.IsHighSurrogate(line[TailMaxLineChars - 1]) ? TailMaxLineChars - 1 : TailMaxLineChars)];
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
