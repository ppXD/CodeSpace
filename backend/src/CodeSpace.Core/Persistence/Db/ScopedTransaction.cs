using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace CodeSpace.Core.Persistence.Db;

/// <summary>
/// The handle <see cref="ScopedTransaction.OwnOrJoinAsync"/> returns. Commit and dispose both act on the transaction
/// this handle OWNS and on nothing else, so the same call site is correct whether or not a caller opened one first.
/// </summary>
public interface IOwnedTransaction : IAsyncDisposable
{
    /// <summary>Commits an owned transaction; a no-op when this handle joined a caller's transaction.</summary>
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A transaction on the SCOPED <see cref="CodeSpaceDbContext"/> that the service either owns or joins — and which
/// commits only the one it owns.
///
/// <para>Why: <c>TransactionalBehavior</c> opens one transaction per <c>ICommand</c> on that same scoped context, so
/// any service reached through the mediator already runs inside one. Npgsql refuses a second — "The connection is
/// already in a transaction and cannot participate in another transaction" — so a service that opened its own
/// UNCONDITIONALLY threw on every such call. That is not hypothetical: it is what silently broke the budget
/// settlement sweep, the lesson distiller and the agent-run spool reaper, each from the day its caller became
/// transactional, and what broke an operator's run cancel outright. A service cannot know which door it was called
/// through, so the decision belongs here rather than in each one.</para>
///
/// <para>Joined, <see cref="IOwnedTransaction.CommitAsync"/> is a no-op: the owner decides when — and whether — the
/// work becomes durable. Disposal is a no-op too, so a caller that exits early does NOT roll the owner back. That
/// cuts both ways, and is the rule for choosing this helper: a site that relies on disposing WITHOUT committing to
/// DISCARD writes it already made, or that calls <c>RollbackAsync</c> and then carries on, must keep owning its own
/// transaction (a savepoint, not a join, is the tool there) — joined, its discard would silently become a keep.</para>
///
/// <para>A <c>pg_advisory_xact_lock</c> taken inside a JOINED transaction is held until the OWNER commits, not until
/// the service method returns. That is longer than the unjoined case and is the intended trade: the lock then covers
/// the whole command, which is the window the caller's own write needs anyway. A site that needs the lock released
/// EARLY cannot join, and must stay owning-only for that reason.</para>
/// </summary>
public static class ScopedTransaction
{
    /// <summary>Joins the ambient transaction when the scoped context already has one, otherwise opens and owns a new one.</summary>
    public static async Task<IOwnedTransaction> OwnOrJoinAsync(DatabaseFacade database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        if (database.CurrentTransaction is not null) return new JoinedOrOwnedTransaction(null);

        return new JoinedOrOwnedTransaction(await database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false));
    }

    private sealed class JoinedOrOwnedTransaction : IOwnedTransaction
    {
        private readonly IDbContextTransaction? _owned;

        public JoinedOrOwnedTransaction(IDbContextTransaction? owned) { _owned = owned; }

        public Task CommitAsync(CancellationToken cancellationToken) => _owned?.CommitAsync(cancellationToken) ?? Task.CompletedTask;

        public ValueTask DisposeAsync() => _owned?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
