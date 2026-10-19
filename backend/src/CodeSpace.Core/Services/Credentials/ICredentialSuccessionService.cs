using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Credentials;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// Moves what a person bound through a credential that died — one they disconnected, or one its provider refused for good —
/// onto the credential they connect next on the same provider instance. Reconnecting mints a new credential row; without
/// this, every repository and group hook bound through the old one kept pointing at a row whose token material is gone,
/// or that its provider no longer accepts.
/// </summary>
public interface ICredentialSuccessionService
{
    /// <summary>
    /// Re-point every live repository and in-service group hook bound through one of <paramref name="successor"/>'s
    /// owner's dead credentials (revoked, or refused by their provider) on the same provider instance — or another connection to the same
    /// host — and team at <paramref name="successor"/>, lifting the "needs a new credential" error a disconnect set and reviving the hooks its
    /// failures buried. A repository on another connection moves to the successor's connection with it, unless that is refused
    /// (<see cref="Services.RepositoryBinding.IRepositoryRehoming.RefusalsAsync"/>), when it stays where it was and the connect goes on; a group hook only follows to a credential of
    /// its own connection. A credential with no
    /// owner (team-service) succeeds nobody. Tracked changes only — the caller's unit of work saves them with the
    /// successor, and the revived hooks are dispatched after it commits. A dead credential that a re-authorization has
    /// revived since it was read keeps its bindings, and one revived after that waits for the unit of work to commit: the
    /// credentials are held until it ends.
    /// </summary>
    Task CarryForwardAsync(Credential successor, CancellationToken cancellationToken);

    /// <summary>
    /// Apply the same rule to bindings a connect path left behind — rows stranded before connects carried them forward,
    /// or by an older pod during a rolling deploy: up to <paramref name="batchSize"/> dead credentials that still have
    /// live bindings AND an owner's reconnect to follow, each saved on its own. Returns how many were repaired. A
    /// credential that fails is skipped and logged, and the pass goes on to the ones after it — however many fail, they
    /// cannot crowd a repairable credential out of the pass — and one that has failed several passes in a row is set aside
    /// for a while rather than attempted, and warned about, every tick. A pass is bounded in attempts as well as in
    /// repairs (<see cref="SweepPass{TCandidate}"/>). Each credential is repaired under its refresh lock and judged dead
    /// again once it is held: one a re-authorization has revived since the page was read is no longer a candidate and keeps
    /// its bindings, and one a refresh or a re-authorization is writing is skipped, not waited for, and met on the next pass. The move is made
    /// holding the credential's row, so a verdict the health probe records meanwhile waits for it. The owner's newest credential on the dead one's own connection
    /// is its successor before one on another connection to the same host, and a credential holding only a group hook has no successor on another connection. A successor that takes nothing because every repository was refused (<see cref="StrandedBindingsRefusedException"/>) is a failure of that credential: logged with the reasons and set aside after a few passes, so it never holds a place a repairable one could have.
    /// </summary>
    Task<int> RepairStrandedAsync(int batchSize, CancellationToken cancellationToken);

    /// <summary>
    /// Where a person's own act of ending a connection sends what is bound through it when they name no destination — the
    /// answer reconnecting would have given: when <paramref name="from"/> is still connected, is a person's own credential,
    /// <paramref name="callerId"/> is that person, and they hold exactly one other live Personal credential on the same
    /// provider instance, that one — unless the move would refuse it (<see cref="RefusalAsync"/>). Never a credential of another
    /// connection to the same host: a repository changing connection is a bigger step than holding a credential elsewhere asks for, so it takes a named target. Null in every other case:
    /// someone else acting for them (an Admin cutting off a teammate's token must not have the repository carried onto the
    /// teammate's other grant, with no way to say otherwise), a credential already disconnected (a retry is not a new
    /// choice), a team-service token (two team tokens are rarely interchangeable), none to follow, or two or more to choose
    /// between — all of them the person's to decide by naming a target. Never fails: it is a default a caller chose for
    /// itself.
    /// </summary>
    Task<Credential?> FindOnlyOtherConnectionAsync(Credential from, Guid callerId, CancellationToken cancellationToken);

    /// <summary>
    /// Why <paramref name="to"/> may not take over <paramref name="from"/>'s bindings, worded for the person who asked — or
    /// null when it may. Every move obeys the same floor, whoever asks and whatever else they hold: another credential, of
    /// the same team and provider instance — or of another connection to the same host (<c>ProviderInstanceHost</c>), where
    /// each repository must also be one the new connection can take (not already bound there, its scopes covering the hook) — live, and — when it is a
    /// person's — held by someone who still stands in the team, because the departed-token sweep would retire it and strand what it just took. Who is ALLOWED to ask for
    /// which target is the caller's rule, not this one.
    /// </summary>
    Task<string?> RefusalAsync(Credential from, Credential to, CancellationToken cancellationToken);

    /// <summary>
    /// Re-point every live repository and in-service group hook bound through <paramref name="from"/> at
    /// <paramref name="to"/>, as the carry-forward does: a repository's disconnect Error is lifted, a hook its failures buried
    /// is revived and handed to the registrar once the caller's unit of work commits. A repository that changes connection stages the
    /// hooks the new connection needs and the old ones are retired, once that unit of work commits. Throws
    /// <see cref="InvalidOperationException"/> with the <see cref="RefusalAsync"/> reason when the move is not allowed — everything moves or nothing does.
    /// Tracked changes only. Returns what moved.
    /// </summary>
    Task<BindingsMoved> MoveBindingsAsync(Credential from, Credential to, CancellationToken cancellationToken);
}
