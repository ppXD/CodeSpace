using CodeSpace.Core.Services.Jobs;
using CodeSpace.Messages.Commands.Outbox;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Every 30 seconds, resets Claimed-but-expired outbox rows back to Pending.
///
/// <para>Worst-case recovery latency for an abandoned outbox message is
/// <c>OutboxDispatcher.DefaultLeaseDuration</c> + 30s = ~90s.</para>
/// </summary>
public sealed class OutboxLeaseReaperRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public OutboxLeaseReaperRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(OutboxLeaseReaperRecurringJob);
    public string CronExpression => "*/30 * * * * *";   // every 30 seconds

    public async Task Execute() => await _mediator.Send(new ReapOutboxLeasesCommand()).ConfigureAwait(false);
}
