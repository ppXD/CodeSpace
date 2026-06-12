using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Every minute, dispatches <see cref="FireDueScheduleTriggersCommand"/> to fire any
/// <c>trigger.schedule</c> activation whose cron is due. Thin Mediator dispatcher (Rule 14) — the
/// due-detection + fire logic lives in <c>IScheduleTriggerService</c>.
///
/// <para>Minutely is the finest schedule granularity the platform offers (standard 5-field cron);
/// the service's look-back window absorbs tick jitter, and per-occurrence idempotency keeps a
/// delayed or overlapping tick from double-firing.</para>
/// </summary>
public sealed class ScheduleTriggerRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public ScheduleTriggerRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(ScheduleTriggerRecurringJob);
    public string CronExpression => "* * * * *";   // every minute

    public async Task Execute() => await _mediator.Send(new FireDueScheduleTriggersCommand()).ConfigureAwait(false);
}
