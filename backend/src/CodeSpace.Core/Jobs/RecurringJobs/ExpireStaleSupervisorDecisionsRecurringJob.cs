using CodeSpace.Messages.Commands.Agents;
using Hangfire;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Every minute, dispatches <see cref="ExpireStaleSupervisorDecisionsCommand"/> to durably expire any stale UNDECIDED
/// supervisor decision (still Pending past its retention window), so an abandoned row gets a clean terminal instead of
/// lingering Pending forever. Thin Mediator dispatcher (Rule 14) — the cutoff + sweep logic lives in the handler +
/// <see cref="ISupervisorDecisionLog"/>. The supervisor lane is always on, so the sweep is always registered; the
/// per-row CAS is single-winner, so running on multiple Hangfire pods never double-expires a row.
/// </summary>
public sealed class ExpireStaleSupervisorDecisionsRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public ExpireStaleSupervisorDecisionsRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(ExpireStaleSupervisorDecisionsRecurringJob);
    public string CronExpression => Cron.Minutely();

    public async Task Execute() => await _mediator.Send(new ExpireStaleSupervisorDecisionsCommand()).ConfigureAwait(false);
}
