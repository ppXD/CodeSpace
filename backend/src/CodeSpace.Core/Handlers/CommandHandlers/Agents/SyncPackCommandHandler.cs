using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Agents;

public sealed class SyncPackCommandHandler : IRequestHandler<SyncPackCommand, PackSyncResult>
{
    private readonly IPackImportService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public SyncPackCommandHandler(IPackImportService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public Task<PackSyncResult> Handle(SyncPackCommand request, CancellationToken cancellationToken) =>
        _service.SyncAsync(_currentTeam.Id!.Value, request.PackId, _currentUser.Id!.Value, cancellationToken);
}
