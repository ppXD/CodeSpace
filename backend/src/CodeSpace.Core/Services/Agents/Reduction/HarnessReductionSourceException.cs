using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Agents.Reduction;

/// <summary>
/// The record source yielded a frame that cannot be folded — a record whose payload binding, digest or redaction claim
/// does not hold together, or a projection attributed to a frame it did not complete. Raised at the seam rather than
/// inside the fold so an unreadable frame can never reach the prefix digest: once a bad record is chained in, every
/// later checkpoint carries a witness to a prefix that never existed.
/// </summary>
public sealed class HarnessReductionSourceException : Exception, IFailure
{
    public HarnessReductionSourceException(string message) : base(message)
    {
    }

    /// <summary>Understood and blocked by a rule: the record was recorded and is retained, it just cannot be projected faithfully. Not <see cref="FailureKind.Internal"/> — a harness is free to emit a frame we cannot read, so this is not necessarily an invariant of ours that broke.</summary>
    public FailureKind Kind => FailureKind.Unprocessable;

    public string Code => FailureCodes.HarnessRecordUnreadable;
}
