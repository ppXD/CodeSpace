using System.Linq.Expressions;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Constants;
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

    private static Expression<Func<Guid, Guid, bool>> Rule(CodeSpaceDbContext db) => (teamId, userId) =>
        db.TeamMembership.Any(m => m.TeamId == teamId && m.UserId == userId) ||
        db.RoleUser.Any(ru => ru.UserId == userId && db.Role.Any(r => r.Id == ru.RoleId && r.Name == Roles.Admin && r.Status));
}
