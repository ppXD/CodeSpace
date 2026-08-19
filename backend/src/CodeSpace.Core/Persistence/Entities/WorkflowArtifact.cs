namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Content-addressable artifact. Records reference these via id in their payload_json (e.g.
/// <c>external_call.completed</c> stores <c>response_artifact_id</c>); the run-detail UI
/// fetches the bytes lazily when the operator expands the artifact card.
///
/// Never updated: the trigger from migration 0016 rejects UPDATE outright, since the sha IS the identity. A row can be
/// DELETED only by a purge that asked for the permission in its own session, which today means exactly one caller —
/// <see cref="Services.Workflows.Artifacts.Retention.IArtifactRetentionReaper"/>, and only for an artifact carrying a
/// retention declaration that no reference site points at.
///
/// Per-team dedup by (team_id, sha256) so storing identical bytes twice from the same team returns the existing row.
/// Exactly one of <see cref="InlineBytes"/> / <see cref="StorageUrl"/> / <see cref="CasArtifactObjectId"/> is set — the
/// threshold decides inline vs offloaded and the team's route decides where an offloaded blob goes.
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

    /// <summary>External storage URL for large artifacts held by the local blob backend (NULL when inline or routed).</summary>
    public string? StorageUrl { get; set; }

    /// <summary>
    /// The <c>artifact_object</c> this row's bytes were placed under when the team routes <c>workflow-artifact/v1</c>
    /// through a configured storage profile (NULL when inline or on the local backend). The profile revisions those
    /// bytes live under are recorded on the object's <c>artifact_location</c> rows; a read resolves through those
    /// durable locations — never through the current route. The row records the object, not one chosen location, so
    /// a second location for the same object (replication, backfill) would be an equally valid source for the read.
    /// </summary>
    public Guid? CasArtifactObjectId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Team Team { get; set; } = default!;
}
