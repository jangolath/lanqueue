namespace LanQueue.Payload;

/// <param name="BundlePath">Local git bundle containing HEAD plus a snapshot commit of uncommitted changes (null unless requested).</param>
/// <param name="ChangedFiles">Paths changed relative to HEAD (the uncommitted overlay).</param>
/// <param name="OverlayDiff">Unified diff of the overlay, used only for secret scanning.</param>
/// <param name="ArchivePath">Local tar.gz of the same snapshot for machines using <c>transfer: archive</c> (null unless requested).</param>
public sealed record JobPayload(string? BundlePath, string Sha, IReadOnlyList<string> ChangedFiles, string OverlayDiff, string? ArchivePath = null);

[Flags]
public enum PayloadKinds
{
    Bundle = 1,
    Archive = 2,
}

public interface IPayloadBuilder
{
    Task<JobPayload> BuildAsync(string jobId, string repoDir, string outputDir, PayloadKinds kinds, CancellationToken ct);
}
