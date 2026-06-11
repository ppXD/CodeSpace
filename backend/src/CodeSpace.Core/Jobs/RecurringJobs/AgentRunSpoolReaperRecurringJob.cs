using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Hourly, dispatches <see cref="ReapAgentRunSpoolsCommand"/> to reclaim the on-disk spool of agent runs that
/// finished past the retention window. Thin Mediator dispatcher (Rule 14) — the logic lives in
/// <see cref="Services.Agents.IAgentRunSpoolReaper"/>.
///
/// <para>Hourly is ample: a terminal run's spool is disk debris (its redacted output is already in the durable
/// event log), and the reaper only ever touches runs with a CompletedAt past the window, so it can never
/// delete a live run's spool however long it runs. Offset to :30 so it doesn't pile onto the top-of-hour
/// workspace janitor.</para>
/// </summary>
public sealed class AgentRunSpoolReaperRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public AgentRunSpoolReaperRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(AgentRunSpoolReaperRecurringJob);
    public string CronExpression => "30 * * * *";   // half past every hour

    public async Task Execute() => await _mediator.Send(new ReapAgentRunSpoolsCommand()).ConfigureAwait(false);
}
