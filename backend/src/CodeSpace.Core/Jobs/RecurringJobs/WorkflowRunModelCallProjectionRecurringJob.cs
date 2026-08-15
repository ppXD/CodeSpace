using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Projects bounded interaction batches every minute. Admission is source-id/idempotency based; a missed or repeated
/// tick changes only projection latency and can never skip facts or alter the Workflow Run that produced them.
/// </summary>
public sealed class WorkflowRunModelCallProjectionRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public WorkflowRunModelCallProjectionRecurringJob(IMediator mediator) => _mediator = mediator;

    public string JobId => nameof(WorkflowRunModelCallProjectionRecurringJob);
    public string CronExpression => "* * * * *";

    public async Task Execute() => await _mediator.Send(new ProjectWorkflowRunModelCallsCommand()).ConfigureAwait(false);
}
