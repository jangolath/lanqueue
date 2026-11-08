namespace LanQueue.Payload;

/// <param name="BundlePath">Local git bundle containing HEAD plus a snapshot commit of uncommitted changes (null unless requested).</param>
/// <param name="ChangedFiles">Paths changed relative to HEAD (the uncommitted overlay).</param>
/// <param name="OverlayDiff">Unified diff of the overlay, used only for secret scanning.</param>
/// <param name="ArchivePath">Local tar.gz of the same snapshot, only when <see cref="PayloadKinds.Archive"/> was asked for up front (null otherwise).</param>
/// <param name="SkippedSymlinks">Symlinks left out of that archive.</param>
/// <param name="SkippedSubmodules">Submodules left out of that archive.</param>
public sealed record JobPayload(
    string? BundlePath,
    string Sha,
    IReadOnlyList<string> ChangedFiles,
    string OverlayDiff,
    string? ArchivePath = null,
    int SkippedSymlinks = 0,
    int SkippedSubmodules = 0);

/// <summary>A built archive plus how many entries a tar.gz cannot carry (symlinks, submodules) were left out.</summary>
public sealed record ArchiveBuild(string Path, int SkippedSymlinks, int SkippedSubmodules);

[Flags]
public enum PayloadKinds
{
    None = 0,
    Bundle = 1,
    Archive = 2,
}

public interface IPayloadBuilder
{
    Task<JobPayload> BuildAsync(string jobId, string repoDir, string outputDir, PayloadKinds kinds, CancellationToken ct);

    /// <summary>Builds the tar.gz for the snapshot commit <paramref name="sha"/> returned by <see cref="BuildAsync"/>; called only when an archive machine is about to get the job.</summary>
    Task<ArchiveBuild> BuildArchiveAsync(string jobId, string repoDir, string outputDir, string sha, CancellationToken ct);
}
