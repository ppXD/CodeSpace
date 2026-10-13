using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Authorization;

/// <summary>
/// The rule every credential tier inside <see cref="CredentialAccessAuthorizationBehavior{TRequest,TResponse}"/> applies,
/// with its own permission: the owner of a Personal credential passes on the membership that behavior already proved;
/// anyone else — on a teammate's credential, or on the team's own, which has no owner — needs the named team permission.
/// An instance Admin resolves as an Owner (<see cref="TeamMembershipResolver"/>), the standing <see cref="TeamStanding"/>
/// gives them in every team.
/// </summary>
public sealed class CredentialOwnerOrPermission : IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;
    private readonly TeamMembershipResolver _resolver;

    public CredentialOwnerOrPermission(CodeSpaceDbContext db, ICurrentTeam currentTeam, ICurrentUser currentUser, TeamMembershipResolver resolver)
    {
        _db = db;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
        _resolver = resolver;
    }

    public async Task EnsureAsync(Guid credentialId, string permission, CancellationToken cancellationToken)
    {
        if (await IsCallersOwnPersonalCredentialAsync(credentialId, cancellationToken).ConfigureAwait(false)) return;

        await EnsurePermissionAsync(permission, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> IsCallersOwnPersonalCredentialAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        if (_currentUser.Id is not { } callerId) return false;

        return await _db.Credential.AsNoTracking()
            .AnyAsync(c => c.Id == credentialId && c.DeletedDate == null && c.Ownership == CredentialOwnership.Personal && c.OwnerUserId == callerId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task EnsurePermissionAsync(string permission, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id ?? throw TenantAccessDeniedException.NoTeamSelected(_currentUser.Id, HeaderCurrentTeam.HeaderName);

        var role = await _resolver.ResolveRoleAsync(teamId, cancellationToken).ConfigureAwait(false);

        if (!TeamPermissionMatrix.IsGranted(role, permission))
            throw TenantAccessDeniedException.MissingTeamPermission(_currentUser.Id, teamId, role, permission);
    }
}
