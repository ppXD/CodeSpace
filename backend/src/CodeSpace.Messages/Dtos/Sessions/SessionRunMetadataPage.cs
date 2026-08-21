namespace CodeSpace.Messages.Dtos.Sessions;

/// <summary>A hard-bounded chronological metadata page over one Work Session's run membership.</summary>
public sealed record SessionRunMetadataPage
{
    /// <summary>The exact selector supplied by the caller.</summary>
    public required SessionRunMetadataSelector Selector { get; init; }

    /// <summary>The exact team-scoped session admitted by the selector.</summary>
    public required Guid SessionId { get; init; }

    public required SessionRunMetadataPageDirection Direction { get; init; }
    public string? RequestCursor { get; init; }

    /// <summary>
    /// Highest immutable RunNumber admitted to this page family. It freezes membership only: mutable status, error,
    /// and timing fields are read at each request's own database snapshot and can legitimately change between pages.
    /// </summary>
    public required long MembershipHeadRunNumber { get; init; }

    /// <summary>The requested anchor's lineage root; null for a direct session selector.</summary>
    public Guid? AnchorRootRunId { get; init; }

    public required SessionRunMetadataConsistency Consistency { get; init; }
    public required IReadOnlyList<SessionRunMetadataItem> Items { get; init; }
    public required SessionRunMetadataOmission Omitted { get; init; }
    public required SessionRunMetadataContinuation Continuation { get; init; }
}

/// <summary>Exactly one selector arm is populated. The response echoes this record byte-for-byte.</summary>
public sealed record SessionRunMetadataSelector
{
    public required SessionRunMetadataSelectorKind Kind { get; init; }
    public Guid? SessionId { get; init; }
    public Guid? RunAnchorId { get; init; }
}

public enum SessionRunMetadataSelectorKind
{
    Session,
    RunAnchor,
}

public enum SessionRunMetadataPageDirection
{
    Tail,
    Older,
}

public enum SessionRunMetadataConsistency
{
    /// <summary>Membership is frozen at HeadRunNumber; row state is a fresh observation on every request.</summary>
    MembershipHeadOnly,
}

/// <summary>Which chronological sides are intentionally absent from this bounded window.</summary>
public sealed record SessionRunMetadataOmission
{
    public required bool Older { get; init; }
    public required bool Newer { get; init; }
}

/// <summary>Opaque continuation controls. OlderCursor is null at the oldest edge; ReturnToTail exits historical mode.</summary>
public sealed record SessionRunMetadataContinuation
{
    public string? OlderCursor { get; init; }
    public required bool ReturnToTail { get; init; }
}

/// <summary>One narrow run/request metadata row. No output, request payload, artifact, manifest, goal, or result body.</summary>
public sealed record SessionRunMetadataItem
{
    public required Guid RunId { get; init; }
    public required long RunNumber { get; init; }
    public required Guid RunRequestId { get; init; }
    public Guid? RootRunId { get; init; }
    public int? SessionTurnIndex { get; init; }
    public required CodeSpace.Messages.Enums.WorkflowRunStatus Status { get; init; }
    public required SessionRunMetadataText ProjectionKind { get; init; }
    public required SessionRunMetadataText SourceType { get; init; }
    public required SessionRunMetadataText RerunFromNodeId { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required SessionRunMetadataText Error { get; init; }
    public required CodeSpace.Messages.Enums.WorkflowRunRequestStatus RequestStatus { get; init; }
    public required DateTimeOffset RequestReceivedAt { get; init; }
}

/// <summary>A UTF-8 byte-bounded observation of one persisted text field; SizeBytes always describes the original.</summary>
public sealed record SessionRunMetadataText
{
    public string? Text { get; init; }
    public required long SizeBytes { get; init; }
    public required SessionRunMetadataTextState State { get; init; }
}

public enum SessionRunMetadataTextState
{
    None,
    Complete,
    Truncated,
    Corrupt,
}
