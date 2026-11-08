using System.Diagnostics;
using System.Text;

namespace LanQueue.Payload;

/// <summary>
/// Snapshots HEAD plus uncommitted and untracked (non-ignored) changes into a commit using a throwaway index,
/// so the user's index, branches and working tree are never touched, then bundles it.
/// </summary>
public sealed class GitPayloadBuilder : IPayloadBuilder
{
    public async Task<JobPayload> BuildAsync(string jobId, string repoDir, string outputDir, CancellationToken ct)
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
            await Git(top, env, ct, "add", "-A").ConfigureAwait(false);
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

            var bundle = Path.Combine(outputDir, jobId + ".bundle");
            await Git(top, null, ct, "update-ref", refName, sha).ConfigureAwait(false);
            await Git(top, null, ct, "bundle", "create", bundle, refName).ConfigureAwait(false);
            return new JobPayload(bundle, sha, changed, diff);
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
