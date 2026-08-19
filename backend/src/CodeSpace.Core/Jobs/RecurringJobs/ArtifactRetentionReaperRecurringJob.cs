using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Hourly, dispatches <see cref="ReapUnreferencedArtifactsCommand"/> to collect declared artifacts that nothing
/// references. Thin Mediator dispatcher (Rule 14) — the work lives in
/// <see cref="Services.Workflows.Artifacts.Retention.IArtifactRetentionReaper"/>.
///
/// <para>Hourly is ample and deliberately unhurried: the shortest retention rule keeps an object for days before it is
/// even a candidate and then quarantines it for another day, so the cadence changes nothing about WHAT is collected —
/// only how promptly. Offset to :15 so it does not pile onto the top-of-hour workspace janitor or the :30 spool
/// reaper.</para>
/// </summary>
public sealed class ArtifactRetentionReaperRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public ArtifactRetentionReaperRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(ArtifactRetentionReaperRecurringJob);
    public string CronExpression => "15 * * * *";   // quarter past every hour

    public async Task Execute() => await _mediator.Send(new ReapUnreferencedArtifactsCommand()).ConfigureAwait(false);
}
