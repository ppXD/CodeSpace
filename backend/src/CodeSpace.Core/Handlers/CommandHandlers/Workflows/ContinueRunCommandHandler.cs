using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Workflows;

public sealed class ContinueRunCommandHandler : IRequestHandler<ContinueRunCommand, bool>
{
    private readonly IWorkflowService _service;
    private readonly ICurrentTeam _currentTeam;

    public ContinueRunCommandHandler(IWorkflowService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public async Task<bool> Handle(ContinueRunCommand request, CancellationToken cancellationToken) =>
        await _service.ContinueRunAsync(request.RunId, _currentTeam.Id!.Value, cancellationToken).ConfigureAwait(false);
}
