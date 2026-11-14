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
/// <param name="CommittedConfigs">Content of the package-manager configs (<c>.npmrc</c>, <c>.pypirc</c>) in the snapshot, committed or uncommitted, by path, for the credential scan; null content means too large to check.</param>
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
    IReadOnlyDictionary<string, string?>? CommittedConfigs = null)
{
    /// <summary>
    /// Findings in the content of uncommitted files git shows as binary (0.9.2; see <see cref="Policy.BlobScan"/>), including the fail-closed
    /// scan-incomplete finding; scanned with the rest of the payload.
    /// </summary>
    public IReadOnlyList<Policy.SecretFinding> BinaryFindings { get; init; } = [];

    /// <summary>
    /// Findings of the text key-block scan (0.10.1; see <c>GitPayloadBuilder.TextKeyScan.cs</c>): a private key replaced in place in a changed
    /// text file, whose BEGIN line the diff does not show, including the fail-closed scan-incomplete finding; scanned with the rest of the payload.
    /// </summary>
    public IReadOnlyList<Policy.SecretFinding> TextKeyFindings { get; init; } = [];

    /// <summary>
    /// Uncommitted files that were over a cap and not read in full: files git shows as binary (0.9.2) and changed text files with a
    /// private-key BEGIN line (0.10.1). Reported in scanSkipped, never refused.
    /// </summary>
    public IReadOnlyList<Policy.ScanSkip> ScanSkipped { get; init; } = [];

    /// <summary>How many blobs the binary scan read (whole or a first part): 0 on a clean tree or when git shows nothing as binary.</summary>
    public int BlobsRead { get; init; }

    /// <summary>How many blobs the text key-block scan read (0.10.1): 0 unless a changed text file has a BEGIN line.</summary>
    public int TextBlobsRead { get; init; }
}

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

    /// <summary>lanq run --baseline: the base commit (<see cref="BaseRef.ResolveAsync"/>), resolved locally before anything is sent.</summary>
    /// <param name="named">--baseline-ref, or null for the default branch.</param>
    Task<BaseResolution> ResolveBaseAsync(string repoDir, string? named, CancellationToken ct) => BaseRef.ResolveAsync(repoDir, named, ct);

    /// <summary>
    /// lanq run --baseline: the payload of commit <paramref name="sha"/> as it is, a clean tree (no overlay), with its committed file names and
    /// package configs for the secret scan; built only when a baseline actually runs. Bundles and archives of it are built through
    /// <see cref="BuildBundleAsync"/> and <see cref="BuildArchiveAsync"/> with that sha.
    /// </summary>
    Task<JobPayload> BuildCommitAsync(string jobId, string repoDir, string outputDir, string sha, PayloadKinds kinds, CancellationToken ct) =>
        throw new InvalidOperationException("this payload builder cannot build a commit's payload");

    /// <summary>
    /// Whether commit <paramref name="ancestor"/> is <paramref name="descendant"/> or an ancestor of it in the local repository (false when either
    /// is unknown). A worker that fetched an older commit than its recorded base keeps that base (lanq run --baseline), and so does lanq.
    /// </summary>
    Task<bool> IsAncestorAsync(string repoDir, string ancestor, string descendant, CancellationToken ct) => Task.FromResult(false);

    /// <summary>The exact branch or tag <paramref name="name"/> names, as a full ref (<see cref="BaseRef.BranchOrTagAsync"/>): lanq.yml's baseline_ref must name one.</summary>
    Task<NamedRef> BranchOrTagAsync(string repoDir, string name, CancellationToken ct) => BaseRef.BranchOrTagAsync(repoDir, name, ct);
}
