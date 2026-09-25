using CodeSpace.Core.Persistence.Db;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Engine;

/// <summary>
/// The run-generation fence (<see cref="Persistence.Entities.WorkflowRun.Generation"/>) for what a walk commits in a
/// transaction of its own. <see cref="TryLockAsync"/> is the share lock a park takes on the run row AT the walk's claimed
/// generation: a Continue's bump either waits for that commit or has already landed, and then nothing is written.
///
/// <para>The engine parks with the generation it holds. The supervisor records its decisions and stages its waves in its
/// own DI scope, where that generation is out of reach, so the engine carries its claim to every node it runs
/// (<see cref="Claim"/>) and those writes read it back (<see cref="ClaimedFor"/>) to take the same lock
/// (<see cref="EnterAsync"/>, <see cref="CommitUnderClaimAsync{T}"/>). It flows with the async call, so two walks of one
/// run on one host — an overtaken one and the revived one — each see their own. A turn driven outside a walk carries no
/// claim, and there is nothing to fence.</para>
/// </summary>
public static class RunGenerationFence
{
    private static readonly AsyncLocal<WalkClaim?> Current = new();

    /// <summary>Carry <paramref name="generation"/> as the claim of the walk running <paramref name="runId"/> until the returned handle is disposed, which restores the claim it replaced.</summary>
    public static IDisposable Claim(Guid runId, int generation)
    {
        var replaced = Current.Value;
        Current.Value = new WalkClaim(runId, generation);
        return new Restore(replaced);
    }

    /// <summary>The generation the walk executing this code claimed for <paramref name="runId"/>, or null outside a walk of that run.</summary>
    public static int? ClaimedFor(Guid runId) => Current.Value is { } claim && claim.RunId == runId ? claim.Generation : null;

    /// <summary>
    /// Share-lock the run row while it still stands at <paramref name="generation"/>; false once a Continue has moved it.
    /// Held to the end of the caller's transaction, so a Continue's bump waits for it — which is why it refuses to run
    /// outside one: there the lock would end with its own statement, and the fence would be only a check.
    /// </summary>
    public static async Task<bool> TryLockAsync(CodeSpaceDbContext db, Guid runId, int generation, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException($"The generation fence on run {runId} needs the caller's transaction: outside one its share lock ends with its own statement.");

        return (await db.Database.SqlQuery<int>($"SELECT 1 AS \"Value\" FROM workflow_run WHERE id = {runId} AND generation = {generation} FOR SHARE").ToListAsync(cancellationToken).ConfigureAwait(false)).Count > 0;
    }

    /// <summary>The fence for a transaction the caller already has open: share-lock the run at the current walk's claim, or throw <see cref="RunSupersededException"/> once a Continue has moved it. Outside a walk it does nothing.</summary>
    public static async Task EnterAsync(CodeSpaceDbContext db, Guid runId, CancellationToken cancellationToken)
    {
        if (ClaimedFor(runId) is not { } generation) return;

        if (!await TryLockAsync(db, runId, generation, cancellationToken).ConfigureAwait(false))
            throw new RunSupersededException(generation);
    }

    /// <summary>Commit <paramref name="write"/> under the current walk's claim on <paramref name="runId"/>: in one transaction (the caller's, or its own), fenced first, so a walk a Continue overtook writes nothing and stands down with <see cref="RunSupersededException"/>. Outside a walk the write commits as it always did.</summary>
    public static async Task<T> CommitUnderClaimAsync<T>(CodeSpaceDbContext db, Guid runId, Func<Task<T>> write, CancellationToken cancellationToken)
    {
        if (ClaimedFor(runId) is null) return await write().ConfigureAwait(false);

        await using var transaction = await ScopedTransaction.OwnOrJoinAsync(db.Database, cancellationToken).ConfigureAwait(false);

        await EnterAsync(db, runId, cancellationToken).ConfigureAwait(false);

        var result = await write().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <summary>Stand down before a step that writes nothing itself — a re-park on a wave or question already staged: throw <see cref="RunSupersededException"/> once a Continue has moved the run past the current walk's claim. A read, not a lock: what follows is fenced where it writes.</summary>
    public static async Task ThrowIfSupersededAsync(CodeSpaceDbContext db, Guid runId, CancellationToken cancellationToken)
    {
        if (ClaimedFor(runId) is not { } generation) return;

        if (!await db.WorkflowRun.AsNoTracking().AnyAsync(r => r.Id == runId && r.Generation == generation, cancellationToken).ConfigureAwait(false))
            throw new RunSupersededException(generation);
    }

    private sealed record WalkClaim(Guid RunId, int Generation);

    private sealed class Restore : IDisposable
    {
        private readonly WalkClaim? _replaced;

        public Restore(WalkClaim? replaced) { _replaced = replaced; }

        public void Dispose() => Current.Value = _replaced;
    }
}
