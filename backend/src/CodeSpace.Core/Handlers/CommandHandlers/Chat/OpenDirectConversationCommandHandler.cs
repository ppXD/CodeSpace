using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Commands.Chat;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Chat;

public sealed class OpenDirectConversationCommandHandler : IRequestHandler<OpenDirectConversationCommand, Guid>
{
    private readonly IConversationService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public OpenDirectConversationCommandHandler(IConversationService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public Task<Guid> Handle(OpenDirectConversationCommand request, CancellationToken cancellationToken) =>
        _service.GetOrCreateDirectAsync(_currentTeam.Id!.Value, _currentUser.Id!.Value, request.OtherUserId, cancellationToken);
}
