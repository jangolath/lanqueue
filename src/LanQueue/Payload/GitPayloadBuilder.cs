using System.Diagnostics;
using System.Text;

namespace LanQueue.Payload;

/// <summary>
/// Snapshots HEAD plus uncommitted and untracked (non-ignored) changes into a commit using a throwaway index,
/// so the user's index, branches and working tree are never touched, then bundles it.
/// </summary>
public sealed class GitPayloadBuilder : IPayloadBuilder
{
    public async Task<JobPayload> BuildAsync(string jobId, string repoDir, string outputDir, PayloadKinds kinds, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var top = (await Git(repoDir, null, ct, "rev-parse", "--show-toplevel").ConfigureAwait(false)).Trim();
        var head = (await Git(top, null, ct, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
        var tmpIndex = Path.Combine(Path.GetTempPath(), $"lanq-index-{jobId}");
        var env = new Dictionary<string, string>
        {
            ["GIT_INDEX_FILE"] = tmpIndex,
            ["GIT_AUTHOR_NAME"] = "lanq",
            ["GIT_AUTHOR_EMAIL"] = "lanq@localhost",
            ["GIT_COMMITTER_NAME"] = "lanq",
            ["GIT_COMMITTER_EMAIL"] = "lanq@localhost",
        };
        var refName = $"refs/lanq/{jobId}";
        try
        {
            await Git(top, env, ct, "read-tree", "HEAD").ConfigureAwait(false);

            // lanq's own state (logs, job history) must never ride along in a snapshot, even when not gitignored.
            await Git(top, env, ct, "add", "-A", "--", ".", ":(exclude,glob)**/.lanq/**").ConfigureAwait(false);
            var tree = (await Git(top, env, ct, "write-tree").ConfigureAwait(false)).Trim();
            var headTree = (await Git(top, null, ct, "rev-parse", "HEAD^{tree}").ConfigureAwait(false)).Trim();
            var changed = tree == headTree
                ? []
                : (await Git(top, env, ct, "diff", "--cached", "--name-only", "HEAD").ConfigureAwait(false))
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var diff = tree == headTree
                ? ""
                : await Git(top, env, ct, "diff", "--cached", "--no-color", "-U0", "HEAD").ConfigureAwait(false);
            var sha = tree == headTree
                ? head
                : (await Git(top, env, ct, "commit-tree", tree, "-p", head, "-m", "lanq snapshot " + jobId).ConfigureAwait(false)).Trim();

            string? bundle = null, repoId = null;
            if (kinds.HasFlag(PayloadKinds.Bundle))
            {
                repoId = await RepoIdAsync(top, ct).ConfigureAwait(false);
                bundle = Path.Combine(outputDir, jobId + ".bundle");
                await Git(top, null, ct, "update-ref", refName, sha).ConfigureAwait(false);
                await Git(top, null, ct, "bundle", "create", bundle, refName).ConfigureAwait(false);
            }

            ArchiveBuild? built = null;
            if (kinds.HasFlag(PayloadKinds.Archive))
            {
                built = await WriteArchiveAsync(top, tree, Path.Combine(outputDir, jobId + ".tar.gz"), ct).ConfigureAwait(false);
            }

            return new JobPayload(bundle, sha, changed, diff, built?.Path, built?.SkippedSymlinks ?? 0, built?.SkippedSubmodules ?? 0, repoId);
        }
        finally
        {
            try
            {
                await Git(top, null, CancellationToken.None, "update-ref", "-d", refName).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // ref was never created
            }

            File.Delete(tmpIndex);
        }
    }

    /// <summary>
    /// Names the worker's cache for this repository: the first 16 hex digits of its root commit (the parentless commit reachable from
    /// HEAD; with several roots, the ordinally smallest sha), so every clone of the same project shares one cache and unrelated
    /// projects never do.
    /// </summary>
    internal static async Task<string> RepoIdAsync(string top, CancellationToken ct)
    {
        var roots = (await Git(top, null, ct, "rev-list", "--max-parents=0", "HEAD").ConfigureAwait(false))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return roots.Min(StringComparer.Ordinal)![..16];
    }

    /// <summary>
    /// Builds the tar.gz for an already-built snapshot commit (the sha from <see cref="BuildAsync"/>), so a job that never
    /// needs an archive never pays for one. The commit stays readable in the object store after the temporary ref is gone.
    /// </summary>
    public async Task<ArchiveBuild> BuildArchiveAsync(string jobId, string repoDir, string outputDir, string sha, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var top = (await Git(repoDir, null, ct, "rev-parse", "--show-toplevel").ConfigureAwait(false)).Trim();
        var tree = (await Git(top, null, ct, "rev-parse", sha + "^{tree}").ConfigureAwait(false)).Trim();
        return await WriteArchiveAsync(top, tree, Path.Combine(outputDir, jobId + ".tar.gz"), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes every regular file of the snapshot tree. Content is streamed from one long-lived cat-file process straight into the
    /// tar entry (exactly the blob's size is read), so memory stays about one file. Symlinks and submodules are counted, not archived.
    /// </summary>
    private static async Task<ArchiveBuild> WriteArchiveAsync(string top, string tree, string archivePath, CancellationToken ct)
    {
        var listing = await Git(top, null, ct, "ls-tree", "-r", "-z", tree).ConfigureAwait(false);
        var wanted = new List<(string Sha, string Path, bool Exec)>();
        int symlinks = 0, submodules = 0;
        foreach (var rec in listing.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // "<mode> SP <type> SP <sha> TAB <path>"
            var tab = rec.IndexOf('\t', StringComparison.Ordinal);
            var meta = rec[..tab].Split(' ');
            if (meta[1] == "blob" && ArchiveWriter.IsRegularMode(meta[0]))
            {
                wanted.Add((meta[2], rec[(tab + 1)..], meta[0] == "100755"));
            }
            else if (meta[0] == "120000")
            {
                symlinks++;
            }
            else if (meta[0] == "160000")
            {
                submodules++;
            }
        }

        // cat-file answers in request order and the tar entries are written in ordinal path order: request in that order.
        wanted.Sort((x, y) => string.CompareOrdinal(x.Path, y.Path));
        try
        {
            if (wanted.Count == 0)
            {
                await ArchiveWriter.CreateAsync(archivePath, [], ct).ConfigureAwait(false);
                return new ArchiveBuild(archivePath, symlinks, submodules);
            }

            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = top,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("cat-file");
            psi.ArgumentList.Add("--batch");
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git.");
            try
            {
                var err = p.StandardError.ReadToEndAsync(ct);
                var feed = Task.Run(
                    async () =>
                    {
                        foreach (var w in wanted)
                        {
                            await p.StandardInput.WriteAsync(w.Sha + "\n").ConfigureAwait(false);
                        }

                        p.StandardInput.Close();
                    },
                    ct);
                var blobs = new BlobStream(p.StandardOutput.BaseStream);
                var files = wanted.Select((w, i) => new ArchiveFile(w.Path, w.Exec, c => blobs.NextAsync(i, w.Path, c))).ToList();
                try
                {
                    await ArchiveWriter.CreateAsync(archivePath, files, ct).ConfigureAwait(false);
                }
                catch (ArgumentException ex)
                {
                    throw new InvalidOperationException("Cannot build an archive: " + ex.Message, ex);
                }

                await blobs.FinishAsync(ct).ConfigureAwait(false);
                await feed.ConfigureAwait(false);
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
                if (p.ExitCode != 0)
                {
                    throw new InvalidOperationException("git cat-file failed: " + (await err.ConfigureAwait(false)).Trim());
                }
            }
            finally
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                }
            }

            return new ArchiveBuild(archivePath, symlinks, submodules);
        }
        catch
        {
            File.Delete(archivePath);
            throw;
        }
    }

    /// <summary>Hands out the blobs of a <c>git cat-file --batch</c> stream one at a time, strictly in request order, each bounded to its size.</summary>
    private sealed class BlobStream(Stream stdout)
    {
        private int _next;
        private BoundedReadStream? _current;
        private bool _lfPending;

        public async ValueTask<Stream> NextAsync(int index, string path, CancellationToken ct)
        {
            if (index != _next)
            {
                throw new InvalidOperationException($"Archive entries were requested out of order at {path}.");
            }

            await FinishCurrentAsync(ct).ConfigureAwait(false);
            var header = await ReadLine(stdout, ct).ConfigureAwait(false);
            var parts = header.Split(' ');
            if (parts.Length != 3 || parts[1] != "blob"
                || !long.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var size))
            {
                throw new InvalidOperationException($"git cat-file returned '{header}' for {path}.");
            }

            _next++;
            _lfPending = true;
            _current = new BoundedReadStream(stdout, size);
            return _current;
        }

        public Task FinishAsync(CancellationToken ct) => FinishCurrentAsync(ct);

        private async Task FinishCurrentAsync(CancellationToken ct)
        {
            if (_current is not null)
            {
                await _current.DrainAsync(ct).ConfigureAwait(false);
                _current = null;
            }

            if (_lfPending)
            {
                await ReadLine(stdout, ct).ConfigureAwait(false); // trailing LF after the content
                _lfPending = false;
            }
        }
    }

    /// <summary>Read-only, forward-only view of exactly <c>total</c> bytes of the underlying stream; disposing it leaves the underlying stream open.</summary>
    private sealed class BoundedReadStream(Stream inner, long total) : Stream
    {
        private readonly long _total = total;
        private long _remaining = total;

        public override bool CanRead => true;

        // TarWriter insists on a "seekable" stream to learn the size up front; only position queries are supported.
        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => _total;

        public override long Position
        {
            get => _total - _remaining;
            set
            {
                if (value != Position)
                {
                    throw new NotSupportedException();
                }
            }
        }

        public async Task DrainAsync(CancellationToken ct)
        {
            var scratch = new byte[8192];
            while (_remaining > 0)
            {
                if (await ReadAsync(scratch, ct).ConfigureAwait(false) == 0)
                {
                    break;
                }
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = ClampRead(buffer.Length);
            if (n == 0)
            {
                return 0;
            }

            var got = inner.Read(buffer[..n]);
            return Account(got);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = ClampRead(buffer.Length);
            if (n == 0)
            {
                return 0;
            }

            var got = await inner.ReadAsync(buffer[..n], cancellationToken).ConfigureAwait(false);
            return Account(got);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            origin == SeekOrigin.Current && offset == 0 ? Position : throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int ClampRead(int wanted) => (int)Math.Min(wanted, _remaining);

        private int Account(int got)
        {
            if (got == 0)
            {
                throw new InvalidOperationException("git cat-file ended before the whole file was read.");
            }

            _remaining -= got;
            return got;
        }
    }

    private static async Task<string> ReadLine(Stream s, CancellationToken ct)
    {
        var bytes = new List<byte>(64);
        var one = new byte[1];
        while (true)
        {
            if (await s.ReadAsync(one, ct).ConfigureAwait(false) == 0)
            {
                break;
            }

            if (one[0] == (byte)'\n')
            {
                break;
            }

            bytes.Add(one[0]);
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static async Task<string> Git(string dir, Dictionary<string, string>? env, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        if (env is not null)
        {
            foreach (var (k, v) in env)
            {
                psi.Environment[k] = v;
            }
        }

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git.");
        var err = p.StandardError.ReadToEndAsync(ct);
        var output = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {args[0]} failed: {(await err.ConfigureAwait(false)).Trim()}");
        }

        return output;
    }
}
