using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Users;

/// <summary>
/// Which live account an address names — one rule for sign-in and for invitations.
///
/// <para>The address exactly as given first, then the same address ignoring letter case, and that only when a single
/// live account holds it. The case-insensitive unique index (migration 0245) is skipped on a deployment that already
/// had two accounts at one address in different case, so this cannot assume one row: those accounts each keep signing
/// in with their own spelling, and a spelling that matches neither exactly names nobody rather than whichever row the
/// database returned first.</para>
/// </summary>
public static class AccountAddress
{
    public static async Task<User?> ResolveAsync(IQueryable<User> users, string address, CancellationToken cancellationToken)
    {
        var live = users.Where(u => u.DeletedDate == null);
        var folded = address.ToLowerInvariant();

        return await SoleAsync(live.Where(u => u.Email == address), cancellationToken).ConfigureAwait(false)
            ?? await SoleAsync(live.Where(u => u.Email.ToLower() == folded), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether any live account holds this address in any letter case — the test a new account's address must pass.</summary>
    public static async Task<bool> IsHeldAsync(IQueryable<User> users, string address, CancellationToken cancellationToken)
    {
        var folded = address.ToLowerInvariant();

        return await users.AnyAsync(u => u.DeletedDate == null && u.Email.ToLower() == folded, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The one row a query matches, or null when it matches none or more than one.</summary>
    public static async Task<User?> SoleAsync(IQueryable<User> matches, CancellationToken cancellationToken)
    {
        var firstTwo = await matches.Take(2).ToListAsync(cancellationToken).ConfigureAwait(false);

        return firstTwo.Count == 1 ? firstTwo[0] : null;
    }
}
