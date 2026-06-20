namespace CodeSpace.Messages.Enums;

/// <summary>
/// Lifecycle status of an agent run. <see cref="Queued"/> / <see cref="Running"/> are the in-flight
/// states; the rest are terminal. The durable AgentRun entity uses the full set; <c>AgentRunResult.Status</c>
/// only ever carries a TERMINAL value. Lives in Messages (like <see cref="WorkflowRunStatus"/>) so both
/// the persistence entity and the service/DTO layers reference it without a backwards layer dependency.
/// Stored as its string name (see AgentRunConfiguration).
/// </summary>
public enum AgentRunStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    TimedOut,

    /// <summary>
    /// Terminal: the run reached an end but can't be called a clean <see cref="Succeeded"/> — it left work a
    /// human must resolve before anything downstream proceeds (a decision it raised is still unanswered, or its
    /// final output reads as an unresolved question). The completion contract (<c>AgentCompletionContract</c>)
    /// re-grades a would-be <see cref="Succeeded"/> to this; <c>AgentRunResult.CompletionDisposition</c> names
    /// which case it is. A consumer that only proceeds on <see cref="Succeeded"/> (the agent.code node) treats it
    /// as a clean non-success — the work is NOT consumed as if it had finished.
    /// </summary>
    NeedsReview,
}
