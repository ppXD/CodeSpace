using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>
/// Dispatch the stuck-run reconciler sweep. Fired by the recurring job, but can also be
/// sent ad-hoc from an admin endpoint / tests. Returns the per-state counts of rows that
/// the sweep recovered.
///
/// <para>NOT tenant-scoped (no <c>IRequireTeamMembership</c>) — this is a system-wide
/// operation that runs without an actor context. Tenancy is enforced at the engine level
/// (run rows carry team_id); the reconciler doesn't need to filter by tenant.</para>
/// </summary>
public sealed record ReconcileStuckRunsCommand : ICommand<ReconcileStuckRunsResponse>;

/// <summary>Per-state counts returned to the caller for log surfacing.</summary>
public sealed record ReconcileStuckRunsResponse
{
    public required int RedispatchedFromPending { get; init; }
    public required int RevertedFromEnqueued { get; init; }
    public required int MarkedAbandonedFromRunning { get; init; }
    public required int RedispatchedFromStrandedSuspended { get; init; }

    /// <summary>Supervisor self-advances re-fired because the post-commit ResumeWaitAsync enqueue was lost (PR-E E2). 0 when the supervisor lane is off.</summary>
    public required int RecoveredSupervisorAdvance { get; init; }

    /// <summary>Abandoned-Running supervisor runs with a recoverable in-flight decision that were re-dispatched instead of failed (PR-E P1-2). 0 when the supervisor lane is off.</summary>
    public required int RecoveredAbandonedSupervisorRun { get; init; }
}
