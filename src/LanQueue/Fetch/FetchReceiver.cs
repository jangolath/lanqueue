using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LanQueue.Jobs;

namespace LanQueue.Fetch;

/// <summary>One fetched file: its relative path below the job's output folder, size and SHA-256 (lowercase hex).</summary>
public sealed record FetchedFile(string Path, long Bytes, string Sha256);

/// <summary>A file that was not fetched, and why (the path is the worker's, sanitised for display).</summary>
public sealed record FetchSkip(string Path, string Reason);

/// <summary>What lanq run --fetch brought back.</summary>
/// <param name="Directory">The local folder the files were written to (out/jobid).</param>
/// <param name="Error">A problem with the fetch as a whole (timed out, stream cut short, worker folder missing...), or null.</param>
/// <param name="MoreSkipped">Skipped files beyond the ones listed in <see cref="Skipped"/>.</param>
public sealed record FetchReport(string Directory, IReadOnlyList<FetchedFile> Fetched, IReadOnlyList<FetchSkip> Skipped, string? Error, int MoreSkipped = 0)
{
    public long Bytes => Fetched.Sum(f => f.Bytes);
}

/// <summary>
/// The local side of lanq run --fetch. Consumes the worker's framed text stream line by line (from the bounded line reader) and writes
/// each file to <c>out/jobid/relpath</c>, streaming: never more than one data line in memory. Grammar, one item per line:
/// <code>
/// LQF1 FILE &lt;bytes&gt; &lt;base64 utf-8 path&gt;
///   &lt;base64 data&gt;  (any number of lines, each at most 4096 characters, a multiple of 4)
/// LQF1 END &lt;sha-256, 64 lowercase hex&gt;
/// LQF1 SKIP &lt;reason&gt; &lt;base64 utf-8 path&gt;
/// LQF1 ERROR &lt;word&gt;
/// LQF1 DONE
/// </code>
/// Nothing the worker says is trusted: every path is checked again (<see cref="FetchPath"/>, must match a requested pattern, no
/// case-insensitive collision), the counts and sizes are enforced on the bytes actually received, the size in the header must be exact, and
/// the hash must match. Any frame error deletes that partial file. Files are created with CreateNew below folders that lanq created or
/// checked are not links, so nothing outside out/jobid is ever written. Never throws from <see cref="OnLine"/>.
/// </summary>
public sealed class FetchReceiver : IDisposable
{
    internal const int MaxDataLineChars = 4096;

    /// <summary>Skipped files kept for the report; the rest are only counted.</summary>
    internal const int MaxListedSkips = 200;

    internal const string Prefix = "LQF1 ";

    private static readonly HashSet<string> WorkerReasons = new(StringComparer.Ordinal)
    {
        "link", "hardlink", "special", "outside", "name", "count", "size", "total", "unreadable",
    };

