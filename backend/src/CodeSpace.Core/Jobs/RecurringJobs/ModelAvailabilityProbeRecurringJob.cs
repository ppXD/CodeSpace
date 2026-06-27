using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Every 15 minutes, dispatches <see cref="ProbeStaleModelAvailabilityCommand"/> to re-probe the cached reachability of
/// each team's enabled Custom-gateway pool model whose availability is stale (older than the service's 30-minute
/// back-off window) or never probed. Thin Mediator dispatcher (Rule 14) — the probe logic lives in
/// <c>IModelAvailabilityProbeService</c>. The back-off keeps a 15-minute cron a near-no-op between windows.
/// </summary>
public sealed class ModelAvailabilityProbeRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public ModelAvailabilityProbeRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(ModelAvailabilityProbeRecurringJob);
    public string CronExpression => "*/15 * * * *";   // every 15 minutes (the 30-min back-off gates the actual re-probe)

    public async Task Execute() => await _mediator.Send(new ProbeStaleModelAvailabilityCommand()).ConfigureAwait(false);
}
