using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Authorization;
using MediatR;

namespace CodeSpace.Core.Authorization;

public sealed class TeamPermissionAuthorizationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequireTeamPermission
{
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;
    private readonly TeamMembershipResolver _resolver;

    public TeamPermissionAuthorizationBehavior(ICurrentTeam currentTeam, ICurrentUser currentUser, TeamMembershipResolver resolver)
    {
        _currentTeam = currentTeam;
        _currentUser = currentUser;
        _resolver = resolver;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id ?? throw TenantAccessDeniedException.NoTeamSelected(_currentUser.Id, HeaderCurrentTeam.HeaderName);

        var role = await _resolver.ResolveRoleAsync(teamId, cancellationToken).ConfigureAwait(false);

        if (!TeamPermissionMatrix.IsGranted(role, request.RequiredPermission)) throw TenantAccessDeniedException.MissingTeamPermission(_currentUser.Id, teamId, role, request.RequiredPermission);

        return await next().ConfigureAwait(false);
    }
}
