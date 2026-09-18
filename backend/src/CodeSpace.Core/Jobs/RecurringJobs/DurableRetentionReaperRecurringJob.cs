using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Hourly, dispatches <see cref="ReapExpiredDurableRecordsCommand"/> to reclaim durable records nothing cites any
/// more. Thin Mediator dispatcher (Rule 14) — the work lives in
/// <see cref="Services.Workflows.Retention.IDurableRetentionReaper"/>.
///
/// <para>Hourly is ample and deliberately unhurried: the shortest rule keeps a record for days before it is even a
/// candidate and then quarantines it for another day, so the cadence changes nothing about WHAT is reclaimed — only
/// how promptly. Offset to :45 so it does not pile onto the :15 artifact reaper or the :30 spool reaper.</para>
/// </summary>
public sealed class DurableRetentionReaperRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public DurableRetentionReaperRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(DurableRetentionReaperRecurringJob);
    public string CronExpression => "45 * * * *";   // quarter to every hour

    public async Task Execute() => await _mediator.Send(new ReapExpiredDurableRecordsCommand()).ConfigureAwait(false);
}
