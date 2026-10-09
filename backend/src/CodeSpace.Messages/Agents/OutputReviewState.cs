namespace CodeSpace.Messages.Agents;

/// <summary>
/// Where a CONFIGURED output review left an agent run's result. Absent (null) on a result no review was configured
/// for, or one that produced nothing to review. Every door to the reviewable head, to a PR-open and to a change set
/// takes a reviewed unit's work only at <see cref="Approved"/>: a review that never reached a verdict over the whole
/// result is not an approval, whatever kept it from running.
/// </summary>
public enum OutputReviewState
{
    /// <summary>The review ran over the whole result and approved it.</summary>
    Approved,

    /// <summary>The review ran and objected (<c>AgentRunResult.ReviewFeedback</c> says why).</summary>
    Flagged,

    /// <summary>The review never examined the whole result (<c>AgentRunResult.UnreviewedReason</c> says why): it could not run, it read only part of the result, or the run ended in a way that skipped it.</summary>
    Unreviewed,
}
