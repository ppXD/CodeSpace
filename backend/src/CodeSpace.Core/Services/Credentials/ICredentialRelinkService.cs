using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Dtos.Credentials;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// What to do about a credential that is gone and still has things bound through it: see what is waiting, see where it
/// could go, and send all of it there in one action. The credential may be soft-deleted — a departed member's retired
/// token is the case this exists for — so every method loads it by (id, the header team) rather than through the
/// credential authorization marker, which refuses a deleted one.
/// </summary>
public interface ICredentialRelinkService
{
    /// <summary>Every repository (with its projects), group hook, act-as identity and workflow trigger that leans on the credential. A credential outside the caller's team reads as not found.</summary>
    Task<CredentialDependents> GetDependentsAsync(Guid credentialId, CancellationToken cancellationToken);

    /// <summary>The credentials the caller could move this one's bindings onto — their own live connections first, then the team's service tokens — each a candidate <see cref="RelinkAllAsync"/> would accept.</summary>
    Task<IReadOnlyList<RelinkSuggestion>> GetSuggestionsAsync(Guid credentialId, CancellationToken cancellationToken);

    /// <summary>
    /// Move every live repository and in-service group hook bound through a dead credential onto another credential, after
    /// the target has been asked to read each repository. Needs <c>repos.manage</c> (the command's marker) and
    /// <c>credentials.manage</c> (asserted here). Tracked changes only — the caller's unit of work saves them.
    /// </summary>
    Task<RelinkCredentialBindingsResult> RelinkAllAsync(Guid fromCredentialId, Guid toCredentialId, CancellationToken cancellationToken);
}
