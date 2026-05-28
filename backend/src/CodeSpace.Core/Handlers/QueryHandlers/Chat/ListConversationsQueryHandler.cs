using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Dtos.Chat;
using CodeSpace.Messages.Queries.Chat;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Chat;

public sealed class ListConversationsQueryHandler : IRequestHandler<ListConversationsQuery, IReadOnlyList<ConversationSummary>>
{
    private readonly IConversationService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public ListConversationsQueryHandler(IConversationService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public Task<IReadOnlyList<ConversationSummary>> Handle(ListConversationsQuery request, CancellationToken cancellationToken) =>
        _service.ListForUserAsync(_currentTeam.Id!.Value, _currentUser.Id!.Value, cancellationToken);
}
