using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.Agents.AgentRunLogging;

/// <summary>
/// Harness-neutral durable byte streams owned by an AgentRun. The process-spool producer uses this seam in Shadow
/// mode; every producer must carry the exact worker fence and capture session, and readers consume bounded ranges.
/// </summary>
public interface IAgentRunLogService : IScopedDependency
{
    Task<AgentRunLogOpenResult> OpenAsync(AgentRunLogOpenRequest request, CancellationToken cancellationToken);
    Task<AgentRunLogAppendResult> AppendAsync(AgentRunLogAppendRequest request, CancellationToken cancellationToken);
    Task<AgentRunLogFinalizeSourceResult> FinalizeSourceAsync(AgentRunLogFinalizeSourceRequest request, CancellationToken cancellationToken);
    Task<AgentRunLogCompleteResult> CompleteAsync(AgentRunLogCompleteRequest request, CancellationToken cancellationToken);
    Task<AgentRunLogFailCaptureResult> FailCaptureAsync(AgentRunLogFailCaptureRequest request, CancellationToken cancellationToken);
    Task<AgentRunLogMetadataResult> GetMetadataAsync(Guid teamId, Guid streamId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AgentRunLogMetadata>> ListMetadataAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AgentRunLogCaptureHead>> ListCaptureHeadsAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken);
    Task<AgentRunLogRangeResult> ReadRangeAsync(AgentRunLogRangeRequest request, CancellationToken cancellationToken);
}

public static class AgentRunLogKinds
{
    public const string StandardOutput = "stdout/v1";
    public const string StandardError = "stderr/v1";
    public const string Transcript = "transcript/v1";
    public const string Debug = "debug/v1";
}

/// <summary>Canonical representation for harness process text after the capture bridge's UTF-8 byte redaction.</summary>
public static class AgentRunLogRepresentations
{
    public const string PlainTextContentType = "text/plain";
    public const string Utf8ContentEncoding = "utf-8";
}

public sealed record AgentRunLogOpenRequest
{
    public required Guid TeamId { get; init; }
    public required Guid AgentRunId { get; init; }
    public required long WorkerFenceEpoch { get; init; }
    public required Guid CaptureSessionId { get; init; }
    public required string StreamKind { get; init; }
    public required string ContentType { get; init; }
    public string? ContentEncoding { get; init; }
    public required string CaptureSource { get; init; }
    public ArtifactRetention Retention { get; init; } = ArtifactRetention.Run;
    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed record AgentRunLogAppendRequest
{
    public required Guid TeamId { get; init; }
    public required Guid AgentRunId { get; init; }
    public required Guid StreamId { get; init; }
    public required long WorkerFenceEpoch { get; init; }
    public required Guid CaptureSessionId { get; init; }
    public required long ExpectedSegmentOrdinal { get; init; }
    public required long ExpectedOffsetBytes { get; init; }
    public required long ExpectedSourceOffsetBytes { get; init; }
    public required long SourceLengthBytes { get; init; }
    public required Guid StorageProfileId { get; init; }
    public required int StorageProfileRevision { get; init; }
    public required Guid ActorId { get; init; }
    public required ReadOnlyMemory<byte> Bytes { get; init; }
    public TimeSpan? OperationTimeout { get; init; }
}

public sealed record AgentRunLogFailCaptureRequest
{
    public required Guid TeamId { get; init; }
    public required Guid AgentRunId { get; init; }
    public required Guid StreamId { get; init; }
    public required long WorkerFenceEpoch { get; init; }
    public required Guid CaptureSessionId { get; init; }
    public required long ExpectedRevision { get; init; }
    public required string ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public AgentRunLogRecoveryClaimRef? RecoveryClaim { get; init; }