    private readonly string _dir;
    private readonly Regex _matcher;
    private readonly FetchLimits _limits;
    private readonly List<FetchedFile> _fetched = [];
    private readonly List<FetchSkip> _skipped = [];
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase); // every file and folder taken, by case-insensitive key
    private readonly byte[] _decoded = new byte[MaxDataLineChars / 4 * 3];
    private int _moreSkipped;
    private long _total;
    private int _accepted;
    private bool _done;
    private string? _error;
    private bool _dirReady;

    // The file being received.
    private Current? _cur;

    /// <param name="dir">out/jobid: created on the first file; must not be a link.</param>
    /// <param name="ignoreCase">The worker matched case-insensitively (Windows), so the local pattern check does too.</param>
    public FetchReceiver(string dir, IReadOnlyList<string> patterns, FetchLimits limits, bool ignoreCase)
    {
        _dir = Path.GetFullPath(dir);
        _matcher = FetchPatterns.Matcher(patterns, ignoreCase);
        _limits = limits;
    }

    public bool Done => _done;

    private sealed class Current(string rel, long size, FileStream? stream, string fullPath)
    {
        public string Rel { get; } = rel;

        public long Size { get; } = size;

        /// <summary>Null when the file is being read past and dropped (refused before anything was written).</summary>
        public FileStream? Stream { get; } = stream;

        public string FullPath { get; } = fullPath;

        public IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public long Written { get; set; }

        /// <summary>Why the file is being read past without writing; null while it is written.</summary>
        public string? DropReason { get; init; }

        /// <summary>Already reported as failed: the rest of its frame is read past silently.</summary>
        public bool Silent { get; init; }
    }

    public void OnLine(string line)
    {
        try
        {
            Handle(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            if (_cur is not null)
            {
                Fail("could not write the file: " + ex.Message);
            }
            else
            {
                _error ??= "local write error: " + ex.Message;
            }
        }
    }

    private void Handle(string line)
    {
        if (_done)
        {
            return; // nothing after DONE counts
        }

        if (_cur is not null)
        {
            if (line.StartsWith(Prefix + "END ", StringComparison.Ordinal))
            {
                End(line[(Prefix.Length + 4)..]);
                return;
            }

            if (Data(line))
            {
                return;
            }

            // Not a data line: the frame is broken. The file fails; the line is read again as a frame line (it may start the next file).
            Fail("frame error (unexpected line inside the file)");
        }

        if (!line.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return; // noise between frames (a worker warning): ignored, never written anywhere
        }

        var rest = line[Prefix.Length..];
        if (rest == "DONE")
        {
            _done = true;
        }
        else if (rest.StartsWith("ERROR ", StringComparison.Ordinal))
        {
            _error ??= "the worker could not list the files: " + Word(rest[6..]);
        }
        else if (rest.StartsWith("SKIP ", StringComparison.Ordinal))
        {
            var parts = rest[5..].Split(' ', 2);
            var reason = WorkerReasons.Contains(parts[0]) ? Describe(parts[0]) : "skipped by the worker";
            Skip(parts.Length == 2 ? DecodePath(parts[1]) ?? "?" : "?", reason);
        }
        else if (rest.StartsWith("FILE ", StringComparison.Ordinal))
        {
            Begin(rest[5..]);
        }
    }

    private static string Word(string s) => s.Length <= 40 && s.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-') ? s : "?";

    internal static string Describe(string reason) => reason switch
    {
        "link" => "symbolic link, junction or reparse point (never followed)",
        "hardlink" => "hard link (the file has more than one name)",
        "special" => "not a regular file",
        "outside" => "resolves outside the job folder",
        "name" => "control characters in the name",
        "count" => "over the file count cap",
        "size" => "larger than the per-file cap",
        "total" => "over the total size cap",
        "unreadable" => "unreadable on the worker",
        _ => reason,
    };

    /// <summary>Strict base64 of UTF-8: anything else (or a path that does not decode) is null.</summary>
    private static string? DecodePath(string b64)
    {
        if (b64.Length is 0 or > 2048)
        {
            return null;
        }

        var bytes = new byte[b64.Length];
        if (!Convert.TryFromBase64String(b64, bytes, out var n))
        {
            return null;
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, 0, n);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private void Begin(string header)
    {
        var parts = header.Split(' ');
        long size = -1;
        var sizeOk = parts.Length == 2 && parts[0].Length is > 0 and <= 15 && parts[0].All(char.IsAsciiDigit)
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out size);
        var rel = parts.Length == 2 ? DecodePath(parts[1]) : null;
        string? refuse = null;
        if (!sizeOk || rel is null)
        {
            refuse = "frame error (bad FILE header)";
        }
        else if (FetchPath.Problem(rel) is { } why)
        {
            refuse = "unsafe path: " + why;
        }
        else if (!_matcher.IsMatch(rel))
        {
            refuse = "not matched by any --fetch pattern";
        }
        else if (_accepted >= _limits.MaxFiles)
        {
            refuse = Describe("count");
        }
        else if (size > _limits.MaxFileBytes)
        {
            refuse = Describe("size");
        }
        else if (_total + size > _limits.MaxTotalBytes)
        {
            refuse = Describe("total");
        }
        else if (Collision(rel) is { } other)
        {
            refuse = "name collides (ignoring case) with " + other;
        }

        rel ??= "?";
        if (refuse is not null)
        {
            _cur = new Current(rel, size, null, "") { DropReason = refuse };
            return;
        }

        var full = Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));
        string? linkProblem;
        try
        {
            linkProblem = PrepareFolders(rel);
        }
        catch (IOException ex)
        {
            linkProblem = "could not create its folder: " + ex.Message;
        }

        if (linkProblem is not null)
        {
            _cur = new Current(rel, size, null, "") { DropReason = linkProblem };
            return;
        }

        FileStream stream;
        try
        {
            stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024);
        }
        catch (IOException ex)
        {
            _cur = new Current(rel, size, null, "") { DropReason = "could not create the file (it may already exist): " + ex.Message };
            return;
        }

        Claim(rel);
        _accepted++;
        _cur = new Current(rel, size, stream, full);
    }

    /// <summary>The first name already taken that <paramref name="rel"/> (or one of its folders) collides with ignoring case, or null.</summary>
    private string? Collision(string rel)
    {
        var segments = rel.Split('/');
        for (var i = 1; i <= segments.Length; i++)
        {
            var prefix = string.Join('/', segments.Take(i));
            var key = (i == segments.Length ? "f:" : "d:") + prefix;
            var otherKind = (i == segments.Length ? "d:" : "f:") + prefix;
            if (_names.TryGetValue(otherKind, out var o) || (_names.TryGetValue(key, out o) && (i == segments.Length || o != prefix)))
            {
                return o;
            }
        }

        return null;
    }

    private void Claim(string rel)
    {
        var segments = rel.Split('/');
        for (var i = 1; i < segments.Length; i++)
        {
            var prefix = string.Join('/', segments.Take(i));
            _names.TryAdd("d:" + prefix, prefix);
        }

        _names["f:" + rel] = rel;
    }

    /// <summary>
    /// Creates out/jobid and every folder of <paramref name="rel"/> one level at a time. A folder that already exists must be a real folder,
    /// not a symlink or junction (or any reparse point); otherwise the file is refused. Returns why, or null.
    /// </summary>
    private string? PrepareFolders(string rel)
    {
        if (!_dirReady)
        {
            Directory.CreateDirectory(_dir);
            if (IsLink(_dir))
            {
                return "the output folder is a link";
            }

            _dirReady = true;
        }

        var path = _dir;
        var segments = rel.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            path = Path.Combine(path, segments[i]);
            if (File.Exists(path) && !Directory.Exists(path))
            {
                return "a file is in the way of its folder";
            }

            if (IsLink(path))
            {
                return "a folder on its path is a link";
            }

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                if (IsLink(path))
                {
                    return "a folder on its path is a link";
                }
            }
        }

        var target = Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));
        return IsLink(target) ? "the target is an existing link" : null;
    }

    /// <summary>True when something exists at <paramref name="path"/> and is a symlink, junction or other reparse point (dangling ones too).</summary>
    internal static bool IsLink(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) || new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>A base64 data line: decoded and written. False when the line is not one (the caller fails the file).</summary>
    private bool Data(string line)
    {
        var cur = _cur!;
        if (line.Length is 0 or > MaxDataLineChars || line.Length % 4 != 0 || !Convert.TryFromBase64String(line, _decoded, out var n))
        {
            return false;
        }

        if (cur.DropReason is not null)
        {
            return true; // reading past a refused file
        }

        if (cur.Written + n > cur.Size)
        {
            Fail($"the worker sent more than the {cur.Size.ToString(CultureInfo.InvariantCulture)} bytes it announced");
            _cur = new Current(cur.Rel, cur.Size, null, "") { DropReason = "failed", Silent = true };
            return true;
        }

        if (_total + n > _limits.MaxTotalBytes)
        {
            Fail(Describe("total"));
            _cur = new Current(cur.Rel, cur.Size, null, "") { DropReason = "failed", Silent = true };
            return true;
        }

        cur.Stream!.Write(_decoded, 0, n);
        cur.Hash.AppendData(_decoded, 0, n);
        cur.Written += n;
        _total += n;
        return true;
    }

    private void End(string hash)
    {
        var cur = _cur!;
        if (cur.DropReason is not null)
        {
            if (!cur.Silent)
            {
                Skip(cur.Rel, cur.DropReason);
            }

            cur.Hash.Dispose();
            _cur = null;
            return;
        }

        var actual = Convert.ToHexStringLower(cur.Hash.GetHashAndReset());
        if (cur.Written != cur.Size)
        {
            Fail($"size mismatch: announced {cur.Size.ToString(CultureInfo.InvariantCulture)} bytes, received {cur.Written.ToString(CultureInfo.InvariantCulture)}");
            return;
        }

        if (hash.Length != 64 || !string.Equals(hash, actual, StringComparison.Ordinal))
        {
            Fail("SHA-256 mismatch (the file changed or the stream was damaged)");
            return;
        }

        cur.Stream!.Dispose();
        cur.Hash.Dispose();
        _fetched.Add(new FetchedFile(cur.Rel, cur.Written, actual));
        _cur = null;
    }

    /// <summary>Fails the current file: its partial file is deleted, and the bytes it used no longer count against the total.</summary>
    private void Fail(string reason)
    {
        var cur = _cur!;
        _cur = null;
        cur.Hash.Dispose();
        if (cur.Stream is not null)
        {
            cur.Stream.Dispose();
            _total -= cur.Written;
            TryDelete(cur.FullPath);
        }

        if (!cur.Silent)
        {
            Skip(cur.Rel, cur.DropReason ?? reason);
        }
    }

    private void Skip(string path, string reason)
    {
        if (_skipped.Count >= MaxListedSkips)
        {
            _moreSkipped++;
            return;
        }

        var shown = JobRunner.SafeLine(path);
        _skipped.Add(new FetchSkip(shown.Length > 300 ? shown[..300] + "..." : shown, reason));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort; reported as failed either way
        }
    }

    /// <summary>
    /// The report. A file still open (the stream ended inside it: timeout, cut connection) is deleted and failed; a stream without DONE is an
    /// error. An output folder that stayed empty is removed again.
    /// </summary>
    public FetchReport Finish(string? problem = null)
    {
        if (_cur is not null)
        {
            Fail("the stream ended inside this file");
        }

        var error = problem ?? _error ?? (_done ? null : "the worker's file stream ended early (incomplete fetch)");
        if (_dirReady && _fetched.Count == 0)
        {
            try
            {
                Directory.Delete(_dir, recursive: false);
            }
            catch (IOException)
            {
                // folders were created for files that then failed; leave them
            }
        }

        return new FetchReport(_dir, _fetched.ToList(), _skipped.ToList(), error, _moreSkipped);
    }

    public void Dispose()
    {
        if (_cur is not null)
        {
            Fail("cancelled");
        }
    }
}
