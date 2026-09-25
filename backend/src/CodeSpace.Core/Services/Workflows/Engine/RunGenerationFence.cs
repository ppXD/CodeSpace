using CodeSpace.Core.Persistence.Db;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Engine;

/// <summary>
/// The run-generation fence (<see cref="Persistence.Entities.WorkflowRun.Generation"/>) for what a walk commits in a
/// transaction of its own. <see cref="TryLockAsync"/> is the share lock a park takes on the run row AT the walk's claimed
/// generation: a Continue's bump either waits for that commit or has already landed, and then nothing is written.
///
/// <para>The engine parks with the generation it holds. The supervisor stages its spawn wave in its own DI scope and
/// transaction, where that generation is out of reach, so the engine carries its claim to every node it runs
/// (<see cref="Claim"/>) and the staging reads it back (<see cref="ClaimedFor"/>) to take the same lock. It flows with
/// the async call, so two walks of one run on one host — an overtaken one and the revived one — each see their own. A
/// turn driven outside a walk carries no claim, and there is nothing to fence.</para>
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

    /// <summary>Share-lock the run row while it still stands at <paramref name="generation"/>; false once a Continue has moved it. Held to the end of the caller's transaction, so a Continue's bump waits for it.</summary>
    public static async Task<bool> TryLockAsync(CodeSpaceDbContext db, Guid runId, int generation, CancellationToken cancellationToken) =>
        (await db.Database.SqlQuery<int>($"SELECT 1 AS \"Value\" FROM workflow_run WHERE id = {runId} AND generation = {generation} FOR SHARE").ToListAsync(cancellationToken).ConfigureAwait(false)).Count > 0;

    private sealed record WalkClaim(Guid RunId, int Generation);

    private sealed class Restore : IDisposable
    {
        private readonly WalkClaim? _replaced;

        public Restore(WalkClaim? replaced) { _replaced = replaced; }

        public void Dispose() => Current.Value = _replaced;
    }
}
