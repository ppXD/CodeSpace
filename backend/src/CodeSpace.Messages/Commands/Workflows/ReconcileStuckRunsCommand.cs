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
}
