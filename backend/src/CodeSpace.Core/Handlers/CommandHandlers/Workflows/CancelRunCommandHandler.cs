using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Workflows;

public sealed class CancelRunCommandHandler : IRequestHandler<CancelRunCommand, CancelRunOutcome?>
{
    private readonly IWorkflowService _service;
    private readonly ICurrentTeam _currentTeam;

    public CancelRunCommandHandler(IWorkflowService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public async Task<CancelRunOutcome?> Handle(CancelRunCommand request, CancellationToken cancellationToken) =>
        await _service.CancelRunAsync(request.RunId, _currentTeam.Id!.Value, cancellationToken).ConfigureAwait(false);
}
