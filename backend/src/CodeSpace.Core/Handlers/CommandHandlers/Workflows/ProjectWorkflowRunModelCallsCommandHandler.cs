using CodeSpace.Core.Services.Workflows.ModelCalls;
using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Workflows;

public sealed class ProjectWorkflowRunModelCallsCommandHandler : IRequestHandler<ProjectWorkflowRunModelCallsCommand, int>
{
    private readonly IWorkflowRunModelCallProjector _projector;

    public ProjectWorkflowRunModelCallsCommandHandler(IWorkflowRunModelCallProjector projector) => _projector = projector;

    public async Task<int> Handle(ProjectWorkflowRunModelCallsCommand request, CancellationToken cancellationToken) =>
        (await _projector.SweepAsync(request.BatchSize, cancellationToken).ConfigureAwait(false)).TotalChanges;
}
