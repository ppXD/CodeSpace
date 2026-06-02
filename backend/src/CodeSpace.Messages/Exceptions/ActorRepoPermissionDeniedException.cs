using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// Thrown at chat-respond time when the responder's linked identity EXISTS (and its credential is
/// Active) but can't act on the TARGET repository — they're not a member, their role is too low, or
/// they have no access. Membership/role is only knowable from the provider, so a pre-flight probe
/// discovers this BEFORE the interactive wait resolves.
///
/// Maps to 403 <c>actor_repo_permission_denied</c> so the client shows the reason on the card and
/// leaves it Open — no false "success", no background write that fails later. Distinct from
/// <see cref="ActorIdentityRequiredException"/> (428 — no identity at all, prompt a link).
/// </summary>
public sealed class ActorRepoPermissionDeniedException : Exception
{
    public ActorRepoPermissionDeniedException(ProviderKind providerKind, Guid providerInstanceId, string repositoryPath, string? reason)
        : base($"Actor lacks permission to act on {providerKind} repository '{repositoryPath}': {reason ?? "insufficient access"}")
    {
        ProviderKind = providerKind;
        ProviderInstanceId = providerInstanceId;
        RepositoryPath = repositoryPath;
        Reason = reason;
    }

    public ProviderKind ProviderKind { get; }

    public Guid ProviderInstanceId { get; }

    /// <summary>The repo's full path (e.g. "acme/web") — named in the end-user message.</summary>
    public string RepositoryPath { get; }

    /// <summary>End-user remediation reason from the provider probe; null falls back to a generic message.</summary>
    public string? Reason { get; }
}
