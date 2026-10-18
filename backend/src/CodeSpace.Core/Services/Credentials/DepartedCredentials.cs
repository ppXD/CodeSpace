using CodeSpace.Core.Authorization;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// The one definition of a credential whose person has left, asked by the sweep that retires them and by the credential list
/// that says one is about to be: written once because the two must agree, and a list that called a credential "owner left"
/// by a looser rule would promise a retirement that never comes.
/// </summary>
internal static class DepartedCredentials
{
    /// <summary>
    /// Live Personal credentials whose person has no standing in the credential's team (<see cref="TeamStanding"/>): no
    /// membership row there, and not an instance Admin. The person is the owner, or — for a token stored without one, as
    /// adding a token without naming an owner did before the owner defaulted to the caller — whoever added it.
    ///
    /// <para>Only a person with an account. An owner always has one (the column is a foreign key); a creator stamp is not
    /// one — it can be empty, or name nobody when a script wrote the row — and then nothing says who lent the token or
    /// whether they left. That token is kept: retiring it would break every repository bound through it on a guess. A
    /// soft-deleted account is still somebody, and goes like anyone else without standing.</para>
    /// </summary>
    public static IQueryable<Credential> Of(CodeSpaceDbContext db) =>
        db.Credential
            .Where(c => c.Ownership == CredentialOwnership.Personal && c.DeletedDate == null)
            .Where(c => db.User.IgnoreQueryFilters().Any(u => u.Id == (c.OwnerUserId ?? c.CreatedBy)))
            .Where(ExpressionPredicate.Not(TeamStanding.Of<Credential>(db, c => c.TeamId, c => c.OwnerUserId ?? c.CreatedBy)));
}
