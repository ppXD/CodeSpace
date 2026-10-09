using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Webhooks;

/// <summary>
/// Delete every <c>webhook_claim</c> row whose window has ended — replayed-body claims after their retention, pull-request
/// debounce claims after their minute. A claim on the same key takes an expired row over in place, so nothing depends on
/// this for correctness; it only keeps the table near one window of deliveries. Dispatched by the recurring sweep.
///
/// <para>NOT transactional (<see cref="INonTransactionalCommand"/>): each batch commits on its own, so a backlog never
/// holds a long lock that a delivery's claim could queue behind, and one batch failing keeps what the others cleared.
/// NOT tenant-scoped — a system-wide sweep with no actor context.</para>
/// </summary>
public sealed record PurgeExpiredWebhookClaimsCommand : ICommand<PurgeExpiredWebhookClaimsResponse>, INonTransactionalCommand;

/// <summary>How many expired claims this sweep deleted — surfaced in the job log so an operator can see it working.</summary>
public sealed record PurgeExpiredWebhookClaimsResponse
{
    public required int Deleted { get; init; }
}
