using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Seal the clone URL of every pack row that still holds a credential in its <c>url</c> column: the rows imported
/// before a pack kept its credential sealed, and any an older pod writes during a rolling deploy. Each row gets the
/// credential-free URL plus the sealed original, so it keeps syncing on every pod that reads the seal (a pod that
/// predates it cannot — see <c>IPackCloneUrlBackfillService</c>). Idempotent and bounded: a sealed row no longer
/// carries a credential, so it leaves the candidate set and the backlog only shrinks. NOT tenant-scoped: a
/// system-wide repair that runs without an actor context (mirrors <c>BackfillRunScorecardsCommand</c>).
///
/// <para>NOT transactional (<see cref="INonTransactionalCommand"/>): each row is sealed by its own conditional UPDATE,
/// and a row that fails stays a candidate while the pass carries on. One transaction around the pass would let one
/// failing row undo every other row's seal.</para>
/// </summary>
public sealed record BackfillPackCloneUrlsCommand : ICommand<int>, INonTransactionalCommand
{
    /// <summary>Packs sealed per tick — bounds each pass.</summary>
    public int BatchSize { get; init; } = 50;
}
