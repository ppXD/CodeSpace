using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Webhooks;

/// <summary>
/// Dispatch the stuck-webhook-registration reconciler sweep. Fired by the recurring job, but
/// can also be sent ad-hoc from an admin endpoint / tests. Returns the per-state counts of
/// rows that the sweep recovered.
///
/// <para>NOT tenant-scoped — system-wide operation that runs without an actor context.
/// Tenancy is enforced at the bind layer (RepositoryWebhook rows are anchored to a Repository
/// that carries team_id); the reconciler doesn't need to filter by tenant.</para>
/// </summary>
public sealed record ReconcileStuckWebhookRegistrationsCommand : ICommand<ReconcileStuckWebhookRegistrationsResponse>;

/// <summary>Per-state counts returned to the caller for log surfacing.</summary>
public sealed record ReconcileStuckWebhookRegistrationsResponse
{
    public required int RedispatchedFromPending { get; init; }
    public required int RevertedFromEnqueued { get; init; }
    public required int RevertedFromRegistering { get; init; }
    public required int RevivedFromFailed { get; init; }
}
