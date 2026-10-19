using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Authorization;

/// <summary>
/// The rule every credential tier applies, with its own permission: the owner of a Personal credential passes on membership
/// alone; anyone else — on a teammate's credential, or on the team's own, which has no owner — needs the named team permission.
/// An instance Admin resolves as an Owner (<see cref="TeamMembershipResolver"/>), the standing <see cref="TeamStanding"/>
/// gives them in every team.
///
/// <para>Defined once, in <see cref="Permits"/>, because every place that asks must agree: this class enforces it for a request
/// (<see cref="CredentialManagementAuthorizationBehavior{TRequest,TResponse}"/>, the spend tier and the OAuth connect's
/// <see cref="CredentialReauthorizationAuthorizationBehavior{TRequest,TResponse}"/>), <c>CallerStanding</c> decides a remedy's
/// buttons — and the credential list's <c>callerMayRepair</c>, which the repository rows read — by it, and the OAuth callback asks it
/// again of whoever started a reconnect — so a button is never shown to a person the endpoint behind it refuses, and a connect is
/// never finished under a permission that was taken back.</para>
///
/// <para>A reconnect is held to one more thing, which is <see cref="PermitsReconnect"/>: the reach over the credential's person.</para>
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

    /// <summary>
    /// The rule: a credential's own person passes, and anyone else needs <paramref name="permission"/>. Pure, so the same answer is the
    /// one a request gets, a button shows and a callback asks again. <paramref name="ownsThePersonalCredential"/> is the credential
    /// being Personal and the person being its owner — a team-service credential has none, so it is never true for one.
    /// </summary>
    public static bool Permits(TeamRole role, bool ownsThePersonalCredential, string permission) => ownsThePersonalCredential || TeamPermissionMatrix.IsGranted(role, permission);

    /// <summary>
    /// A reconnect, which is the rule above and one more: it signs in AS the credential's person, so it acts on a person and not only on a
    /// credential, and holds to the reach every act on a member keeps (<see cref="TeamRoleRank.Reaches"/>) — an Admin does not reach another
    /// Admin or an Owner, nobody reaches an Owner but themselves. <paramref name="ownerStanding"/> is the role the person stands as in the team
    /// (<see cref="TeamStanding.RolesActedOnAsync"/>), and null when they have none — they left, or the credential has no person — which nobody
    /// can reconnect. What the service refuses with <c>RoleOutranksActorException</c> or <c>KeyNotFoundException</c> after the pipeline has let
    /// the request in is this answer, so a button, the service and the OAuth callback all ask it here.
    /// </summary>
    public static bool PermitsReconnect(TeamRole role, bool ownsThePersonalCredential, TeamRole? ownerStanding) =>
        ownerStanding is { } owner && Permits(role, ownsThePersonalCredential, TeamPermissions.CredentialsManage) && TeamRoleRank.Reaches(role, owner, isOwn: ownsThePersonalCredential);

    /// <summary>
    /// For the caller on this team. <paramref name="credentialId"/> is null for a request that may name no credential — a connect that
    /// mints one has no owner to pass, so it is the permission's alone.
    /// </summary>
    public async Task EnsureAsync(Guid? credentialId, string permission, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id ?? throw TenantAccessDeniedException.NoTeamSelected(_currentUser.Id, HeaderCurrentTeam.HeaderName);

        var role = await _resolver.ResolveRoleAsync(teamId, cancellationToken).ConfigureAwait(false);
        var owns = credentialId is Guid id && await IsCallersOwnPersonalCredentialAsync(id, teamId, cancellationToken).ConfigureAwait(false);

        if (!Permits(role, owns, permission))
            throw TenantAccessDeniedException.MissingTeamPermission(_currentUser.Id, teamId, role, permission);
    }

    /// <summary>A live credential of the team being asked about: an owner's pass is for a credential of THIS team, whatever else the request proved.</summary>
    private async Task<bool> IsCallersOwnPersonalCredentialAsync(Guid credentialId, Guid teamId, CancellationToken cancellationToken)
    {
        if (_currentUser.Id is not { } callerId) return false;

        return await _db.Credential.AsNoTracking()
            .AnyAsync(c => c.Id == credentialId && c.TeamId == teamId && c.DeletedDate == null && c.Ownership == CredentialOwnership.Personal && c.OwnerUserId == callerId, cancellationToken)
            .ConfigureAwait(false);
    }
}
