using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Hourly, dispatches <see cref="ProbeUnknownModelCapabilitiesCommand"/> to objectively probe each team's opaque
/// (brain-tiered 'Unknown') pool model whose probe is stale (older than the service's days-long window) or never run.
/// Thin Mediator dispatcher (Rule 14) — the probe logic lives in <c>IModelCapabilityProbeService</c>. Capability is
/// stable, so the cron is slow and the days-long back-off keeps it a steady-state no-op (much rarer than the brain-tier
/// backfill's 15-min cron and the availability probe's 15-min cron).
/// </summary>
public sealed class ModelCapabilityProbeRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public ModelCapabilityProbeRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(ModelCapabilityProbeRecurringJob);
    public string CronExpression => "0 * * * *";   // hourly (the days-long back-off gates the actual re-probe)

    public async Task Execute() => await _mediator.Send(new ProbeUnknownModelCapabilitiesCommand()).ConfigureAwait(false);
}
