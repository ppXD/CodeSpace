using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Authorization;

/// <summary>
/// Single source of truth for "is this user allowed to act on this team, and as what?" Used by every
/// tenancy pipeline behavior. The Admin role bypasses entirely; non-admins must hold a TeamMembership
/// row, and its role is their answer.
/// </summary>
public sealed class TeamMembershipResolver : IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly Dictionary<Guid, TeamRole> _resolved = new();

    public TeamMembershipResolver(CodeSpaceDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task EnsureMembershipAsync(Guid teamId, CancellationToken cancellationToken)
    {
        if (_currentUser.HasRole(Roles.Admin)) return;

        await ResolveRoleAsync(teamId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The caller's effective role on this team, or <see cref="TenantAccessDeniedException"/> when they
    /// have no standing at all — which is what lets <see cref="EnsureMembershipAsync"/> delegate to it
    /// instead of issuing a second query.
    ///
    /// <para>The membership row is the whole answer, including for owners. It used to be OR'd with
    /// <c>team.owner_user_id</c>, and that column outlived every departure: leaving a team deletes the
    /// row and cannot delete a column, so the person who walked out kept resolving to
    /// <see cref="TeamRole.Owner"/> here. It also WON, so an account the column named read as Owner
    /// past a demotion that had already rewritten their row.</para>
    ///
    /// <para>Memoized per team for the lifetime of the scope: the membership and permission behaviors
    /// both ask, and authorization is decided once at request entry, so re-reading inside the same
    /// request could only pick up a change the request itself is making.</para>
    /// </summary>
    public async Task<TeamRole> ResolveRoleAsync(Guid teamId, CancellationToken cancellationToken)
    {
        if (_currentUser.HasRole(Roles.Admin)) return TeamRole.Owner;

        if (_resolved.TryGetValue(teamId, out var memoized)) return memoized;

        var userId = _currentUser.Id;

        if (userId == null) throw new TenantAccessDeniedException(null, teamId, "no authenticated user on request");

        // Rooted on the team so a soft-deleted one denies everyone, membership rows or not — they are
        // a hard-delete junction table and outlive the team they point at.
        var role = await _db.Team.AsNoTracking()
            .Where(t => t.Id == teamId && t.DeletedDate == null)
            .SelectMany(t => t.Memberships.Where(m => m.UserId == userId.Value).Select(m => (TeamRole?)m.Role))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (role == null) throw new TenantAccessDeniedException(userId, teamId, "user is not a member of this team");

        _resolved[teamId] = role.Value;

        return role.Value;
    }
}
