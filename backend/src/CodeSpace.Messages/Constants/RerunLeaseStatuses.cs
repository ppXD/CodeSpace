namespace CodeSpace.Messages.Constants;

/// <summary>The lifecycle states of a <c>workflow_rerun_lease</c> row. Only <see cref="InProgress"/> rows
/// participate in the unique-partial concurrency guard; a released lease no longer blocks a re-rerun.</summary>
public static class RerunLeaseStatuses
{
    /// <summary>The fork holding this lease is in flight — the branch is claimed.</summary>
    public const string InProgress = "in_progress";

    /// <summary>The fork reached a terminal state — the lease is freed (inline on completion, or by the reconciler).</summary>
    public const string Released = "released";
}
