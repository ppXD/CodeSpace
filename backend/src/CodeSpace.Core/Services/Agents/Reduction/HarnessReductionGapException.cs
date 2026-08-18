using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Agents.Reduction;

/// <summary>
/// A frame arrived PAST the reduction's frontier, so the records between the two were never folded. Raised rather than
/// tolerated: folding it anyway would produce a state that looks like a consumed prefix, gets stored as one, and is
/// then indistinguishable from a complete fold — the exact failure mode a checkpoint exists to make impossible.
/// A caller that legitimately cannot obtain the missing records must record the gap, not advance over it.
/// </summary>
public sealed class HarnessReductionGapException : Exception, IFailure
{
    /// <summary>Understood and blocked by a rule the caller cannot restate its way out of: the records are missing, and only recovering or recording the gap moves it forward.</summary>
    public FailureKind Kind => FailureKind.Unprocessable;

    public string Code => FailureCodes.HarnessReductionGap;

    public HarnessReductionGapException(Guid streamId, long expectedOrdinal, long observedOrdinal)
        : base($"stream '{streamId}' skipped from ordinal {expectedOrdinal} to {observedOrdinal}, so {observedOrdinal - expectedOrdinal} record(s) would never be folded")
    {
        StreamId = streamId;
        ExpectedOrdinal = expectedOrdinal;
        ObservedOrdinal = observedOrdinal;
    }

    /// <summary>The stream whose ordinals jumped.</summary>
    public Guid StreamId { get; }

    /// <summary>The ordinal the frontier was waiting for.</summary>
    public long ExpectedOrdinal { get; }

    /// <summary>The ordinal that arrived instead.</summary>
    public long ObservedOrdinal { get; }
}
