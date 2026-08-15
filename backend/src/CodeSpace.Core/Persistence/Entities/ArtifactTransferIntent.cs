namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Provider-neutral upload/copy saga. Expected content identity, destination and idempotency key are immutable;
/// immutable execution lineage and mutable provider handles advance through a database-guarded revision/state machine.
/// </summary>
public class ArtifactTransferIntent : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid StorageProfileRevisionId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public ArtifactDigestAlgorithm ExpectedDigestAlgorithm { get; set; } = ArtifactDigestAlgorithm.Sha256;
    public byte[] ExpectedDigest { get; set; } = [];
    public long ExpectedSizeBytes { get; set; }
    public string TargetLocator { get; set; } = string.Empty;
    public string TargetObjectKey { get; set; } = string.Empty;
    public string? TemporaryObjectKey { get; set; }
    public string? ProviderUploadId { get; set; }
    public ArtifactTransferState State { get; set; } = ArtifactTransferState.Intended;
    public long Revision { get; set; } = 1;

    /// <summary>Immutable generic attempt identity; null as a complete bundle for system-owned transfers.</summary>
    public Guid? ExecutionAttemptId { get; set; }
    public int? ExecutionAttemptOrdinal { get; set; }
    public int? ExecutionGeneration { get; set; }

    /// <summary>Independent monotonic worker-claim fence, advanced by a state-neutral claim and pinned across each transition.</summary>
    public long? WorkerFenceEpoch { get; set; }

    /// <summary>
    /// Expiring ownership lease for the current fence. Active work may renew it without changing state or fence;
    /// retry/terminal transitions release it so another process can claim only when the durable schedule permits.
    /// </summary>
    public DateTimeOffset? WorkerLeaseExpiresAt { get; set; }

    public int RetryCount { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public Guid? ArtifactObjectId { get; set; }
    public Guid? ArtifactLocationId { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
    public uint Xmin { get; set; }

    public StorageProfileRevision StorageProfileRevision { get; set; } = default!;
    public ArtifactObject? ArtifactObject { get; set; }
    public ArtifactLocation? ArtifactLocation { get; set; }
}

/// <summary>Monotonic transfer saga states. RetryScheduled may re-enter Uploading or Verifying, never Intended.</summary>
public enum ArtifactTransferState
{
    Intended,
    Uploading,
    Uploaded,
    Verifying,
    RetryScheduled,
    Committed,
    Failed,
    Cancelled,
}
