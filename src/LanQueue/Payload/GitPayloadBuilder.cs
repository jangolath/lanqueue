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

            string? bundle = null;
            if (kinds.HasFlag(PayloadKinds.Bundle))
            {
                bundle = Path.Combine(outputDir, jobId + ".bundle");
                await Git(top, null, ct, "update-ref", refName, sha).ConfigureAwait(false);
                await Git(top, null, ct, "bundle", "create", bundle, refName).ConfigureAwait(false);
            }

            string? archive = null;
            if (kinds.HasFlag(PayloadKinds.Archive))
            {
                archive = Path.Combine(outputDir, jobId + ".tar.gz");
                var snapshot = await ReadSnapshotFiles(top, tree, ct).ConfigureAwait(false);
                try
                {
                    await ArchiveWriter.CreateAsync(archive, snapshot, ct).ConfigureAwait(false);
                }
                catch (ArgumentException ex)
                {
                    throw new InvalidOperationException("Cannot build an archive: " + ex.Message, ex);
                }
            }

            return new JobPayload(bundle, sha, changed, diff, archive);
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

    /// <summary>Reads every regular file of the snapshot tree (the throwaway index result) through one long-lived cat-file process.</summary>
    private static async Task<List<ArchiveFile>> ReadSnapshotFiles(string top, string tree, CancellationToken ct)
    {
        var listing = await Git(top, null, ct, "ls-tree", "-r", "-z", tree).ConfigureAwait(false);
        var wanted = new List<(string Sha, string Path, bool Exec)>();
        foreach (var rec in listing.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // "<mode> SP <type> SP <sha> TAB <path>"
            var tab = rec.IndexOf('\t', StringComparison.Ordinal);
            var meta = rec[..tab].Split(' ');
            if (meta[1] == "blob" && ArchiveWriter.IsRegularMode(meta[0]))
            {
                wanted.Add((meta[2], rec[(tab + 1)..], meta[0] == "100755"));
            }
        }

        var files = new List<ArchiveFile>(wanted.Count);
        if (wanted.Count == 0)
        {
            return files;
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
        var stdout = p.StandardOutput.BaseStream;
        foreach (var w in wanted)
        {
            var header = await ReadLine(stdout, ct).ConfigureAwait(false);
            var parts = header.Split(' ');
            if (parts.Length != 3 || parts[1] != "blob" || !long.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var size) || size > int.MaxValue)
            {
                throw new InvalidOperationException($"git cat-file returned '{header}' for {w.Path}.");
            }

            var buf = new byte[size];
            await stdout.ReadExactlyAsync(buf, ct).ConfigureAwait(false);
            await ReadLine(stdout, ct).ConfigureAwait(false); // trailing LF
            files.Add(new ArchiveFile(w.Path, w.Exec, buf));
        }

        await feed.ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException("git cat-file failed: " + (await err.ConfigureAwait(false)).Trim());
        }

        return files;
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
