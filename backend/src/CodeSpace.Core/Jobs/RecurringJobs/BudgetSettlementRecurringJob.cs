using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Every 5 minutes, dispatches <see cref="SweepBudgetSettlementCommand"/> (W-hard 2b) — folded attempts settle at
/// the priced actual (freeing over-estimated headroom for later waves), terminal orphans release, overdue
/// reservations expire to Indeterminate. Thin Mediator dispatcher (Rule 14); admission stays correct without this
/// job — settlement is a headroom OPTIMIZATION plus the orphan-recovery pass, eventually-consistent by design.
/// </summary>
public sealed class BudgetSettlementRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public BudgetSettlementRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(BudgetSettlementRecurringJob);
    public string CronExpression => "*/5 * * * *";

    public async Task Execute() => await _mediator.Send(new SweepBudgetSettlementCommand()).ConfigureAwait(false);
}
