using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// Additive profile-driven Artifact CAS v2 runtime. It does not replace <see cref="IArtifactStore"/> or confer any
/// workflow completion/delivery authority. Write requests are streaming and bind one exact profile revision.
/// </summary>
public interface IArtifactCasRuntimeCoordinator : IScopedDependency
{
    Task<ArtifactCasTransferResult> PutAsync(ArtifactCasTransferRequest request, CancellationToken cancellationToken);
    Task<ArtifactCasReadResult> OpenReadAsync(ArtifactCasReadRequest request, CancellationToken cancellationToken);
}

public sealed record ArtifactCasTransferRequest
{
    /// <summary>Mirrors <c>artifact_transfer_intent.idempotency_key</c>, which is VARCHAR(256).</summary>
    public const int MaximumKeyLength = 256;

    /// <summary>
    /// The longest <see cref="IdempotencyScope"/> the runtime accepts. The remainder of the column is reserved for the
    /// attempt-generation suffix the runtime appends, and 12 characters hold <c>/g</c> plus any <see cref="int"/> — so
    /// a scope at this cap can never mint a key the column cannot store.
    /// </summary>
    public const int MaximumScopeLength = MaximumKeyLength - 12;

    public required Guid TeamId { get; init; }
    public required Guid StorageProfileId { get; init; }
    public required int StorageProfileRevision { get; init; }

    /// <summary>
    /// The idempotency SCOPE for these bytes under this profile revision: the exact key the first attempt claims, and
    /// the prefix every later attempt generation extends. Callers that pass the same scope for the same content share
    /// one durable intent, which is what makes concurrent writers of identical payloads cooperate instead of race.
    ///
    /// <para>The runtime — not the caller — picks the generation, because <c>Failed</c> is a one-way door in the
    /// database and only a distinct key can mint a fresh intent after one is burned. A caller that hand-rolls a bare
    /// key therefore cannot recover from a repaired misconfiguration at all, which is why this is a scope rather than
    /// a literal key.</para>
    /// </summary>
    public required string IdempotencyScope { get; init; }

    public required string TargetObjectKey { get; init; }
    public required Stream Content { get; init; }
    public required long ExpectedSizeBytes { get; init; }
    public required string ExpectedSha256 { get; init; }
    public required Guid ActorId { get; init; }
    public string? ContentType { get; init; }
    public ArtifactCasExecutionIdentity? ExecutionIdentity { get; init; }
    public TimeSpan? OperationTimeout { get; init; }
}

/// <summary>
/// Reserved immutable lineage for a future attempt-authority adapter. The additive runtime rejects attempt-owned
/// requests until that adapter can prove the identity is still operationally active at every effect commit.
/// </summary>
public sealed record ArtifactCasExecutionIdentity(Guid AttemptId, int AttemptOrdinal, int Generation);

public sealed record ArtifactCasReadRequest
{
    public required Guid TeamId { get; init; }
    public required Guid ArtifactObjectId { get; init; }
    public required Guid StorageProfileId { get; init; }
    public required int StorageProfileRevision { get; init; }
    public TimeSpan? OperationTimeout { get; init; }
}

public abstract record ArtifactCasTransferResult
{
    private ArtifactCasTransferResult() { }

    public sealed record Committed(Guid IntentId, Guid ArtifactObjectId, Guid ArtifactLocationId, bool WasAlreadyCommitted) : ArtifactCasTransferResult;
    public sealed record Deferred(Guid IntentId, DateTimeOffset NextAttemptAt, ArtifactCasProblem Problem) : ArtifactCasTransferResult;
    public sealed record Rejected(Guid? IntentId, ArtifactCasProblem Problem) : ArtifactCasTransferResult;
}

public abstract record ArtifactCasReadResult
{
    private ArtifactCasReadResult() { }

    /// <summary>
    /// Owns a streaming provider read. The stream is pinned to the persisted provider version/ETag and verifies the
    /// complete SHA-256 at EOF; callers must dispose it and must treat an integrity exception as a failed read.
    /// </summary>
    public sealed record Opened(Stream Content, long SizeBytes, string Sha256) : ArtifactCasReadResult;
    public sealed record Unavailable(ArtifactCasProblem Problem) : ArtifactCasReadResult;
}

public sealed record ArtifactCasProblem(ArtifactCasProblemCode Code, bool IsRetryable);

public enum ArtifactCasProblemCode
{
    ProfileMissing,
    ProfileNotActive,
    ProfileRevisionMissing,
    ProfileInvalid,
    ProviderUnavailable,
    CredentialUnavailable,
    CredentialInvalid,
    CredentialBrokerUnavailable,
    ExecutionAdmissionUnavailable,
    IdempotencyConflict,
    ArtifactMissing,
    TargetMissing,
    TargetCorrupt,
    Unauthorized,
    Forbidden,
    Throttled,
    ProviderTimeout,
    ProviderUnavailableTransient,
    ProviderFailure,
    Unsupported,
    StaleWorker,
    TransferInProgress,
    MultipleLocationsUnsupported,
    LocationUnavailable,
}
