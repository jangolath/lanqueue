using System.Formats.Tar;
using System.IO.Compression;

namespace LanQueue.Payload;

/// <param name="Path">Repository-relative path with forward slashes.</param>
/// <param name="Executable">Whether the file gets the executable mode bits.</param>
/// <param name="OpenAsync">Opens the content. Called once per file, in path order, and the stream is disposed after the entry is written.</param>
public sealed record ArchiveFile(string Path, bool Executable, Func<CancellationToken, ValueTask<Stream>> OpenAsync)
{
    public ArchiveFile(string path, bool executable, byte[] content)
        : this(path, executable, _ => ValueTask.FromResult<Stream>(new MemoryStream(content, writable: false)))
    {
    }
}

/// <summary>
/// Writes a deterministic tar.gz: entries sorted ordinally, fixed timestamps and ownership, regular files and
/// directories only. Entry paths are validated so the archive can never address anything outside its extraction root.
/// </summary>
public static class ArchiveWriter
{
    private static readonly DateTimeOffset FixedTime = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Git tree entry modes that may be archived: regular and executable files. Symlinks (120000) and submodules (160000) are not.</summary>
    public static bool IsRegularMode(string mode) => mode is "100644" or "100755";

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Refuses any path that could escape the extraction root or misbehave on any worker. The rules apply to every archive
    /// (any worker may be Windows). The message names the rule that failed.
    /// </summary>
    public static void ValidatePath(string path)
    {
        if (path.Length == 0 || path.Length > 4096)
        {
            throw Refused(path, "empty-or-too-long");
        }

        if (path[0] == '/' || path.Contains('\\', StringComparison.Ordinal) || path.Any(char.IsControl))
        {
            throw Refused(path, "absolute-backslash-or-control-character");
        }

        if (path.Contains(" -> ", StringComparison.Ordinal) || path.Contains(" link to ", StringComparison.Ordinal))
        {
            throw Refused(path, "name-looks-like-link-listing");
        }

        if (path.AsSpan().IndexOfAny("<>:\"|?*") >= 0)
        {
            throw Refused(path, "windows-forbidden-character");
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                throw Refused(path, "empty-dot-or-dotdot-segment");
            }

            if (segment[^1] is '.' or ' ')
            {
                throw Refused(path, "windows-trailing-dot-or-space");
            }

            var dot = segment.IndexOf('.', StringComparison.Ordinal);
            var stem = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');
            if (ReservedDeviceNames.Contains(stem))
            {
                throw Refused(path, "windows-reserved-device-name");
            }
        }
    }

    private static ArgumentException Refused(string path, string rule) =>
        new($"Archive path '{path}' refused (rule: {rule}).", nameof(path));

    public static async Task CreateAsync(string outputPath, IEnumerable<ArchiveFile> files, CancellationToken ct)
    {
        var sorted = files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        foreach (var f in sorted)
        {
            ValidatePath(f.Path);
        }

        var dirs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in sorted)
        {
            for (var i = f.Path.IndexOf('/', StringComparison.Ordinal); i >= 0; i = f.Path.IndexOf('/', i + 1))
            {
                dirs.Add(f.Path[..i]);
            }
        }

        var entries = dirs.Select(d => (Path: d, File: (ArchiveFile?)null))
            .Concat(sorted.Select(f => (f.Path, File: (ArchiveFile?)f)))
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ToList();

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outputPath))!);
        var tmp = outputPath + ".partial";
        try
        {
            await using (var fs = File.Create(tmp))
            await using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            await using (var tar = new TarWriter(gz, TarEntryFormat.Ustar, leaveOpen: false))
            {
                foreach (var (path, file) in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = file is null
                        ? NewEntry(TarEntryType.Directory, path)
                        : NewEntry(TarEntryType.RegularFile, path);
                    entry.Mode = file is null || file.Executable
                        ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                        : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
                    entry.ModificationTime = FixedTime;
                    entry.Uid = 0;
                    entry.Gid = 0;
                    if (file is null)
                    {
                        await tar.WriteEntryAsync(entry, ct).ConfigureAwait(false);
                        continue;
                    }

                    var data = await file.OpenAsync(ct).ConfigureAwait(false);
                    await using (data.ConfigureAwait(false))
                    {
                        entry.DataStream = data;
                        await tar.WriteEntryAsync(entry, ct).ConfigureAwait(false);
                    }
                }
            }

            File.Move(tmp, outputPath, overwrite: true);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    private static TarEntry NewEntry(TarEntryType type, string name)
    {
        if (name.Length <= 100 && name.All(char.IsAscii))
        {
            return new UstarTarEntry(type, name);
        }

        var fixedStamp = FixedTime.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new PaxTarEntry(
            type,
            name,
            new Dictionary<string, string> { ["mtime"] = fixedStamp, ["atime"] = fixedStamp, ["ctime"] = fixedStamp });
    }
}
