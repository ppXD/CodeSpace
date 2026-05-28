using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Users;
using CodeSpace.Messages.Dtos.Users;
using CodeSpace.Messages.Queries.Users;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Users;

public sealed class ListTeamMembersQueryHandler : IRequestHandler<ListTeamMembersQuery, IReadOnlyList<TeamMemberSummary>>
{
    private readonly IUserService _users;
    private readonly ICurrentTeam _currentTeam;

    public ListTeamMembersQueryHandler(IUserService users, ICurrentTeam currentTeam)
    {
        _users = users;
        _currentTeam = currentTeam;
    }

    public Task<IReadOnlyList<TeamMemberSummary>> Handle(ListTeamMembersQuery request, CancellationToken cancellationToken) =>
        _users.ListTeamMembersAsync(_currentTeam.Id!.Value, cancellationToken);
}
