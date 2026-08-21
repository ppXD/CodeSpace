using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Revisits a bounded source-id anti-join each minute. Repeated or missed ticks change only observation latency;
/// they cannot authorize, execute, replay, approve or alter a governed tool invocation.
/// </summary>
public sealed class WorkflowRunToolCallProjectionRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public WorkflowRunToolCallProjectionRecurringJob(IMediator mediator) => _mediator = mediator;

    public string JobId => nameof(WorkflowRunToolCallProjectionRecurringJob);
    public string CronExpression => "* * * * *";
    public async Task Execute() => await _mediator.Send(new ProjectWorkflowRunToolCallsCommand()).ConfigureAwait(false);
}
