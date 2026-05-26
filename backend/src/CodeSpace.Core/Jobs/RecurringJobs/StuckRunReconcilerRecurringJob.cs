using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Every 2 minutes, dispatches <see cref="ReconcileStuckRunsCommand"/>. The actual recovery
/// logic lives in <see cref="Services.Workflows.Reconciliation.IStuckRunReconcilerService"/>;
/// this class only exists so Hangfire has a stable type-handle to register.
///
/// <para>2-minute cadence keeps recovery latency low: a process that crashes between
/// SaveChanges + DispatchAsync leaves a row in Pending; with this cadence the row gets
/// re-dispatched within 2 minutes. Tighter would cost more idle DB scans for no recovery
/// benefit (stuck rows are rare). Looser would let a Pending row sit longer than the
/// operator expects.</para>
/// </summary>
public sealed class StuckRunReconcilerRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public StuckRunReconcilerRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(StuckRunReconcilerRecurringJob);
    public string CronExpression => "*/2 * * * *";   // every 2 minutes

    public async Task Execute() => await _mediator.Send(new ReconcileStuckRunsCommand()).ConfigureAwait(false);
}
