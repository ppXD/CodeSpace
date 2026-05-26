using CodeSpace.Messages.Commands.Webhooks;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Every 2 minutes, dispatches <see cref="ReconcileStuckWebhookRegistrationsCommand"/>. The
/// actual recovery logic lives in
/// <see cref="Services.Webhooks.Registration.IStuckWebhookRegistrationReconcilerService"/>;
/// this class only exists so Hangfire has a stable type-handle to register.
///
/// <para>2-minute cadence keeps recovery latency low for the stuck-Pending case (process
/// crashed between BindAsync.SaveChanges and dispatcher.DispatchAsync) — the row gets
/// re-dispatched within 2 minutes. The stuck-Registering / stuck-Enqueued sweeps have
/// longer thresholds inside the service (5 min / 10 min) so a slow-but-fine remote call
/// doesn't get pre-empted.</para>
/// </summary>
public sealed class StuckWebhookRegistrationReconcilerRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public StuckWebhookRegistrationReconcilerRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(StuckWebhookRegistrationReconcilerRecurringJob);
    public string CronExpression => "*/2 * * * *";   // every 2 minutes

    public async Task Execute() => await _mediator.Send(new ReconcileStuckWebhookRegistrationsCommand()).ConfigureAwait(false);
}
