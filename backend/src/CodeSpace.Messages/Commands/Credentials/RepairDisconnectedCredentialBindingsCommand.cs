using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Credentials;

/// <summary>
/// Move every repository and group hook still bound to a disconnected credential onto the credential that credential's
/// own owner connected afterwards on the same provider instance and team. The connect paths carry these bindings
/// forward themselves; this repairs the ones a connect path left behind anyway — rows stranded before the carry-forward
/// existed, a connect served by an older pod during a rolling deploy, and any later path that strands one. Idempotent
/// and bounded: a moved binding no longer points at a revoked credential, so the backlog only shrinks. NOT
/// tenant-scoped: a system-wide repair that runs without an actor context (mirrors <c>BackfillPackCloneUrlsCommand</c>).
///
/// <para>NOT transactional (<see cref="INonTransactionalCommand"/>): each credential's bindings are saved on their own,
/// and a credential that fails stays a candidate while the pass carries on. One transaction around the pass would let
/// one failing credential undo every other one's repair.</para>
/// </summary>
public sealed record RepairDisconnectedCredentialBindingsCommand : ICommand<int>, INonTransactionalCommand
{
    /// <summary>Disconnected credentials repaired per tick — bounds each pass.</summary>
    public int BatchSize { get; init; } = 50;
}
