namespace LanQueue.Payload;

/// <summary>
/// The commit a lanq run --baseline rerun uses (<see cref="BaseRef.ResolveAsync"/>).
/// </summary>
/// <param name="Ref">What it came from: the default branch found (origin/main) or --baseline-ref as given.</param>
/// <param name="Sha">The full commit id; null when it could not be found in a shallow clone (<paramref name="Problem"/> says so; the baseline is then inconclusive).</param>
/// <param name="Label">How lanq names it in its lines: "merge-base with origin/main", or the ref itself when the base is that commit.</param>
/// <param name="Problem">Why <paramref name="Sha"/> is null, or null.</param>
public sealed record BaseCommit(string Ref, string? Sha, string Label, string? Problem = null)
{
    /// <summary>The first 7 hex digits of <see cref="Sha"/> (jobs.jsonl commit, the text lines), or "?" when it is unknown.</summary>
    public string Short => Sha is { Length: >= 7 } s ? s[..7] : "?";
}

/// <summary>A resolved base (<see cref="Base"/>) or why --baseline / --baseline-ref was refused (<see cref="Error"/>, Rejected 2).</summary>
public sealed record BaseResolution(BaseCommit? Base, string? Error);

/// <summary>
/// Finds the base commit of lanq run --baseline, locally and before anything is sent (a bad ref is refused at once):
/// <list type="bullet">
/// <item>--baseline: the merge-base of HEAD and the default branch, the first of refs/remotes/origin/HEAD (its target), origin/main,
/// origin/master, main and master that exists.</item>
/// <item>--baseline-ref REF: REF verified as a commit, then the merge-base of HEAD and REF (so a branch that moved on since you branched
/// still compares with where you started). When git finds no merge-base, REF itself is used only if it is an ancestor of HEAD (with a
/// merge-base found that is always the case, so this only matters to an odd history); otherwise it is refused as not an ancestor of HEAD.</item>
/// <item>No merge-base in a shallow clone (the history that would hold it was not fetched): not refused, since the job itself can run; the
/// base is unknown and the baseline ends inconclusive, "could not find the base commit (shallow clone?)". Shallowness cannot tell whether a
/// merge-base would exist, so refusing up front would refuse runs that are fine.</item>
/// <item>No merge-base in a full clone: refused (unrelated histories).</item>
/// </list>
/// A base equal to the snapshot (on the base commit, nothing uncommitted) is not decided here: the runner then skips the baseline. On the base
/// commit WITH uncommitted changes the base is HEAD itself, a clean tree.
/// </summary>
public static class BaseRef
{
    /// <summary>The branches tried, in order, after refs/remotes/origin/HEAD.</summary>
    public static readonly IReadOnlyList<string> DefaultBranches = ["origin/main", "origin/master", "main", "master"];

    /// <summary>The longest --baseline-ref accepted.</summary>
    public const int MaxRefLength = 256;

    /// <summary>The Problem of a base that a shallow clone does not hold.</summary>
    public const string ShallowProblem = "could not find the base commit (shallow clone?)";

    /// <summary>Why a --baseline-ref value is unusable before git is asked (empty, too long, control characters, or an option), or null.</summary>
    public static string? CheckRefText(string named) =>
        named.Length == 0 || named.Length > MaxRefLength || named.Any(char.IsControl) || named.StartsWith('-') || named.Contains("..", StringComparison.Ordinal)
            ? "--baseline-ref must name one branch, tag or commit (for example --baseline-ref origin/main or --baseline-ref v1.2.0)."
            : null;

    /// <summary>Why a lanq.yml baseline_ref was refused (after "lanq.yml: job 'x' (line N): ").</summary>
    public const string PlanRefMessage = "baseline_ref must name a branch or tag (a commit can be given on the command line with --baseline-ref)";

    /// <summary>The namespaces a lanq.yml baseline_ref may resolve in.</summary>
    private static readonly string[] NamedRefs = ["refs/heads/", "refs/remotes/", "refs/tags/"];

    /// <summary>
    /// Whether <paramref name="name"/> is a branch or tag name: it passes <see cref="CheckRefText"/>, has no revision syntax (~ ^ : @{ and the
    /// like), is not HEAD or another pseudo-ref, and git resolves it to a full ref under refs/heads/, refs/remotes/ or refs/tags/. A commit id
    /// (full or abbreviated), HEAD, HEAD~n
    /// or anything else is not.
    /// </summary>
    public static async Task<bool> IsBranchOrTagAsync(string repoDir, string name, CancellationToken ct)
    {
        // HEAD and the other pseudo-refs (FETCH_HEAD, ORIG_HEAD, ...) follow whatever is checked out or fetched: not a branch or tag name.
        if (name == "HEAD" || name.EndsWith("_HEAD", StringComparison.Ordinal) || CheckRefText(name) is not null || name.IndexOfAny(['~', '^', ':', '@', '{', '}', '\\', '*', '?', '[', ' ']) >= 0)
        {
            return false;
        }

        var (code, output, _) = await GitPayloadBuilder.GitRun(repoDir, null, ct, "rev-parse", "--symbolic-full-name", name).ConfigureAwait(false);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return code == 0 && lines.Length == 1 && NamedRefs.Any(p => lines[0].StartsWith(p, StringComparison.Ordinal) && lines[0].Length > p.Length);
    }

