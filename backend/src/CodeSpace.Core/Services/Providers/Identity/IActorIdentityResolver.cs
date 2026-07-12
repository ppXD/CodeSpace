using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Providers;

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

    /// <summary>
    /// Every teammate who can be an AUTHOR on the repository — those with a live, usable identity on the
    /// repo's provider instance. Uses the SAME predicate as <see cref="ResolveAsync"/>, so an offered
    /// candidate is guaranteed to resolve (never throws ActorIdentityRequiredException) at write time.
    /// Empty when none qualify or the repo is not the team's. Bots are excluded (they can't link one).
    /// </summary>
    Task<IReadOnlyList<ActAsCandidateSummary>> ListCandidatesAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken);
}
