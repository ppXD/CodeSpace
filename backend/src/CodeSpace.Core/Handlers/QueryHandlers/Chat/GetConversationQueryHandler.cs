using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Dtos.Chat;
using CodeSpace.Messages.Queries.Chat;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Chat;

public sealed class GetConversationQueryHandler : IRequestHandler<GetConversationQuery, ConversationSummary?>
{
    private readonly IConversationService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public GetConversationQueryHandler(IConversationService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public Task<ConversationSummary?> Handle(GetConversationQuery request, CancellationToken cancellationToken) =>
        _service.GetAsync(_currentTeam.Id!.Value, _currentUser.Id!.Value, request.ConversationId, cancellationToken);
}
