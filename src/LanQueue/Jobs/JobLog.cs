using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanQueue.Jobs;

/// <summary>
/// One line of jobs.jsonl. The fields after <see cref="Status"/> were added in 0.7.0; each is left out of the line when null, so a record
/// without them writes exactly the line 0.6 wrote, and an older line reads back with them null.
/// </summary>
public sealed record JobRecord(
    string Id,
    string? Machine,
    string Command,
    DateTimeOffset StartedUtc,
    double WallSeconds,
    int ExitCode,
    string Status)
{
    /// <summary>The line's schema version: 2 on every line 0.7.0 writes; absent (null) means 1.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? V { get; init; }

    /// <summary>The command's identity (<see cref="JobHistory.Key"/>): the TEMPLATE argv for a shard, with {shard}/{shards} kept literally.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Key { get; init; }

    /// <summary>The job's --dir, normalised (<see cref="JobHistory.Dir"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Dir { get; init; }

    /// <summary>--shards only: this job's 1-based shard index.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Shard { get; init; }

    /// <summary>--shards only: the run's shard count N.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Shards { get; init; }

    /// <summary>--shards only: the id every shard (and every retried attempt) of one run shares.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    /// <summary>How long the job's own command ran on the worker (not the wait, upload, prepare, fetch or cleanup); null when it never started.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RunSeconds { get; init; }
}

/// <summary>What <see cref="JobLog.Read"/> found: the records (oldest file first), how many lines could not be read, and the files read.</summary>
public sealed record JobLogRead(IReadOnlyList<JobRecord> Records, int Skipped, IReadOnlyList<string> Files);

/// <summary>
/// Local append-only JSONL of finished jobs, kept in a per-user state folder. Nothing leaves the machine. At <see cref="RotateBytes"/> the
/// file moves to jobs.1.jsonl (replacing the one before), so at most two files exist; readers read both.
/// </summary>
/// <param name="warn">Where a job that could not be recorded is reported (a warning, never an exception); null means stderr.</param>
public sealed class JobLog(string folder, Action<string>? warn = null)
{
    /// <summary>jobs.jsonl moves to jobs.1.jsonl before an append once it is at least this big (4 MiB).</summary>
    public const long RotateBytes = 4L * 1024 * 1024;

    /// <summary>How many times an append is retried after an I/O error (another lanq writing, an antivirus scan) before it gives up with a warning.</summary>
    internal const int AppendRetries = 5;

    private readonly Action<string> warn = warn ?? (line => Console.Error.WriteLine(line));

    public string FilePath => Path.Combine(folder, "jobs.jsonl");

    /// <summary>The previous file, written before the last rotation.</summary>
    public string RotatedPath => Path.Combine(folder, "jobs.1.jsonl");

    /// <summary>
    /// Held (FileShare.None) by an appending lanq while it rotates and writes, so two lanq processes never rotate twice or write over each
    /// other's line. Readers never take it.
    /// </summary>
    private string LockPath => Path.Combine(folder, "jobs.jsonl.lock");

    /// <summary>The folder of the per-job logs (see <see cref="LogPathFor"/>).</summary>
    public string LogsFolder => Path.Combine(folder, "logs");

    public string LogPathFor(string jobId) => Path.Combine(folder, "logs", jobId + ".log");

    /// <summary>Shards of one run can finish at the same moment: one append at a time within this lanq.</summary>
    private static readonly Lock AppendGate = new();

    /// <summary>
    /// Records one finished job. Never throws: a job's result must never depend on its stats line. Another lanq appending, a reader, or an
    /// antivirus scan can make the file briefly unavailable, so an I/O error is retried <see cref="AppendRetries"/> times after 20 to 100 ms;
    /// after that the job is left out with a warning.
    /// </summary>
    public void Append(JobRecord record) => Append(record, processGate: true);

    /// <param name="processGate">False only in tests that stand in for two lanq processes (which share no in-process lock).</param>
    internal void Append(JobRecord record, bool processGate)
    {
        if (processGate)
        {
            lock (AppendGate)
            {
                AppendOnce(record);
            }
        }
        else
        {
            AppendOnce(record);
        }
    }

    private void AppendOnce(JobRecord record)
    {
        try
        {
            Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, StateJson.Default.JobRecord) + "\n"));
        }
        catch (Exception ex)
        {
            Warn(ex);
        }
    }

    private void Write(byte[] line)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.CreateDirectory(folder);
                using var gate = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                Rotate();
                using var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                fs.Write(line);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < AppendRetries)
            {
                Thread.Sleep(Random.Shared.Next(20, 101));
            }
        }
    }

    /// <summary>Moves a full jobs.jsonl to jobs.1.jsonl; when that cannot be done now (the file is open elsewhere), it is tried again on the next append.</summary>
    private void Rotate()
    {
        try
        {
            var current = new FileInfo(FilePath);
            if (current.Exists && current.Length >= RotateBytes)
            {
                File.Move(FilePath, RotatedPath, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep appending to the current file; rotation is only housekeeping.
        }
    }

    private void Warn(Exception ex)
    {
        try
        {
            warn("[lanq] warning: could not record the job in jobs.jsonl: " + JobRunner.SafeLine(ex.Message));
        }
        catch (Exception)
        {
            // Not even the warning could be written: the job's result still stands.
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

    /// <summary>Every readable record, oldest file first (see <see cref="Read"/>); unreadable lines are left out.</summary>
    public IReadOnlyList<JobRecord> ReadAll() => Read(CancellationToken.None).Records;

    /// <summary>
    /// Reads jobs.1.jsonl (if any) and then jobs.jsonl, sharing them with a lanq that is appending. A line that is not a job record (a torn
    /// line from a crash, a hand edit) is skipped and counted, never fatal. A file that exists but cannot be read throws (IOException or
    /// UnauthorizedAccessException); a missing file is simply not there.
    /// </summary>
    public JobLogRead Read(CancellationToken ct)
    {
        var records = new List<JobRecord>();
        var files = new List<string>();
        var skipped = 0;
        foreach (var path in new[] { RotatedPath, FilePath })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            files.Add(path);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var n = 0;
            while (reader.ReadLine() is { } line)
            {
                if (++n % 1000 == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JobRecord? record;
                try
                {
                    record = JsonSerializer.Deserialize(line, StateJson.Default.JobRecord);
                }
                catch (JsonException)
                {
                    record = null;
                }

                // Valid JSON that is not a job record ({} or null) is as unusable as a torn line.
                if (record is null || record.Id is null || record.Command is null || record.Status is null)
                {
                    skipped++;
                }
                else
                {
                    records.Add(record);
                }
            }
        }

        return new JobLogRead(records, skipped, files);
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
