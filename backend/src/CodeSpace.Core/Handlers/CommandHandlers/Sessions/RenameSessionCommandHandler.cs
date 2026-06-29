using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Messages.Commands.Sessions;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Sessions;

/// <summary>Thin dispatcher (Rule 16): sources the team from <see cref="ICurrentTeam"/> (never the body) and delegates the rename to <see cref="IWorkSessionService"/>.</summary>
public sealed class RenameSessionCommandHandler : IRequestHandler<RenameSessionCommand, bool>
{
    private readonly IWorkSessionService _service;
    private readonly ICurrentTeam _currentTeam;

    public RenameSessionCommandHandler(IWorkSessionService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<bool> Handle(RenameSessionCommand request, CancellationToken cancellationToken) =>
        _service.RenameAsync(request.SessionId, request.Title, _currentTeam.Id!.Value, cancellationToken);
}
