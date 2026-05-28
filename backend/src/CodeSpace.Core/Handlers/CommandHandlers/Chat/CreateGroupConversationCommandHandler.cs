using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Commands.Chat;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Chat;

public sealed class CreateGroupConversationCommandHandler : IRequestHandler<CreateGroupConversationCommand, Guid>
{
    private readonly IConversationService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public CreateGroupConversationCommandHandler(IConversationService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public Task<Guid> Handle(CreateGroupConversationCommand request, CancellationToken cancellationToken) =>
        _service.CreateGroupAsync(_currentTeam.Id!.Value, request.Name, request.MemberUserIds, _currentUser.Id!.Value, cancellationToken);
}
