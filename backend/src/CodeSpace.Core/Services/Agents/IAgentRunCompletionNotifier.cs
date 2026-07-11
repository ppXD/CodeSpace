namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Notified when an agent run reaches a terminal state, so a subscriber can react out-of-band from
/// whatever started the run. The executor fires this immediately after it lands the terminal result;
/// the agent layer stays decoupled from the trigger — a workflow's <c>agent.run</c> node today, a
/// future direct / standalone API run tomorrow.
///
/// The sole production subscriber resumes the workflow node parked on the run (the workflow-layer impl).
/// Implementations MUST be best-effort + idempotent: the run is already committed terminal, so a failure
/// here must not throw back into the executor (that would mask the completed result), and a duplicate
/// notification (a re-claimed Hangfire job, a manual re-run) must not double-resume.
/// </summary>
public interface IAgentRunCompletionNotifier
{
    Task NotifyCompletedAsync(Guid agentRunId, CancellationToken cancellationToken);
}
