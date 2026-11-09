using System.Text.Json;

namespace LanQueue.Payload;

/// <summary>
/// Remembers, per machine and repository, the last commit a worker has fetched (<c>&lt;state&gt;/bases.json</c>), so the next bundle for
/// that machine can be incremental. Writes are atomic (temp file, then rename). A missing or unreadable file means "no base", which only
/// costs a full bundle. A null path keeps everything in memory.
/// </summary>
public sealed class BaseStore(string? path)
{
    private readonly Lock _gate = new();
    private Dictionary<string, Dictionary<string, string>>? _memory;

    public string? Get(string machine, string repoId)
    {
        lock (_gate)
        {
            return Load().TryGetValue(machine, out var repos) && repos.TryGetValue(repoId, out var sha) && GitPayloadBuilder.IsObjectId(sha)
                ? sha
                : null;
        }
    }

    public void Set(string machine, string repoId, string sha)
    {
        lock (_gate)
        {
            var all = Load();
            if (!all.TryGetValue(machine, out var repos))
            {
                repos = new Dictionary<string, string>(StringComparer.Ordinal);
                all[machine] = repos;
            }

            repos[repoId] = sha;
            if (path is null)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(all));
                File.Move(tmp, path, overwrite: true);
            }
            finally
            {
                File.Delete(tmp);
            }
        }
    }

    private Dictionary<string, Dictionary<string, string>> Load()
    {
        if (path is null)
        {
            return _memory ??= new(StringComparer.Ordinal);
        }

        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(path)) ?? new(StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new(StringComparer.Ordinal); // unreadable: behave as if nothing was recorded (full bundles)
        }
    }
}
