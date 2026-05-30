namespace CodeSpace.Messages.Constants;

/// <summary>
/// How a suspended run will be woken. Stored as <c>workflow_run_wait.wait_kind</c> (CHECK-
/// constrained at the DB layer) and carried as <c>SuspensionToken.Kind</c> from the node.
/// </summary>
public static class WorkflowWaitKinds
{
    /// <summary>Self-waking after a delay — the engine schedules a resume at <c>wake_at</c>.</summary>
    public const string Timer = "Timer";

    /// <summary>Waits for a human to approve/reject via the API + UI (Phase 1.2).</summary>
    public const string Approval = "Approval";

    /// <summary>Waits for an external system to POST to a tokened callback URL (Phase 1.2).</summary>
    public const string Callback = "Callback";
}

/// <summary>Lifecycle of a <c>workflow_run_wait</c> row. CHECK-constrained at the DB layer.</summary>
public static class WorkflowWaitStatuses
{
    public const string Pending = "Pending";
    public const string Resolved = "Resolved";
}
