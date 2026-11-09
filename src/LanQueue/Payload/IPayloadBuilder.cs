namespace LanQueue.Payload;

/// <param name="Sha">The snapshot commit: HEAD itself on a clean tree, otherwise a commit of the overlay whose parent is HEAD.</param>
/// <param name="ChangedFiles">Paths changed relative to HEAD (the uncommitted overlay).</param>
/// <param name="OverlayDiff">Unified diff of the overlay, used only for secret scanning.</param>
/// <param name="ArchivePath">Local tar.gz of the same snapshot, only when <see cref="PayloadKinds.Archive"/> was asked for up front (null otherwise).</param>
/// <param name="SkippedSymlinks">Symlinks left out of that archive.</param>
/// <param name="SkippedSubmodules">Submodules left out of that archive.</param>
/// <param name="RepoId">Names the worker's per-repository cache (<c>repos/&lt;RepoId&gt;.git</c>): the first 16 hex digits of the root commit; set when <see cref="PayloadKinds.Bundle"/> was asked for.</param>
/// <param name="Head">The HEAD commit the snapshot was taken on; recorded as the next incremental base once a worker has fetched it.</param>
/// <param name="CommittedFiles">Every path in the HEAD tree (committed files), for the secret file-name scan.</param>
/// <param name="CommittedConfigs">Content of committed package-manager configs (<c>.npmrc</c>, <c>.pypirc</c>) by path, for the credential scan; null content means too large to check.</param>
public sealed record JobPayload(
    string Sha,
    IReadOnlyList<string> ChangedFiles,
    string OverlayDiff,
    string? ArchivePath = null,
    int SkippedSymlinks = 0,
    int SkippedSubmodules = 0,
    string? RepoId = null,
    string? Head = null,
    IReadOnlyList<string>? CommittedFiles = null,
    IReadOnlyDictionary<string, string?>? CommittedConfigs = null);

/// <summary>A built archive plus how many entries a tar.gz cannot carry (symlinks, submodules) were left out.</summary>
public sealed record ArchiveBuild(string Path, int SkippedSymlinks, int SkippedSubmodules);

/// <summary>A built git bundle; <paramref name="Base"/> is the commit it is incremental on, or null for a full bundle.</summary>
public sealed record BundleBuild(string Path, string? Base);

[Flags]
public enum PayloadKinds
{
    None = 0,

    /// <summary>A git machine may take the job: compute <see cref="JobPayload.RepoId"/>. Bundles themselves are built per machine.</summary>
    Bundle = 1,
    Archive = 2,
}

public interface IPayloadBuilder
{
    Task<JobPayload> BuildAsync(string jobId, string repoDir, string outputDir, PayloadKinds kinds, CancellationToken ct);

    /// <summary>Builds the tar.gz for the snapshot commit <paramref name="sha"/> returned by <see cref="BuildAsync"/>; called only when an archive machine is about to get the job.</summary>
    Task<ArchiveBuild> BuildArchiveAsync(string jobId, string repoDir, string outputDir, string sha, CancellationToken ct);

    /// <summary>
    /// Bundles the snapshot <paramref name="sha"/>: incremental on <paramref name="baseSha"/> when that commit is still a local ancestor of
    /// the snapshot, otherwise full. Called only when a git machine is about to get the job.
    /// </summary>
    Task<BundleBuild> BuildBundleAsync(string jobId, string repoDir, string outputDir, string sha, string? baseSha, CancellationToken ct);
}
