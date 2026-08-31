using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace CodeSpace.Core.Services.Workflows.Artifacts;

/// <summary>
/// The mutual exclusion every path that decides WHICH PROVIDER an active route writes through must take.
///
/// <para>That fact has two ends and a writer at each. The profile ledger decides which provider a profile's head
/// names, and refuses a head that active routes already follow
/// (<c>StorageProfileService.EnsureWritersKeepAWritableProviderAsync</c>). The routing ledger decides which routes
/// follow that head, and refuses a head that takes no bytes
/// (<c>StorageRouteService.EnsureProviderAcceptsBytesAsync</c>). Each guard is a snapshot read of the OTHER end's
/// rows, so run concurrently both pass and both commit — landing the one state the rule forbids: an Active route
/// resolving a head that accepts no new bytes, whose every write then fails at the destination with no operator
/// standing. Taking this lock on the profile both ends name is what makes the two readings one.</para>
///
/// <para>Per PROFILE, because the profile is the row both ends name. A sibling of <see cref="StorageBootstrapLock"/>
/// rather than a reuse of it: that lock excludes the decision to give a team its FIRST route for a data class, is
/// keyed and documented for exactly that, and is free to be narrowed later — which would silently un-guard this rule
/// if this rule had been folded into it. Its per-team key would also queue every unrelated storage edit in a team
/// behind one.</para>
///
/// <para>A METHOD rather than an exposed constant, for the reason <see cref="StorageBootstrapLock"/> gives: two
/// callers composing their own <c>pg_advisory_xact_lock</c> are one differently-formatted Guid away from taking two
/// different locks and believing they exclude each other.</para>
/// </summary>
public static class StorageProfileHeadLock
{
    /// <summary>The <c>hashtextextended</c> seed, following this codebase's convention of the migration that introduced the mechanism the lock protects. 134 is migration 0134, which introduced the storage-route ledger. Never change it once shipped: through a rolling deploy the old and the new value are two different locks, and the two ends stop excluding each other for as long as both versions are serving.</summary>
    internal const int Seed = 134;

    /// <summary>
    /// Takes the per-profile head lock, and returns the transaction it had to open to hold it — or null when the
    /// caller already had one.
    ///
    /// <para>Returning it is not a courtesy. <c>pg_advisory_xact_lock</c> is released when its transaction ends, so a
    /// caller with no transaction of its own would take the lock inside an implicit one and drop it again before
    /// reading anything: a guard that reads as protection and is none. A caller handed a transaction back MUST commit
    /// it, and must do so AFTER its own write, or it discards the write it just made. Through the mediator
    /// <c>TransactionalBehavior</c> has already opened one and this joins it, which holds the lock until the whole
    /// command commits — exactly the window the rule needs.</para>
    ///
    /// <para>Blocks rather than failing: the loser is meant to proceed and re-read, and then be refused by the rule
    /// itself if the winner moved the head out from under it.</para>
    /// </summary>
    public static async Task<IDbContextTransaction?> TakeAsync(DatabaseFacade database, Guid profileId, CancellationToken cancellationToken)
    {
        var owned = database.CurrentTransaction == null ? await database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false) : null;

        await database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({profileId.ToString()}, {Seed}))", cancellationToken).ConfigureAwait(false);

        return owned;
    }
}
