using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>Runs the bounded lease/fence AgentRun log health reconciler every minute.</summary>
public sealed class AgentRunLogCaptureRecoveryRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public AgentRunLogCaptureRecoveryRecurringJob(IMediator mediator) => _mediator = mediator;

    public string JobId => nameof(AgentRunLogCaptureRecoveryRecurringJob);
    public string CronExpression => "* * * * *";

    public async Task Execute() => await _mediator.Send(new ReconcileAgentRunLogCapturesCommand()).ConfigureAwait(false);
}
