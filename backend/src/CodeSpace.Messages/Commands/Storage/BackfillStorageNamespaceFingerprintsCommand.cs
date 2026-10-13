using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Storage;

/// <summary>
/// Put back, on every storage profile revision, the namespace fingerprint the previous build stores — the hash of the
/// configuration as written — where a build that stored the physical namespace identity wrote another value (see
/// <c>IStorageNamespaceFingerprintBackfillService</c>). Idempotent and bounded: a restored revision is no longer a
/// candidate, so the backlog only shrinks. NOT tenant-scoped: a system-wide repair that runs without an actor context
/// (mirrors <c>BackfillPackCloneUrlsCommand</c>).
///
/// <para>NOT transactional (<see cref="INonTransactionalCommand"/>): the service opens the one transaction its trigger
/// switch has to live inside, and a command transaction around it would refuse a second.</para>
/// </summary>
public sealed record BackfillStorageNamespaceFingerprintsCommand : ICommand<int>, INonTransactionalCommand
{
    /// <summary>Revisions restored per tick — bounds how long one pass holds the revision table's write lock.</summary>
    public int BatchSize { get; init; } = 200;
}
