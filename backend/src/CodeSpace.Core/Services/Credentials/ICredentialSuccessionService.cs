using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// Moves what a person bound through a credential they disconnected onto the credential they connect next on the same
/// provider instance. Reconnecting mints a new credential row; without this, every repository and group hook bound
/// through the old one kept pointing at a revoked row whose token material is gone.
/// </summary>
public interface ICredentialSuccessionService
{
    /// <summary>
    /// Re-point every live repository and in-service group hook bound through one of <paramref name="successor"/>'s
    /// owner's revoked credentials on the same provider instance and team at <paramref name="successor"/>, lifting the
    /// "needs a new credential" error the disconnect set and reviving the hooks its failures buried. A credential with no
    /// owner (team-service) succeeds nobody. Tracked changes only — the caller's unit of work saves them with the
    /// successor, and the revived hooks are dispatched after it commits.
    /// </summary>
    Task CarryForwardAsync(Credential successor, CancellationToken cancellationToken);

    /// <summary>
    /// Apply the same rule to bindings a connect path left behind — rows stranded before connects carried them forward,
    /// or by an older pod during a rolling deploy: up to <paramref name="batchSize"/> revoked credentials that still have
    /// live bindings AND an owner's reconnect to follow, each saved on its own. Returns how many were repaired.
    /// </summary>
    Task<int> RepairStrandedAsync(int batchSize, CancellationToken cancellationToken);
}
