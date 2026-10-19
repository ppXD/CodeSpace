using System.Linq.Expressions;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace CodeSpace.Core.Authorization;

/// <summary>
/// Standing in a team, asked of anyone rather than of the caller: a membership row in the team, or the instance Admin
/// role, which has the standing of an Owner in every team — what <see cref="TeamMembershipResolver"/> grants the caller.
/// The role counts while it is switched on, the same <c>role_user</c> → <c>role</c> join the caller's own roles are read
/// through. Whether the team is live is not part of it; the seams that care ask that separately.
///
/// <para>Defined once because several seams ask it of someone other than the caller, and must agree: the departed-token
/// sweep and a departure (what to retire), the holds a write in someone's name takes (whether to write it), and the
/// act-as picker and the write it feeds (whom to act as). Read as a membership row alone, the sweep retired every token
/// an instance Admin had lent a team without joining it.</para>
/// </summary>
public static class TeamStanding
{
    /// <summary>
    /// The rule as a filter over rows that each name a team and a person — a credential and whoever lent it, an identity
    /// and its user — composed into one predicate EF translates. Both selectors must read from the same row.
    /// </summary>
    public static Expression<Func<T, bool>> Of<T>(CodeSpaceDbContext db, Expression<Func<T, Guid>> team, Expression<Func<T, Guid>> person)
    {
        var row = team.Parameters[0];
        var personOfRow = ReplacingExpressionVisitor.Replace(person.Parameters[0], row, person.Body);
        var rule = Rule(db);

        return Expression.Lambda<Func<T, bool>>(ReplacingExpressionVisitor.Replace(rule.Parameters, new[] { team.Body, personOfRow }, rule.Body), row);
    }

    /// <summary>Whether <paramref name="userId"/> has standing in <paramref name="teamId"/> as of now. Someone with no account has none.</summary>
    public static async Task<bool> HeldAsync(CodeSpaceDbContext db, Guid teamId, Guid userId, CancellationToken cancellationToken) =>
        await db.User.IgnoreQueryFilters().AsNoTracking().Where(u => u.Id == userId).AnyAsync(Of<User>(db, _ => teamId, u => u.Id), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// What that standing amounts to as a role, asked of anyone — what <see cref="TeamMembershipResolver.ResolveRoleAsync"/> answers for the
    /// caller, for a request that has no caller to ask it of (the provider's OAuth callback is anonymous): an instance Admin stands as an
    /// Owner, anyone else as the role of their membership row in a live team; null when they have neither. Agrees with
    /// <see cref="HeldAsync"/>, which differs only in answering whether rather than what — and, for a member, in treating a deleted team as no
    /// standing, as the resolver does.
    /// </summary>
    public static async Task<TeamRole?> RoleHeldAsync(CodeSpaceDbContext db, Guid teamId, Guid userId, CancellationToken cancellationToken)
    {
        var isInstanceAdmin = await db.RoleUser.AsNoTracking().AnyAsync(ru => ru.UserId == userId && db.Role.Any(r => r.Id == ru.RoleId && r.Name == Roles.Admin && r.Status), cancellationToken).ConfigureAwait(false);

        if (isInstanceAdmin) return TeamRole.Owner;

        return await db.Team.AsNoTracking()
            .Where(t => t.Id == teamId && t.DeletedDate == null)
            .SelectMany(t => t.Memberships.Where(m => m.UserId == userId).Select(m => (TeamRole?)m.Role))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What each person is to someone ACTING ON them — naming them as a credential's owner, signing in as them — not to someone asking
    /// what they may do: the role of their membership row in a live team, else an instance Admin's standing as an Owner; absent when they have
    /// neither. It is the target's side of every reach (<see cref="TeamRoleRank.Reaches"/>), which reads a member's row before an instance
    /// Admin's role — the opposite of <see cref="RoleHeldAsync"/>, which answers for someone acting and so lets the role win. One definition for
    /// the service that enforces the reach (<c>TeamMemberService.EnsureMayActOnAsync</c>), the callback that asks it again and the remedy
    /// and the credential list that say, beforehand, whether it would hold — so a button and the endpoint behind it read the person alike.
    /// Asked for many people at once because a list of credentials has one owner each.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, TeamRole>> RolesActedOnAsync(CodeSpaceDbContext db, Guid teamId, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        var roles = new Dictionary<Guid, TeamRole>();

        if (userIds.Count == 0 || !await db.Team.AsNoTracking().AnyAsync(t => t.Id == teamId && t.DeletedDate == null, cancellationToken).ConfigureAwait(false)) return roles;

        var rows = await db.TeamMembership.AsNoTracking().Where(m => m.TeamId == teamId && userIds.Contains(m.UserId)).Select(m => new { m.UserId, m.Role }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var instanceAdmins = await db.RoleUser.AsNoTracking().Where(ru => userIds.Contains(ru.UserId) && db.Role.Any(r => r.Id == ru.RoleId && r.Name == Roles.Admin && r.Status)).Select(ru => ru.UserId).ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var row in rows) roles[row.UserId] = row.Role;
        foreach (var admin in instanceAdmins) roles.TryAdd(admin, TeamRole.Owner);

        return roles;
    }

    /// <summary><see cref="RolesActedOnAsync"/> for one person: null when they have no standing.</summary>
    public static async Task<TeamRole?> RoleActedOnAsync(CodeSpaceDbContext db, Guid teamId, Guid userId, CancellationToken cancellationToken)
    {
        var roles = await RolesActedOnAsync(db, teamId, new[] { userId }, cancellationToken).ConfigureAwait(false);

        return roles.TryGetValue(userId, out var role) ? role : null;
    }

    private static Expression<Func<Guid, Guid, bool>> Rule(CodeSpaceDbContext db) => (teamId, userId) =>
        db.TeamMembership.Any(m => m.TeamId == teamId && m.UserId == userId) ||
        db.RoleUser.Any(ru => ru.UserId == userId && db.Role.Any(r => r.Id == ru.RoleId && r.Name == Roles.Admin && r.Status));
}
