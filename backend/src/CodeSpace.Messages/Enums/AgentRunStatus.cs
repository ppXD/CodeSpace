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
}
