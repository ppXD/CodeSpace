namespace CodeSpace.Messages.Tasks.Trace;

public static class RunRecordPagePayloadStates
{
    /// <summary>The immutable payload exists on the record but is intentionally absent from this metadata page.</summary>
    public const string Deferred = "Deferred";
}

/// <summary>
/// Body-free metadata for one raw Workflow Run ledger record. <see cref="RecordId"/> is the immutable identity used by
/// the exact bounded payload endpoint; the closed payload state prevents an omitted body from being mistaken for an
/// empty object. The legacy snapshot/SSE keep using <see cref="RunRecordView"/> with inline payloads.
/// </summary>
public sealed record RunRecordPageItem
{
    public required Guid RecordId { get; init; }
    public required long Sequence { get; init; }
    public required string RecordType { get; init; }
    public string? NodeId { get; init; }
    public required string IterationKey { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required string PayloadState { get; init; }
    public required string PayloadContentType { get; init; }
    public Guid? CorrelationId { get; init; }
    public Guid? ParentRecordId { get; init; }
}
