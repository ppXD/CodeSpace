using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.Providers.Identity;

/// <summary>
/// The generic "act AS the human" enforcement seam (Model B). EVERY attributable write — PR review
/// today, future merge / issue / comment — funnels through <see cref="RequireAsync"/>: resolve the
/// caller's linked identity for the instance, or throw <see cref="Messages.Exceptions.ActorIdentityRequiredException"/>.
/// One method, one throw, mapped to one HTTP signal — so a new act-as-user operation inherits the
/// whole link-or-prompt flow with a single call and zero per-feature code.
/// </summary>
public interface IActorCredentialProvider
{
    /// <summary>The actor's usable credential for the instance, or throw if they haven't linked one
    /// (or it's no longer usable). "Usable" is whatever <see cref="IActorIdentityResolver"/> deems
    /// live (active, non-deleted credential).</summary>
    Task<Credential> RequireAsync(Guid actorUserId, ProviderInstance instance, CancellationToken cancellationToken);
}
