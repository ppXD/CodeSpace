using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Authorization;

/// <summary>
/// Single source of truth for "is this user allowed to act on this team, and as what?" Used by every
/// tenancy pipeline behavior. The Admin role bypasses entirely; non-admins must be the
/// team owner or hold a TeamMembership row.
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
    /// <para>Ownership is recorded on <c>team.owner_user_id</c> and does NOT require a TeamMembership
    /// row (only the personal-team backfill in migration 0008 writes one), so an owner without a
    /// membership row resolves to <see cref="TeamRole.Owner"/> synthetically. A lookup that read only
    /// the membership table would lock an owner out of their own team.</para>
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

        var standing = await _db.Team.AsNoTracking()
            .Where(t => t.Id == teamId && t.DeletedDate == null)
            .Select(t => new { IsOwner = t.OwnerUserId == userId.Value, MemberRoles = t.Memberships.Where(m => m.UserId == userId.Value).Select(m => m.Role).ToList() })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (standing == null || (!standing.IsOwner && standing.MemberRoles.Count == 0)) throw new TenantAccessDeniedException(userId, teamId, "user is not a member of this team");

        var role = standing.IsOwner ? TeamRole.Owner : standing.MemberRoles[0];

        _resolved[teamId] = role;

        return role;
    }
}
