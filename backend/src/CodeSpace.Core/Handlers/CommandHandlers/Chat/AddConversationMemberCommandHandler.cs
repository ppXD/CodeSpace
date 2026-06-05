using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Commands.Chat;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Chat;

public sealed class AddConversationMemberCommandHandler : IRequestHandler<AddConversationMemberCommand, Unit>
{
    private readonly IConversationService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public AddConversationMemberCommandHandler(IConversationService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<Unit> Handle(AddConversationMemberCommand request, CancellationToken cancellationToken)
    {
        await _service.AddMemberAsync(_currentTeam.Id!.Value, _currentUser.Id!.Value, request.ConversationId, request.UserId, cancellationToken);
        return Unit.Value;
    }
}
