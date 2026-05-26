namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Content-addressable artifact. Records reference these via id in their payload_json (e.g.
/// <c>external_call.completed</c> stores <c>response_artifact_id</c>); the run-detail UI
/// fetches the bytes lazily when the operator expands the artifact card.
///
/// Append-only by trigger. Per-team dedup by (team_id, sha256) so storing identical bytes
/// twice from the same team returns the existing row. Exactly one of
/// <see cref="InlineBytes"/> / <see cref="StorageUrl"/> is set — the threshold is enforced
/// by <c>IArtifactStore.PutAsync</c>.
/// </summary>
public class WorkflowArtifact : IEntity<Guid>
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }

    /// <summary>SHA-256 of the raw bytes, hex-lowercase (64 chars).</summary>
    public string Sha256 { get; set; } = default!;

    /// <summary>MIME type. Application-supplied; not validated against bytes by the store.</summary>
    public string ContentType { get; set; } = default!;

    /// <summary>Total content size in bytes. Mirrors <c>inline_bytes.length</c> for inline rows.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Bytes for small artifacts (NULL when content lives at <see cref="StorageUrl"/>).</summary>
    public byte[]? InlineBytes { get; set; }

    /// <summary>External storage URL for large artifacts (NULL when inline).</summary>
    public string? StorageUrl { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Team Team { get; set; } = default!;
}
