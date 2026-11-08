namespace LanQueue.Payload;

/// <param name="BundlePath">Local git bundle containing HEAD plus a snapshot commit of uncommitted changes.</param>
/// <param name="ChangedFiles">Paths changed relative to HEAD (the uncommitted overlay).</param>
/// <param name="OverlayDiff">Unified diff of the overlay, used only for secret scanning.</param>
public sealed record JobPayload(string BundlePath, string Sha, IReadOnlyList<string> ChangedFiles, string OverlayDiff);

public interface IPayloadBuilder
{
    Task<JobPayload> BuildAsync(string jobId, string repoDir, string outputDir, CancellationToken ct);
}
