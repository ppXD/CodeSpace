using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Twice hourly, dispatches <see cref="ReapAgentRunOrphansCommand"/> to settle the orphaned-resource receipts
/// addressed to this host. Thin Mediator dispatcher (Rule 14) — the logic lives in
/// <see cref="Services.Agents.Recovery.IAgentRunOrphanReaper"/>.
///
/// <para>More often than the hourly spool reaper because one of the resources it frees is scarce: the egress netns
/// teardown is what returns a host-global subnet lease to a BOUNDED pool, and a lost host's leases are held until
/// this sweep runs. Offset to :20 / :50 so it lands on no other sweep's minute (the top-of-hour workspace janitor,
/// the quarter-past artifact retention, the half-past spool reaper).</para>
/// </summary>
public sealed class AgentRunOrphanReaperRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public AgentRunOrphanReaperRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(AgentRunOrphanReaperRecurringJob);
    public string CronExpression => "20,50 * * * *";   // twice hourly, on a minute no other sweep uses

    public async Task Execute() => await _mediator.Send(new ReapAgentRunOrphansCommand()).ConfigureAwait(false);
}
