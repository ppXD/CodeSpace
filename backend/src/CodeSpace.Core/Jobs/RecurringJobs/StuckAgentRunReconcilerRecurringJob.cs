using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Every minute, dispatches <see cref="ReconcileStuckAgentRunsCommand"/>. The recovery logic lives
/// in <see cref="Services.Agents.IAgentRunReconcilerService"/>; this class only exists so Hangfire has a
/// stable type-handle to register (Rule 14 — a thin Mediator dispatcher).
///
/// <para>1-minute cadence keeps recovery latency low without idle DB churn: an agent run orphaned by a
/// killed pod is flipped to Failed within ~1 minute of its lease lapsing, so it never shows as
/// forever-Running. Tightened from 2 min now that lease-based detection (not heartbeat-silence inference)
/// gates the reclaim, leaving the cron as the only remaining latency term.</para>
/// </summary>
public sealed class StuckAgentRunReconcilerRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public StuckAgentRunReconcilerRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(StuckAgentRunReconcilerRecurringJob);
    public string CronExpression => "* * * * *";   // every minute

    public async Task Execute() => await _mediator.Send(new ReconcileStuckAgentRunsCommand()).ConfigureAwait(false);
}
