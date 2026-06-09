using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Every 2 minutes, dispatches <see cref="ReconcileStuckAgentRunsCommand"/>. The recovery logic lives
/// in <see cref="Services.Agents.IAgentRunReconcilerService"/>; this class only exists so Hangfire has a
/// stable type-handle to register (Rule 14 — a thin Mediator dispatcher).
///
/// <para>2-minute cadence keeps recovery latency low without idle DB churn: an agent run orphaned by a
/// killed pod is flipped to Failed within ~2 minutes of its liveness window lapsing, so it never shows
/// as forever-Running.</para>
/// </summary>
public sealed class StuckAgentRunReconcilerRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public StuckAgentRunReconcilerRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(StuckAgentRunReconcilerRecurringJob);
    public string CronExpression => "*/2 * * * *";   // every 2 minutes, matching the workflow reconciler

    public async Task Execute() => await _mediator.Send(new ReconcileStuckAgentRunsCommand()).ConfigureAwait(false);
}
