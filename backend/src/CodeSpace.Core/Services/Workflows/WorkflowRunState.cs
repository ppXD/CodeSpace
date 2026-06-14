using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows;

/// <summary>
/// The WorkflowRun lifecycle as a pure predicate set: which statuses are terminal, and which a
/// deliberate operator cancel may flip to <see cref="WorkflowRunStatus.Cancelled"/>. The single
/// source of truth so the cancel path's status-guarded CAS and its tests agree on the boundary.
/// Pure + side-effect-free → unit-tested exhaustively (mirrors <c>AgentRunStateMachine</c>).
/// </summary>
public static class WorkflowRunState
{
    /// <summary>True when <paramref name="status"/> is a terminal state — the run is finished and a cancel is a no-op.</summary>
    public static bool IsTerminal(WorkflowRunStatus status) =>
        status is WorkflowRunStatus.Success or WorkflowRunStatus.Failure or WorkflowRunStatus.Cancelled;

    /// <summary>True when an operator cancel may flip <paramref name="status"/> to Cancelled — every non-terminal state (Pending/Enqueued/Running/Suspended).</summary>
    public static bool IsCancellable(WorkflowRunStatus status) => !IsTerminal(status);
}
