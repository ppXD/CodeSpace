using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Every 15 minutes, dispatches <see cref="TierStaleModelCapabilitiesCommand"/> to backfill the cached capability tier
/// for any not-yet-tiered pool model (a freshly added / reflected model is tiered within a tick; already-tiered rows are
/// skipped, so steady-state this is a no-op). Thin Mediator dispatcher (Rule 14) — the tiering logic lives in
/// <c>IModelCapabilityTieringService</c>.
/// </summary>
public sealed class ModelCapabilityTieringRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public ModelCapabilityTieringRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(ModelCapabilityTieringRecurringJob);
    public string CronExpression => "*/15 * * * *";   // every 15 minutes

    public async Task Execute() => await _mediator.Send(new TierStaleModelCapabilitiesCommand()).ConfigureAwait(false);
}
