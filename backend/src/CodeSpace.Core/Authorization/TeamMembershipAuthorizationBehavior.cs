using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Authorization;
using MediatR;

namespace CodeSpace.Core.Authorization;

public sealed class TeamMembershipAuthorizationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequireTeamMembership
{
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;
    private readonly TeamMembershipResolver _resolver;

    public TeamMembershipAuthorizationBehavior(ICurrentTeam currentTeam, ICurrentUser currentUser, TeamMembershipResolver resolver)
    {
        _currentTeam = currentTeam;
        _currentUser = currentUser;
        _resolver = resolver;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id ?? throw TenantAccessDeniedException.NoTeamSelected(_currentUser.Id, HeaderCurrentTeam.HeaderName);

        await _resolver.EnsureMembershipAsync(teamId, cancellationToken).ConfigureAwait(false);

        return await next().ConfigureAwait(false);
    }
}
