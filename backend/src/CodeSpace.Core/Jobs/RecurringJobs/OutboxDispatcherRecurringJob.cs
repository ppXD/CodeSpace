using CodeSpace.Messages.Commands.Outbox;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Every 10 seconds, drains the outbox by up to 50 messages. The actual handler dispatch
/// happens inside <see cref="Services.Outbox.IOutboxDispatcher.DrainOnceAsync"/> with
/// SKIP LOCKED claim/lease so multiple replicas safely share the work.
///
/// <para>10-second cadence is the right floor for webhook-registration (the only remaining
/// outbox message type): operator binds a repo → outbox row enqueued → up to 10s before
/// the webhook actually lands at the provider. Faster cadence wastes empty scans; slower
/// makes the bind UX feel laggy.</para>
/// </summary>
public sealed class OutboxDispatcherRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public OutboxDispatcherRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(OutboxDispatcherRecurringJob);
    // Hangfire's cron parser accepts the 5-field standard form; we use the 6-field form
    // with seconds for sub-minute granularity. "*/10 * * * * *" = every 10 seconds.
    public string CronExpression => "*/10 * * * * *";

    public async Task Execute() => await _mediator.Send(new DrainOutboxCommand()).ConfigureAwait(false);
}
