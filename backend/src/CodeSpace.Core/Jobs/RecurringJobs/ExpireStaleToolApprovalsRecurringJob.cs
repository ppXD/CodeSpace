using CodeSpace.Messages.Commands.Agents;
using Hangfire;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Every minute, dispatches <see cref="ExpireStaleToolApprovalsCommand"/> to durably expire any undecided tool-call
/// approval (item D3) whose deadline has passed, so a re-call gets a clean terminal instead of an approval that lingers
/// forever. Thin Mediator dispatcher (Rule 14) — the expire + signal + card-mirror logic lives in
/// <see cref="Services.Agents.Mcp.IToolApprovalExpiryService"/>.
///
/// <para>Minutely is responsive (approval deadlines are on the order of minutes) and cheap: the candidate query is a
/// narrow status + deadline scan, and the sweep finds no rows at all when governance is off — a no-op. The per-row CAS
/// is single-winner, so running on multiple Hangfire pods never double-expires a row.</para>
/// </summary>
public sealed class ExpireStaleToolApprovalsRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public ExpireStaleToolApprovalsRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(ExpireStaleToolApprovalsRecurringJob);
    public string CronExpression => Cron.Minutely();

    public async Task Execute() => await _mediator.Send(new ExpireStaleToolApprovalsCommand()).ConfigureAwait(false);
}
