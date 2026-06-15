using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Commands.Agents;
using Hangfire;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Every minute (when the supervisor lane is enabled), dispatches <see cref="ExpireStaleSupervisorDecisionsCommand"/> to
/// durably expire any stale UNDECIDED supervisor decision (still Pending past its retention window), so an abandoned row
/// gets a clean terminal instead of lingering Pending forever. Thin Mediator dispatcher (Rule 14) — the cutoff + sweep
/// logic lives in the handler + <see cref="ISupervisorDecisionLog"/>.
///
/// <para><b>Flag-gated registration:</b> implements <see cref="IConditionalRecurringJob"/> so the scheduler scan SKIPS it
/// entirely unless <see cref="SupervisorLane.IsEnabled"/>. A flag-OFF deployment is byte-identical — no recurring entry
/// is created, so no sweep ever runs (the empty <c>SupervisorDecisionRecord</c> table is untouched). The per-row CAS is
/// single-winner, so running on multiple Hangfire pods never double-expires a row.</para>
/// </summary>
public sealed class ExpireStaleSupervisorDecisionsRecurringJob : IConditionalRecurringJob
{
    private readonly IMediator _mediator;

    public ExpireStaleSupervisorDecisionsRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(ExpireStaleSupervisorDecisionsRecurringJob);
    public string CronExpression => Cron.Minutely();

    public bool ShouldRegister => SupervisorLane.IsEnabled();

    public async Task Execute() => await _mediator.Send(new ExpireStaleSupervisorDecisionsCommand()).ConfigureAwait(false);
}