    /// <summary>Which error-bearing terminal capture state to record. <see cref="AgentRunLogStreamState.CaptureFailed"/> (the default) means capture itself broke; <see cref="AgentRunLogStreamState.Truncated"/> means capture succeeded but the source was cut short by its own size cap. Both carry a completion timestamp + an error code, which is exactly what the terminal check constraint demands of a non-Completed state.</summary>
    public AgentRunLogStreamState TerminalState { get; init; } = AgentRunLogStreamState.CaptureFailed;
}

public sealed record AgentRunLogFinalizeSourceRequest
{
    public required Guid TeamId { get; init; }
    public required Guid AgentRunId { get; init; }
    public required Guid StreamId { get; init; }
    public required long WorkerFenceEpoch { get; init; }
    public required Guid CaptureSessionId { get; init; }
    public required long ExpectedRevision { get; init; }
    public required long ExpectedSourceOffsetBytes { get; init; }
}

public sealed record AgentRunLogCompleteRequest
{
    public required Guid TeamId { get; init; }
    public required Guid AgentRunId { get; init; }
    public required Guid StreamId { get; init; }
    public required long WorkerFenceEpoch { get; init; }
    public required Guid CaptureSessionId { get; init; }
    public required long ExpectedRevision { get; init; }
    public TimeSpan? OperationTimeout { get; init; }
    public AgentRunLogRecoveryClaimRef? RecoveryClaim { get; init; }
}

/// <summary>Exact short-lived reconciler authority, validated inside the stream's final database transaction.</summary>
public sealed record AgentRunLogRecoveryClaimRef(Guid IntentId, Guid OwnerId, long FenceEpoch);

public sealed record AgentRunLogRangeRequest(Guid TeamId, Guid StreamId, long OffsetBytes, int Length)
{
    public TimeSpan? OperationTimeout { get; init; }
}

public sealed record AgentRunLogMetadata(
    Guid StreamId,
    Guid AgentRunId,
    string StreamKind,
    string ContentType,
    string? ContentEncoding,
    string CaptureSource,
    ArtifactRetention Retention,
    AgentRunLogStreamState State,
    long Revision,
    long SegmentCount,
    long TotalBytes,
    long SourceOffsetBytes,
    string? Sha256,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastModifiedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorCode);

public sealed record AgentRunLogSegmentReceipt(Guid SegmentId, long SegmentOrdinal, long StartOffsetBytes, long LengthBytes, long SourceStartOffsetBytes, long SourceLengthBytes, Guid ArtifactObjectId);

public sealed record AgentRunLogCaptureHead(AgentRunLogMetadata Metadata, long WorkerFenceEpoch, Guid CaptureSessionId, long CaptureSourceBaseOffsetBytes, DateTimeOffset? CaptureFinalizedAt);

public abstract record AgentRunLogOpenResult
{
    private AgentRunLogOpenResult() { }
    public sealed record Opened(AgentRunLogMetadata Metadata, bool WasAlreadyOpen, bool WasReclaimed) : AgentRunLogOpenResult
    {
        public required long CaptureSourceBaseOffsetBytes { get; init; }
        public DateTimeOffset? CaptureFinalizedAt { get; init; }
    }
    public sealed record Rejected(AgentRunLogProblem Problem) : AgentRunLogOpenResult;
}

public abstract record AgentRunLogAppendResult
{
    private AgentRunLogAppendResult() { }
    public sealed record Appended(AgentRunLogMetadata Metadata, AgentRunLogSegmentReceipt Segment, bool WasExactRetry) : AgentRunLogAppendResult;
    public sealed record Rejected(AgentRunLogProblem Problem) : AgentRunLogAppendResult;
}

public abstract record AgentRunLogCompleteResult
{
    private AgentRunLogCompleteResult() { }
    public sealed record Completed(AgentRunLogMetadata Metadata) : AgentRunLogCompleteResult;
    public sealed record Rejected(AgentRunLogProblem Problem) : AgentRunLogCompleteResult;
}

public abstract record AgentRunLogFinalizeSourceResult
{
    private AgentRunLogFinalizeSourceResult() { }
    public sealed record Finalized(AgentRunLogMetadata Metadata, bool WasAlreadyFinalized) : AgentRunLogFinalizeSourceResult;
    public sealed record Rejected(AgentRunLogProblem Problem) : AgentRunLogFinalizeSourceResult;
}

public abstract record AgentRunLogFailCaptureResult
{
    private AgentRunLogFailCaptureResult() { }
    public sealed record Failed(AgentRunLogMetadata Metadata, bool WasAlreadyFailed) : AgentRunLogFailCaptureResult;
    public sealed record Rejected(AgentRunLogProblem Problem) : AgentRunLogFailCaptureResult;
}

public abstract record AgentRunLogMetadataResult
{
    private AgentRunLogMetadataResult() { }
    public sealed record Found(AgentRunLogMetadata Metadata) : AgentRunLogMetadataResult;
    public sealed record Missing : AgentRunLogMetadataResult;
}

public abstract record AgentRunLogRangeResult
{
    private AgentRunLogRangeResult() { }
    public sealed record Available(AgentRunLogMetadata Metadata, long OffsetBytes, byte[] Bytes) : AgentRunLogRangeResult;
    public sealed record Unavailable(AgentRunLogProblem Problem, AgentRunLogMetadata? Metadata = null) : AgentRunLogRangeResult;
}

/// <summary>
/// One typed refusal from this seam. <see cref="IsRetryable"/> is the authority on whether repeating the SAME call can
/// still succeed — it is set from the storage layer's own verdict, so a code alone must never be read as "transient".
/// </summary>
public sealed record AgentRunLogProblem(AgentRunLogProblemCode Code, bool IsRetryable = false)
{
    /// <summary>
    /// Whether a caller may repeat the identical call. Every caller must ask THIS rather than re-deriving it, because
    /// a fault the storage layer marked permanent — an unresolvable credential, a profile the write is not admitted
    /// against — has to terminalize the stream with its real cause; retrying it only burns the caller's budget and
    /// leaves the stream Open, where terminal reconciliation attributes it to the agent's log source instead.
    ///
    /// <para><see cref="AgentRunLogProblemCode.ProviderTimeout"/> and
    /// <see cref="AgentRunLogProblemCode.ConcurrentMutation"/> are transient by CODE as well, which today changes no
    /// outcome: every production construction of them already passes the flag (each <c>ConcurrentMutation</c> arm in
    /// <c>AgentRunLogService</c> passes true, and its <c>ProviderTimeout</c> arm carries the CAS layer's own true, set
    /// on every timeout the runtime raises). They are kept because both name a deadline or a lost race rather than a
    /// verdict about the request, so a caller that omits the flag on one must not terminalize a stream that the very
    /// next attempt would have committed.</para>
    /// </summary>
    public bool IsTransient => IsRetryable || Code is AgentRunLogProblemCode.ProviderTimeout or AgentRunLogProblemCode.ConcurrentMutation;
}

public enum AgentRunLogProblemCode
{
    InvalidRequest,
    Missing,
    RunNotRunning,
    StaleWorker,
    StaleRecoveryClaim,
    CaptureClaimConflict,
    SourceNotFinalized,
    StreamTerminal,
    NonContiguous,
    IdempotencyConflict,
    ConcurrentMutation,
    ArtifactMissing,
    ArtifactCorrupt,
    AccessDenied,
    BackendUnavailable,
    /// <summary>The team's storage profile/credential could not be ACTIVATED for this write or read: the profile is missing, not admitted, has no such revision, carries invalid configuration, or its credential cannot be resolved. Distinct from <see cref="BackendUnavailable"/> so the durable cause names storage configuration rather than the capture backend.</summary>
    StorageActivationFailed,
    ProviderTimeout,
    Unsupported,
}
