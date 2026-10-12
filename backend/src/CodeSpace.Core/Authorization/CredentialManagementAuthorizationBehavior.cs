using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Authorization;

/// <summary>
/// The owner of a Personal credential manages it on membership alone; every other caller needs
/// <see cref="TeamPermissions.CredentialsManage"/>. Runs inside <see cref="CredentialAccessAuthorizationBehavior{TRequest,TResponse}"/>,
/// which has already proven the credential is the header team's and the caller a member of it.
/// </summary>
public sealed class CredentialManagementAuthorizationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequireCredentialManagement
{
    private readonly CodeSpaceDbContext _db;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;
    private readonly TeamMembershipResolver _resolver;

    public CredentialManagementAuthorizationBehavior(CodeSpaceDbContext db, ICurrentTeam currentTeam, ICurrentUser currentUser, TeamMembershipResolver resolver)
    {
        _db = db;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
        _resolver = resolver;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (await IsCallersOwnPersonalCredentialAsync(request.CredentialId, cancellationToken).ConfigureAwait(false)) return await next().ConfigureAwait(false);

        await EnsureCredentialsManageAsync(cancellationToken).ConfigureAwait(false);

        return await next().ConfigureAwait(false);
    }

    private async Task<bool> IsCallersOwnPersonalCredentialAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        if (_currentUser.Id is not { } callerId) return false;

        return await _db.Credential.AsNoTracking()
            .AnyAsync(c => c.Id == credentialId && c.DeletedDate == null && c.Ownership == CredentialOwnership.Personal && c.OwnerUserId == callerId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task EnsureCredentialsManageAsync(CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id ?? throw TenantAccessDeniedException.NoTeamSelected(_currentUser.Id, HeaderCurrentTeam.HeaderName);

        var role = await _resolver.ResolveRoleAsync(teamId, cancellationToken).ConfigureAwait(false);

        if (!TeamPermissionMatrix.IsGranted(role, TeamPermissions.CredentialsManage))
            throw TenantAccessDeniedException.MissingTeamPermission(_currentUser.Id, teamId, role, TeamPermissions.CredentialsManage);
    }
}
