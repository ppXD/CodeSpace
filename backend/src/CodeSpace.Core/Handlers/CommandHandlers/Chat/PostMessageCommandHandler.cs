using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Commands.Chat;
using CodeSpace.Messages.Dtos.Chat;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Chat;

public sealed class PostMessageCommandHandler : IRequestHandler<PostMessageCommand, MessageView>
{
    private readonly IMessageService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public PostMessageCommandHandler(IMessageService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public Task<MessageView> Handle(PostMessageCommand request, CancellationToken cancellationToken) =>
        _service.PostAsync(_currentTeam.Id!.Value, _currentUser.Id!.Value, request.ConversationId, request.Body, request.ReplyToMessageId, cancellationToken);
}
