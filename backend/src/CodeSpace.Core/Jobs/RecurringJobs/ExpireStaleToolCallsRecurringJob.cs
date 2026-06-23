using CodeSpace.Messages.Commands.Agents;
using Hangfire;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Every minute, dispatches <see cref="ExpireStaleToolCallsCommand"/> to durably terminalize any side-effecting
/// tool-call row stranded non-terminal by a host crash (D6) — the one window the in-process interrupt recovery can't
/// cover (a SIGKILL between the Pending INSERT and the recovery write). Thin Mediator dispatcher (Rule 14) — the
/// candidate-set + single-winner CAS lives in <see cref="Services.Agents.Mcp.IToolCallLedgerService"/>.
///
/// <para>Minutely is cheap: the candidate query is a narrow status + stale-window scan gated on the owning run being
/// terminal, and the sweep finds no rows when nothing crashed — a no-op. The per-row CAS is single-winner, so running
/// on multiple Hangfire pods never double-fails a row.</para>
/// </summary>
public sealed class ExpireStaleToolCallsRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public ExpireStaleToolCallsRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(ExpireStaleToolCallsRecurringJob);
    public string CronExpression => Cron.Minutely();

    public async Task Execute() => await _mediator.Send(new ExpireStaleToolCallsCommand()).ConfigureAwait(false);
}
