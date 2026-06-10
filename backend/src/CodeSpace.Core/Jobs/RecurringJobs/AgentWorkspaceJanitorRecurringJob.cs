using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Hourly, dispatches <see cref="SweepStaleAgentWorkspacesCommand"/> to reclaim agent workspaces orphaned
/// by a crashed worker (the happy-path <c>DisposeAsync</c> can't run if the worker died). Thin Mediator
/// dispatcher (Rule 14) — the sweep logic lives in each <c>IWorkspaceJanitor</c>.
///
/// <para>Hourly is ample: orphans are disk debris, not a correctness hazard, and the age threshold
/// (default 2h) far exceeds any run, so a sweep can never touch a live workspace.</para>
/// </summary>
public sealed class AgentWorkspaceJanitorRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public AgentWorkspaceJanitorRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(AgentWorkspaceJanitorRecurringJob);
    public string CronExpression => "0 * * * *";   // top of every hour

    public async Task Execute() => await _mediator.Send(new SweepStaleAgentWorkspacesCommand()).ConfigureAwait(false);
}
