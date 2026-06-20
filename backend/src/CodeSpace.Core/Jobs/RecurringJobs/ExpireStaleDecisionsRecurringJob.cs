using CodeSpace.Messages.Commands.Decisions;
using Hangfire;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Every minute, dispatches <see cref="ExpireStaleDecisionsCommand"/> to apply the configured default to any undecided
/// agent-grain decision (Decision substrate D5b — AC4 never-hang) whose deadline has passed, so a stranded agent
/// (disconnect / pod restart / lost waiter) gets its default answer instead of hanging forever. Thin Mediator dispatcher
/// (Rule 14) — the default-answer + signal + card-mirror logic lives in <see cref="Services.Decisions.IDecisionExpiryService"/>.
///
/// <para>A plain <see cref="IRecurringJob"/> (the decision substrate has no feature flag — it runs unconditionally), so
/// no <c>ShouldRegister</c> gate. Minutely is responsive (decision deadlines are minute-scale) and cheap: the candidate
/// query is a narrow status + kind + deadline scan that finds no rows when nothing is parked. The per-row answer CAS is
/// single-winner, so running on multiple Hangfire pods never double-answers a row.</para>
/// </summary>
public sealed class ExpireStaleDecisionsRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public ExpireStaleDecisionsRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(ExpireStaleDecisionsRecurringJob);
    public string CronExpression => Cron.Minutely();

    public async Task Execute() => await _mediator.Send(new ExpireStaleDecisionsCommand()).ConfigureAwait(false);
}
