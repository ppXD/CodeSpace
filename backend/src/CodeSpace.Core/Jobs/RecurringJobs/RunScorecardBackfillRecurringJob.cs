using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>A4 — hourly catch-up: terminal runs that terminalized before the durable scorecard existed gain their row (thin Rule-14 dispatcher). Observation-only; a missed tick delays a trend point, never a run.</summary>
public sealed class RunScorecardBackfillRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public RunScorecardBackfillRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(RunScorecardBackfillRecurringJob);
    public string CronExpression => "17 * * * *";

    public async Task Execute() => await _mediator.Send(new BackfillRunScorecardsCommand()).ConfigureAwait(false);
}
