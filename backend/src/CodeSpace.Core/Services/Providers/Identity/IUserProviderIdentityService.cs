using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Providers.Identity;

/// <summary>
/// Manages the CURRENT user's own provider identities (Model B linking). All operations are
/// scoped to the authenticated caller — you link / list / unlink only your own identities.
/// The token is validated against the provider before anything is persisted (reusing the
/// generic <see cref="Capabilities.ICredentialProbeCapability"/> whoami), so a bad token never
/// creates a row.
/// </summary>
public interface IUserProviderIdentityService
{
    /// <summary>
    /// Link the caller's identity on a provider instance via a personal access token. Probes the
    /// token (whoami) first; on success stores a Personal credential + the identity, replacing any
    /// existing live link for the same (user, instance). Throws if the token is invalid.
    /// </summary>
    Task<UserProviderIdentitySummary> LinkByPatAsync(Guid providerInstanceId, string accessToken, CancellationToken cancellationToken);

    /// <summary>The caller's live linked identities (one per provider instance), newest first.</summary>
    Task<IReadOnlyList<UserProviderIdentitySummary>> ListMineAsync(CancellationToken cancellationToken);

    /// <summary>Unlink one of the caller's identities (soft-delete the identity + its credential). No-op if already gone.</summary>
    Task UnlinkAsync(Guid identityId, CancellationToken cancellationToken);

    /// <summary>
    /// Ensure <paramref name="userId"/> has an act-as-user identity on <paramref name="instance"/>
    /// backed by an ALREADY-created credential (the OAuth-complete path — the credential is built by
    /// the OAuth flow, not here). Probes the credential for the provider user, then either re-points
    /// the user's existing live identity to this credential or creates a new one. NON-destructive: it
    /// never revokes another credential (unlike the PAT replace-in-place path). The caller persists —
    /// no SaveChanges here. Throws if the probe fails (caller decides whether that's fatal).
    /// </summary>
    Task EnsureIdentityForCredentialAsync(ProviderInstance instance, Credential credential, Guid userId, CancellationToken cancellationToken);
}
