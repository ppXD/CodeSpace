using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.Providers.Identity;

/// <summary>
/// Resolves the actor identity a CodeSpace user has linked on a provider instance — the per-user
/// GitHub/GitLab account whose token an attributable write should authenticate as (Model B).
///
/// Generic + non-PR: every "act AS the human" operation (PR review write-back, future merge / issue)
/// funnels through this one seam. Returns null when the user has not linked a usable identity for the
/// instance; the CALLER (under the enforcement mode) decides whether to fall back to the repo's
/// connection credential (warn) or refuse (strict) — that policy is not this resolver's concern.
/// </summary>
public interface IActorIdentityResolver
{
    /// <summary>
    /// The user's live, usable identity on the instance, or null. "Usable" = the identity is not
    /// soft-deleted AND its linked credential is present and Active — so a revoked / expired / removed
    /// credential resolves to null exactly like "never linked".
    /// </summary>
    Task<UserProviderIdentity?> ResolveAsync(Guid actorUserId, Guid providerInstanceId, CancellationToken cancellationToken);
}
