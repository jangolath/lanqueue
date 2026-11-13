using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;

namespace LanQueue.Payload;

/// <summary>
/// Snapshots HEAD plus uncommitted and untracked (non-ignored) changes into a commit using a throwaway index,
/// so the user's index, branches and working tree are never touched, then bundles or archives it on demand.
/// </summary>
public sealed class GitPayloadBuilder : IPayloadBuilder
{
    /// <summary>
    /// Every local git call is bounded (0.9.1): git reads no stdin (closed at once), never prompts (GIT_TERMINAL_PROMPT=0, GCM_INTERACTIVE=never)
    /// and never pages (GIT_PAGER=cat), and a call that outlives its limit is killed with its process tree and fails with
    /// <see cref="GitTimeoutException"/>. This limit is for metadata (rev-parse, show-ref, ls-tree, cat-file, update-ref, ...).
    /// </summary>
    internal static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The limit for the calls that read the whole working tree or diff it (add -A, diff), or walk the history (rev-list, merge-base).</summary>
    internal static readonly TimeSpan WorkTreeTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The limit for building a payload file (bundle create, and the archive's cat-file --batch stream), which grows with the repository.</summary>
    internal static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The limit for one git call, by its subcommand. History walks (rev-list, merge-base) get the longer limit too: on a large history
    /// without a commit-graph they can take minutes.
    /// </summary>
    internal static TimeSpan TimeoutFor(string subcommand) => subcommand switch
    {
        "add" or "diff" or "rev-list" or "merge-base" => WorkTreeTimeout,
        "bundle" => BuildTimeout,
        _ => MetadataTimeout,
    };

    public async Task<JobPayload> BuildAsync(string jobId, string repoDir, string outputDir, PayloadKinds kinds, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var top = (await Git(repoDir, null, ct, "rev-parse", "--show-toplevel").ConfigureAwait(false)).Trim();
        var head = (await Git(top, null, ct, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
        var tmpIndex = Path.Combine(Path.GetTempPath(), $"lanq-index-{jobId}-{Guid.NewGuid():N}");
        var env = new Dictionary<string, string>
        {
            ["GIT_INDEX_FILE"] = tmpIndex,
            ["GIT_AUTHOR_NAME"] = "lanq",
            ["GIT_AUTHOR_EMAIL"] = "lanq@localhost",
            ["GIT_COMMITTER_NAME"] = "lanq",
            ["GIT_COMMITTER_EMAIL"] = "lanq@localhost",
        };
        try
        {
            await Git(top, env, ct, "read-tree", "HEAD").ConfigureAwait(false);

            // lanq's own state (logs, job history) must never ride along in a snapshot, even when not gitignored.
            await Git(top, env, ct, "add", "-A", "--", ".", ":(exclude,glob)**/.lanq/**").ConfigureAwait(false);
            var tree = (await Git(top, env, ct, "write-tree").ConfigureAwait(false)).Trim();
            var headTree = (await Git(top, null, ct, "rev-parse", "HEAD^{tree}").ConfigureAwait(false)).Trim();
            var changed = tree == headTree
                ? []
                : (await Git(top, env, ct, "diff", "--cached", "--no-ext-diff", "--no-textconv", "--name-only", "HEAD").ConfigureAwait(false))
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var diff = tree == headTree
                ? ""
                : await Git(top, env, ct, "diff", "--cached", "--no-ext-diff", "--no-textconv", "--no-color", "-U0", "HEAD").ConfigureAwait(false);
            var sha = tree == headTree
                ? head
                : (await Git(top, env, ct, "commit-tree", tree, "-p", head, "-m", "lanq snapshot " + jobId).ConfigureAwait(false)).Trim();

            var repoId = kinds.HasFlag(PayloadKinds.Bundle) ? await RepoIdAsync(top, ct).ConfigureAwait(false) : null;
            var committed = (await Git(top, null, ct, "ls-tree", "-r", "-z", "--name-only", "HEAD").ConfigureAwait(false))
                .Split('\0', StringSplitOptions.RemoveEmptyEntries);
            // Package-manager configs are read from the snapshot itself, so committed and uncommitted ones get the same content check.
            var snapshotFiles = sha == head
                ? committed
                : (await Git(top, null, ct, "ls-tree", "-r", "-z", "--name-only", sha).ConfigureAwait(false)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var configs = await PackageConfigsAsync(top, sha, snapshotFiles, ct).ConfigureAwait(false);

            ArchiveBuild? built = null;
            if (kinds.HasFlag(PayloadKinds.Archive))
            {
                built = await WriteArchiveAsync(top, tree, Path.Combine(outputDir, jobId + ".tar.gz"), ct).ConfigureAwait(false);
            }

            return new JobPayload(sha, changed, diff, built?.Path, built?.SkippedSymlinks ?? 0, built?.SkippedSubmodules ?? 0, repoId, head, committed, configs);
        }
        finally
        {
            // A git killed mid-write (timeout, Ctrl+C) can leave the index lock behind. Best effort: the real error must win.
            TryDelete(tmpIndex);
            TryDelete(tmpIndex + ".lock");
        }
    }

    /// <summary>
    /// The payload of a commit as it is, a clean tree (lanq run --baseline's base): no snapshot commit, no overlay. Its committed file names and
    /// package-manager configs are read for the same secret scans a snapshot gets; nothing else is built (bundles and archives come on demand).
    /// </summary>
    public async Task<JobPayload> BuildCommitAsync(string jobId, string repoDir, string outputDir, string sha, PayloadKinds kinds, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var top = (await Git(repoDir, null, ct, "rev-parse", "--show-toplevel").ConfigureAwait(false)).Trim();
        var commit = (await Git(top, null, ct, "rev-parse", "--verify", sha + "^{commit}").ConfigureAwait(false)).Trim();
        var repoId = kinds.HasFlag(PayloadKinds.Bundle) ? await RepoIdAsync(top, ct).ConfigureAwait(false) : null;
        var files = (await Git(top, null, ct, "ls-tree", "-r", "-z", "--name-only", commit).ConfigureAwait(false)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var configs = await PackageConfigsAsync(top, commit, files, ct).ConfigureAwait(false);
        return new JobPayload(commit, [], "", RepoId: repoId, Head: commit, CommittedFiles: files, CommittedConfigs: configs);
    }

    /// <summary>The content of each package-manager config among <paramref name="files"/> of commit <paramref name="sha"/>, for the credential scan.</summary>
    private static async Task<Dictionary<string, string?>> PackageConfigsAsync(string top, string sha, IEnumerable<string> files, CancellationToken ct)
    {
        var configs = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var path in files.Where(Policy.SecretScanner.IsPackageConfig))
        {
            // Read only small blobs (a bigger one is refused unread by the scan); the content never leaves this process.
            var size = long.Parse((await Git(top, null, ct, "cat-file", "-s", sha + ":" + path).ConfigureAwait(false)).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            configs[path] = size > Policy.SecretScanner.MaxConfigBytes ? null : await Git(top, null, ct, "cat-file", "-p", sha + ":" + path).ConfigureAwait(false);
        }

        return configs;
    }

    /// <summary>
    /// Bundles the snapshot <paramref name="sha"/> under <c>refs/lanq/&lt;jobId&gt;</c>. With a <paramref name="baseSha"/> that is still a
    /// local commit and an ancestor of the snapshot, the bundle is incremental (<c>--not &lt;base&gt;</c>). When the snapshot is the base itself
    /// or an ancestor of it (git refuses an empty bundle), it is incremental on the snapshot's own parents, which the worker has. Otherwise, or
    /// when the base is malformed, it is a full bundle.
    /// </summary>
    public async Task<BundleBuild> BuildBundleAsync(string jobId, string repoDir, string outputDir, string sha, string? baseSha, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var top = (await Git(repoDir, null, ct, "rev-parse", "--show-toplevel").ConfigureAwait(false)).Trim();
        var exclude = baseSha is null ? [] : await IncrementalExcludesAsync(top, baseSha, sha, ct).ConfigureAwait(false);
        var usedBase = exclude.Count > 0 ? baseSha : null;
        var bundle = Path.Combine(outputDir, usedBase is null ? jobId + ".bundle" : $"{jobId}-{usedBase}.bundle");
        var refName = $"refs/lanq/{jobId}";
        await Git(top, null, ct, "update-ref", refName, sha).ConfigureAwait(false);
        ExceptionDispatchInfo? failed = null;
        try
        {
            string[] args = ["bundle", "create", bundle, refName, .. exclude.Count > 0 ? ["--not", .. exclude] : Array.Empty<string>()];
            await Git(top, null, ct, args).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failed or killed bundle create (timeout, Ctrl+C) can leave its lock file; the next try must not trip over it. Best effort:
            // a delete that fails (Windows, a handle still open) must not replace the GitTimeoutException, which ends the retries.
            TryDelete(bundle + ".lock");
            failed = ExceptionDispatchInfo.Capture(ex);
        }

        if (failed is null)
        {
            await Git(top, null, CancellationToken.None, "update-ref", "-d", refName).ConfigureAwait(false);
            return new BundleBuild(bundle, usedBase);
        }

        // The build already failed: removing the temporary ref is best effort, and its own failure only adds a note to the real error.
        try
        {
            await Git(top, null, CancellationToken.None, "update-ref", "-d", refName).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            throw WithLeftoverRef(failed.SourceException, refName);
        }

        failed.Throw();
        throw failed.SourceException; // not reached
    }

    /// <summary>The commits to exclude for an incremental bundle on <paramref name="baseSha"/>, or none when only a full bundle is safe.</summary>
    private static async Task<IReadOnlyList<string>> IncrementalExcludesAsync(string top, string baseSha, string sha, CancellationToken ct)
    {
        try
        {
            if (!IsObjectId(baseSha) || (await GitRun(top, null, ct, "cat-file", "-e", baseSha + "^{commit}").ConfigureAwait(false)).Code != 0)
            {
                return [];
            }

            if ((await GitRun(top, null, ct, "merge-base", "--is-ancestor", baseSha, sha).ConfigureAwait(false)).Code == 0)
            {
                if (!string.Equals(baseSha, sha, StringComparison.Ordinal))
                {
                    return [baseSha];
                }
            }
            else if ((await GitRun(top, null, ct, "merge-base", "--is-ancestor", sha, baseSha).ConfigureAwait(false)).Code != 0)
            {
                return [];
            }

            // The snapshot is the base itself, or an ancestor of it (lanq run --baseline's base commit, sent after a snapshot on top of it): the
            // worker has every commit up to the base, so only the snapshot commit's own objects go, on its parents (a root commit: a full bundle).
            return (await Git(top, null, ct, "rev-parse", sha + "^@").ConfigureAwait(false))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (GitTimeoutException)
        {
            return []; // a full bundle is always safe
        }
    }

    /// <inheritdoc/>
    /// <remarks>A git call that timed out answers false: the caller then records HEAD as the worker's base, which is always safe.</remarks>
    public async Task<bool> IsAncestorAsync(string repoDir, string ancestor, string descendant, CancellationToken ct)
    {
        try
        {
            return IsObjectId(ancestor) && IsObjectId(descendant)
                && (await GitRun(repoDir, null, ct, "merge-base", "--is-ancestor", ancestor, descendant).ConfigureAwait(false)).Code == 0;
        }
        catch (GitTimeoutException)
        {
            return false;
        }
    }

    internal static bool IsObjectId(string s) =>
        s.Length is 40 or 64 && s.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

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
    /// <param name="timeout">The limit of the cat-file --batch stream: <see cref="BuildTimeout"/> unless a test gives a shorter one.</param>
    internal static async Task<ArchiveBuild> WriteArchiveAsync(string top, string tree, string archivePath, CancellationToken ct, TimeSpan? timeout = null)
    {
        var limit = timeout ?? BuildTimeout;
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

            var psi = NonInteractive(new ProcessStartInfo("git")
            {
                WorkingDirectory = top,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            psi.ArgumentList.Add("cat-file");
            psi.ArgumentList.Add("--batch");
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git.");

            // The whole stream is bounded by BuildTimeout: on expiry every read below is cancelled and git is killed (finally).
            var outer = ct;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            cts.CancelAfter(limit);
            ct = cts.Token;
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
            catch (OperationCanceledException) when (!outer.IsCancellationRequested)
            {
                throw new GitTimeoutException("cat-file", limit);
            }
            finally
            {
                Kill(p);
                WaitBriefly(p);
            }

            return new ArchiveBuild(archivePath, symlinks, submodules);
        }
        catch
        {
            TryDelete(archivePath); // best effort: the real error (a GitTimeoutException ends the retries) must win
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
        var (code, output, error) = await GitRun(dir, env, ct, args).ConfigureAwait(false);
        return code == 0 ? output : throw new InvalidOperationException($"git {args[0]} failed: {error.Trim()}");
    }

    internal static Task<(int Code, string Output, string Error)> GitRun(string dir, Dictionary<string, string>? env, CancellationToken ct, params string[] args) =>
        RunAsync("git", dir, env, TimeoutFor(args[0]), ct, args);

    /// <summary>
    /// Runs <paramref name="exe"/> (git, or a stand-in in tests) without a shell: stdin closed at once, stdout and stderr read concurrently (no
    /// pipe deadlock), and the whole call, reads included, bounded by <paramref name="timeout"/>. On expiry the process tree is killed and
    /// <see cref="GitTimeoutException"/> is thrown; on <paramref name="ct"/> (Ctrl+C) it is killed too and the cancellation propagates.
    /// </summary>
    internal static async Task<(int Code, string Output, string Error)> RunAsync(
        string exe, string dir, IReadOnlyDictionary<string, string>? env, TimeSpan timeout, CancellationToken ct, IReadOnlyList<string> args)
    {
        var psi = NonInteractive(new ProcessStartInfo(exe)
        {
            WorkingDirectory = dir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
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

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}.");
        p.StandardInput.Close();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var output = p.StandardOutput.ReadToEndAsync(cts.Token);
        var err = p.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return (p.ExitCode, await output.ConfigureAwait(false), await err.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            Kill(p);
            WaitBriefly(p); // so its file handles (index, locks) are released before the caller cleans up
            try
            {
                await Task.WhenAll(output, err).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // the reads were cancelled with the call, or the pipes closed with the killed process
            }

            ct.ThrowIfCancellationRequested();
            throw new GitTimeoutException(args.Count > 0 ? args[0] : exe, timeout);
        }
    }

    /// <summary>Removed from git's environment (0.9.1): lanq's local git calls always work on the repository they run in.</summary>
    internal static readonly string[] InheritedGitVariables =
        ["GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_EXTERNAL_DIFF", "GIT_INDEX_FILE"];

    /// <summary>
    /// git may never wait for a person: no terminal prompt (GIT_TERMINAL_PROMPT=0), no Git Credential Manager dialog (GCM_INTERACTIVE=never),
    /// and no pager (GIT_PAGER=cat; its output is redirected anyway). Every caller also redirects stdin.
    /// </summary>
    private static ProcessStartInfo NonInteractive(ProcessStartInfo psi)
    {
        // Inherited variables that would point git at another repository, object store, index or diff program. lanq sets GIT_INDEX_FILE
        // itself, after this, for the snapshot's temporary index.
        foreach (var name in InheritedGitVariables)
        {
            psi.Environment.Remove(name);
        }

        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";
        psi.Environment["GIT_PAGER"] = "cat";
        return psi;
    }

    /// <summary>
    /// <paramref name="error"/> with a note that the temporary ref <paramref name="refName"/> was left in the local repository (nothing prunes
    /// refs/lanq/* later) and how to delete it. A GitTimeoutException stays one (it ends the build's retries); another InvalidOperationException
    /// gets the note in a new one; anything else (Ctrl+C) is returned as it is.
    /// </summary>
    internal static Exception WithLeftoverRef(Exception error, string refName)
    {
        var note = $"; the local ref {refName} was left behind (delete it with: git update-ref -d {refName})";
        return error switch
        {
            GitTimeoutException t => new GitTimeoutException(t.Subcommand, t.Timeout, note),
            InvalidOperationException => new InvalidOperationException(error.Message + note, error),
            _ => error,
        };
    }

    /// <summary>Waits at most 5 s for a killed process to be gone.</summary>
    private static void WaitBriefly(Process p)
    {
        try
        {
            p.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // nothing to wait for
        }
    }

    /// <summary>Deletes a leftover file, best effort: a cleanup failure never replaces the error being reported.</summary>
    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // still held by a process that was just killed, or not ours to delete: left behind
        }
    }

    private static void Kill(Process p)
    {
        try
        {
            // Always the whole tree, even when git itself has exited: on Windows a child it started may still run and hold the pipes (on
            // Linux an orphan is reparented and outside the tree, so there this only reaches children of a git still running).
            p.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // it has exited (InvalidOperationException), or cannot be killed: nothing more to do
        }
    }
}

/// <summary>
/// A local git call that outlived its limit and was killed (<see cref="GitPayloadBuilder.TimeoutFor"/>). An <see cref="InvalidOperationException"/>,
/// so every path that reports a failed git call (InfraError 70 for a snapshot or bundle, Rejected 2 for a --baseline base) reports this one too.
/// </summary>
/// <param name="note">Appended to the message (a ref left behind by the failed build).</param>
public sealed class GitTimeoutException(string subcommand, TimeSpan timeout, string note = "")
    : InvalidOperationException($"git {subcommand} did not finish within {Ssh.ProcessSshClient.Describe(timeout)} and was stopped (lanq never gives git any input; look for a stale lock or a slow disk){note}")
{
    public string Subcommand => subcommand;

    public TimeSpan Timeout => timeout;
}
