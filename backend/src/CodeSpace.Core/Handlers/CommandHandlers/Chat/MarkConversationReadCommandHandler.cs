using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Commands.Chat;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Chat;

public sealed class MarkConversationReadCommandHandler : IRequestHandler<MarkConversationReadCommand, Unit>
{
    private readonly IMessageService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public MarkConversationReadCommandHandler(IMessageService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<Unit> Handle(MarkConversationReadCommand request, CancellationToken cancellationToken)
    {
        await _service.MarkReadAsync(_currentTeam.Id!.Value, _currentUser.Id!.Value, request.ConversationId, request.LastReadMessageId, cancellationToken);
        return Unit.Value;
    }
}
