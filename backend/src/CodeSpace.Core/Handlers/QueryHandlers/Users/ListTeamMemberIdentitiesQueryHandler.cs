using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Users;
using CodeSpace.Messages.Dtos.Users;
using CodeSpace.Messages.Queries.Users;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Users;

/// <summary>
/// Resolves to the same <see cref="IUserService.ListTeamMembersAsync"/> as the plain member list —
/// the ONLY difference is the query's <c>IBotInclusive</c> marker, which (via BotVisibilityBehavior)
/// flips the global User filter so the bot row is returned for author-name resolution.
/// </summary>
public sealed class ListTeamMemberIdentitiesQueryHandler : IRequestHandler<ListTeamMemberIdentitiesQuery, IReadOnlyList<TeamMemberSummary>>
{
    private readonly IUserService _users;
    private readonly ICurrentTeam _currentTeam;

    public ListTeamMemberIdentitiesQueryHandler(IUserService users, ICurrentTeam currentTeam)
    {
        _users = users;
        _currentTeam = currentTeam;
    }

    public Task<IReadOnlyList<TeamMemberSummary>> Handle(ListTeamMemberIdentitiesQuery request, CancellationToken cancellationToken) =>
        _users.ListTeamMembersAsync(_currentTeam.Id!.Value, cancellationToken);
}
