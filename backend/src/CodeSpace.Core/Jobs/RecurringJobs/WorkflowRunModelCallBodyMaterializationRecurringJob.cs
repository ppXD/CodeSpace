using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

public sealed class WorkflowRunModelCallBodyMaterializationRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public WorkflowRunModelCallBodyMaterializationRecurringJob(IMediator mediator) => _mediator = mediator;

    public string JobId => nameof(WorkflowRunModelCallBodyMaterializationRecurringJob);
    public string CronExpression => "* * * * *";

    public async Task Execute() => await _mediator.Send(new MaterializeWorkflowRunModelCallBodiesCommand()).ConfigureAwait(false);
}
