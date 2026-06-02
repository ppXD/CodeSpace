using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Repositories;

/// <summary>
/// Submit a review (Approve / RequestChanges / Comment) back to a PR/MR AS the calling user's own
/// linked identity (Model B). The handler always supplies the caller's id as the actor, so a caller
/// who hasn't linked an identity for the repo's provider instance gets <c>ActorIdentityRequiredException</c>
/// → 428 <c>actor_identity_required</c>, and the SPA prompts a link then retries. This is the
/// synchronous trigger that surfaces the actor-identity requirement to the frontend.
/// </summary>
public sealed record SubmitPullRequestReviewCommand : ICommand<RemotePullRequestReview>, IRequireRepositoryAccess
{
    /// <summary>Set by the controller from the route segment via `command with { RepositoryId = ... }`. Non-required so System.Text.Json doesn't 400-fail when the body omits it (URL is authoritative).</summary>
    public Guid RepositoryId { get; init; }

    /// <summary>The PR/MR number. Set by the controller from the route segment; non-required for the same reason as RepositoryId.</summary>
    public int Number { get; init; }

    public required PullRequestReviewVerdict Verdict { get; init; }

    /// <summary>Review body. Required for RequestChanges / Comment (the service enforces non-empty); may be null for a bare Approve.</summary>
    public string? Body { get; init; }
}
