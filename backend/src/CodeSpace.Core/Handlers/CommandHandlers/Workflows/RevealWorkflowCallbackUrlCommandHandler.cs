using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Callbacks;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Workflows;

public sealed class RevealWorkflowCallbackUrlCommandHandler : IRequestHandler<RevealWorkflowCallbackUrlCommand, WorkflowCallbackUrl>
{
    private readonly IWorkflowCallbackUrlService _service;
    private readonly ICurrentTeam _currentTeam;

    public RevealWorkflowCallbackUrlCommandHandler(IWorkflowCallbackUrlService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public async Task<WorkflowCallbackUrl> Handle(RevealWorkflowCallbackUrlCommand request, CancellationToken cancellationToken) =>
        await _service.RevealAsync(request.RunId, request.WaitId, _currentTeam.Id!.Value, cancellationToken).ConfigureAwait(false);
}
