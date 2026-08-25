namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Encrypted recovery-only payload attached to one immutable public run-record row. The public ledger remains safe
/// for operator/UI reads; this sidecar is read only by the workflow engine when rebuilding runtime scope.
/// </summary>
public sealed class WorkflowRunSensitiveRecordPayload
{
    public Guid RecordId { get; set; }
    public Guid RunId { get; set; }
    public Guid TeamId { get; set; }
    public string PayloadKind { get; set; } = default!;
    public string? Ciphertext { get; set; }
    public Guid? CiphertextArtifactId { get; set; }
    public long CiphertextSizeBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
