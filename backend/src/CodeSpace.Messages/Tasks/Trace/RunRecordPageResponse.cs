namespace CodeSpace.Messages.Tasks.Trace;

public static class RunRecordPageLimits
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 500;

    public static bool IsValid(long? beforeSequence, long? afterSequence, int limit) =>
        !(beforeSequence.HasValue && afterSequence.HasValue) &&
        (!beforeSequence.HasValue || beforeSequence.Value > 0) &&
        (!afterSequence.HasValue || afterSequence.Value >= 0) &&
        limit is >= 1 and <= MaxLimit;
}

public static class RunRecordPageModes
{
    public const string Tail = "Tail";
    public const string Older = "Older";
    public const string Newer = "Newer";
}

/// <summary>
/// One bounded keyset page of the raw Workflow Run ledger. Records are always returned in ascending Sequence order.
/// Tail/Older pages expose only <see cref="NextBeforeSequence"/> when another older page exists; Newer pages expose
/// only <see cref="NextAfterSequence"/> when another forward page exists. A null continuation means this page caught
/// up in its requested direction; callers keep their observed head when polling later appends.
/// </summary>
public sealed record RunRecordPageResponse
{
    public required Guid RunId { get; init; }
    public required string RunStatus { get; init; }
    public required string Mode { get; init; }
    public required IReadOnlyList<RunRecordView> Records { get; init; }
    public long? NextBeforeSequence { get; init; }
    public long? NextAfterSequence { get; init; }
}
