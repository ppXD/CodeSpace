using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Dtos.Chat;
using CodeSpace.Messages.Queries.Chat;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Chat;

public sealed class ListMessagesQueryHandler : IRequestHandler<ListMessagesQuery, MessagePage>
{
    private readonly IMessageService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public ListMessagesQueryHandler(IMessageService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public Task<MessagePage> Handle(ListMessagesQuery request, CancellationToken cancellationToken) =>
        _service.ListAsync(_currentTeam.Id!.Value, _currentUser.Id!.Value, request.ConversationId, request.BeforeId, request.Limit, cancellationToken);
}
