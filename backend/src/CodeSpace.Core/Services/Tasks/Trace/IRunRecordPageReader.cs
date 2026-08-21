using CodeSpace.Messages.Tasks.Trace;

namespace CodeSpace.Core.Services.Tasks.Trace;

public sealed record RunRecordPageRequest(Guid RunId, Guid TeamId, long? BeforeSequence, long? AfterSequence, int Limit)
{
    public void Validate()
    {
        if (BeforeSequence.HasValue && AfterSequence.HasValue) throw new ArgumentException("BeforeSequence and AfterSequence are mutually exclusive.");
        if (BeforeSequence is <= 0) throw new ArgumentOutOfRangeException(nameof(BeforeSequence));
        if (AfterSequence is < 0) throw new ArgumentOutOfRangeException(nameof(AfterSequence));
        if (Limit is < 1 or > RunRecordPageLimits.MaxLimit) throw new ArgumentOutOfRangeException(nameof(Limit));
    }
}

/// <summary>
/// Additive bounded reader for the raw Workflow Run ledger. It does not replace the legacy all-record snapshot or the
/// SSE tail; consumers can migrate independently without changing run execution or terminal behavior.
/// </summary>
public interface IRunRecordPageReader
{
    Task<RunRecordPageResponse?> ReadAsync(RunRecordPageRequest request, CancellationToken cancellationToken);
}