    /// <param name="repoDir">Any folder inside the repository.</param>
    /// <param name="named">--baseline-ref, or null for the default branch.</param>
    public static async Task<BaseResolution> ResolveAsync(string repoDir, string? named, CancellationToken ct)
    {
        if (named is not null && CheckRefText(named) is { } bad)
        {
            return new BaseResolution(null, bad);
        }

        var (topCode, topOut, topErr) = await GitPayloadBuilder.GitRun(repoDir, null, ct, "rev-parse", "--show-toplevel").ConfigureAwait(false);
        if (topCode != 0)
        {
            return new BaseResolution(null, "--baseline: could not read the repository: " + FirstLine(topErr));
        }

        var top = topOut.Trim();
        if (await Commit(top, "HEAD", ct).ConfigureAwait(false) is not { } head)
        {
            return new BaseResolution(null, "--baseline: the repository has no commit yet, so there is no base to compare with.");
        }

        string refName;
        string refSha;
        if (named is null)
        {
            var found = await DefaultBranchAsync(top, ct).ConfigureAwait(false);
            if (found is null)
            {
                return new BaseResolution(
                    null,
                    "--baseline: no default branch found (origin/HEAD, " + string.Join(", ", DefaultBranches) + "); name the base with --baseline-ref REF.");
            }

            (refName, refSha) = found.Value;
        }
        else
        {
            // The ref is passed as one argument after options git reads, with ^{commit}, so it is never read as an option (CheckRefText also
            // refuses a leading '-').
            if (await Commit(top, named, ct).ConfigureAwait(false) is not { } sha)
            {
                return new BaseResolution(null, $"--baseline-ref {named} is not a branch, tag or commit in this repository.");
            }

            (refName, refSha) = (named, sha);
        }

        var (mbCode, mbOut, _) = await GitPayloadBuilder.GitRun(top, null, ct, "merge-base", head, refSha).ConfigureAwait(false);
        var mergeBase = mbCode == 0 ? mbOut.Trim() : null;
        if (mergeBase is { Length: > 0 } && GitPayloadBuilder.IsObjectId(mergeBase))
        {
            return new BaseResolution(new BaseCommit(refName, mergeBase, Label(refName, mergeBase, refSha)), null);
        }

        if (named is not null && (await GitPayloadBuilder.GitRun(top, null, ct, "merge-base", "--is-ancestor", refSha, head).ConfigureAwait(false)).Code == 0)
        {
            return new BaseResolution(new BaseCommit(refName, refSha, refName), null);
        }

        var (_, shallow, _) = await GitPayloadBuilder.GitRun(top, null, ct, "rev-parse", "--is-shallow-repository").ConfigureAwait(false);
        if (shallow.Trim() == "true")
        {
            return new BaseResolution(new BaseCommit(refName, null, "merge-base with " + refName, ShallowProblem), null);
        }

        return new BaseResolution(
            null,
            named is null
                ? $"--baseline: HEAD has no common history with {refName}; name the base with --baseline-ref REF."
                : $"--baseline-ref {named} is not an ancestor of HEAD (no common history).");
    }

    /// <summary>"origin/main" when the base is that branch's tip, else "merge-base with origin/main".</summary>
    private static string Label(string refName, string mergeBase, string refSha) =>
        string.Equals(mergeBase, refSha, StringComparison.Ordinal) ? refName : "merge-base with " + refName;

    /// <summary>The default branch: origin/HEAD's target, else the first of <see cref="DefaultBranches"/> that is a commit; with its sha.</summary>
    private static async Task<(string Name, string Sha)?> DefaultBranchAsync(string top, CancellationToken ct)
    {
        var (code, output, _) = await GitPayloadBuilder.GitRun(top, null, ct, "symbolic-ref", "-q", "refs/remotes/origin/HEAD").ConfigureAwait(false);
        var candidates = new List<string>();
        const string Remotes = "refs/remotes/";
        if (code == 0 && output.Trim() is { } target && target.StartsWith(Remotes, StringComparison.Ordinal) && target.Length > Remotes.Length)
        {
            candidates.Add(target[Remotes.Length..]);
        }

        candidates.AddRange(DefaultBranches);
        foreach (var name in candidates.Distinct(StringComparer.Ordinal))
        {
            if (CheckRefText(name) is null && await Commit(top, name, ct).ConfigureAwait(false) is { } sha)
            {
                return (name, sha);
            }
        }

        return null;
    }

    /// <summary>The full id of the commit <paramref name="rev"/> names, or null when it names none.</summary>
    private static async Task<string?> Commit(string top, string rev, CancellationToken ct)
    {
        var (code, output, _) = await GitPayloadBuilder.GitRun(top, null, ct, "rev-parse", "--verify", "-q", rev + "^{commit}").ConfigureAwait(false);
        var sha = output.Trim();
        return code == 0 && GitPayloadBuilder.IsObjectId(sha) ? sha : null;
    }

    private static string FirstLine(string text) => text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "git failed";
}
