using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Constants;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Authorization;

/// <summary>
/// Single source of truth for "is this user allowed to act on this team?" Used by every
/// tenancy pipeline behavior. The Admin role bypasses entirely; non-admins must be the
/// team owner or hold a TeamMembership row.
/// </summary>
public sealed class TeamMembershipResolver : IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ICurrentUser _currentUser;

    public TeamMembershipResolver(CodeSpaceDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task EnsureMembershipAsync(Guid teamId, CancellationToken cancellationToken)
    {
        if (_currentUser.HasRole(Roles.Admin)) return;

        var userId = _currentUser.Id;

        if (userId == null) throw new TenantAccessDeniedException(null, teamId, "no authenticated user on request");

        var allowed = await _db.Team.AsNoTracking()
            .AnyAsync(t => t.Id == teamId && t.DeletedDate == null && (t.OwnerUserId == userId.Value || t.Memberships.Any(m => m.UserId == userId.Value)), cancellationToken)
            .ConfigureAwait(false);

        if (!allowed) throw new TenantAccessDeniedException(userId, teamId, "user is not a member of this team");
    }
}
