using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.PullRequests;

/// <summary>
/// All PR/MR read operations against bound repositories — the live fetches that
/// power the Pulls tab. Each method does the full preflight (repo lookup,
/// credential null-check, scope check) and then invokes the provider's
/// IPullRequestCatalogCapability. Consumers (Mediator handlers, future chat
/// tool-calls, integration tests) don't see any of that wiring.
/// </summary>
public interface IPullRequestService
{
    Task<IReadOnlyList<RemotePullRequest>> ListAsync(Guid repositoryId, PullRequestState? state, int page, int perPage, CancellationToken cancellationToken);
    Task<RemotePullRequest> GetAsync(Guid repositoryId, int number, CancellationToken cancellationToken);
    Task<IReadOnlyList<RemotePullRequestCommit>> ListCommitsAsync(Guid repositoryId, int number, CancellationToken cancellationToken);
    Task<IReadOnlyList<RemotePullRequestFile>> ListFilesAsync(Guid repositoryId, int number, CancellationToken cancellationToken);
    Task<RemotePullRequestCounts> GetCountsAsync(Guid repositoryId, CancellationToken cancellationToken);
    Task<IReadOnlyList<RemotePullRequestCheck>> ListChecksAsync(Guid repositoryId, int number, CancellationToken cancellationToken);

    /// <summary>
    /// Post a top-level conversation comment on a PR/MR. Drives workflow nodes (e.g. the AI
    /// code-review template posts the model's verdict as a comment). Throws when the bound
    /// credential lacks write scope (mapped to 422 with the missing-scope hint).
    /// </summary>
    Task<RemotePullRequestComment> PostCommentAsync(Guid repositoryId, int number, string body, CancellationToken cancellationToken);

    /// <summary>
    /// Submit a review VERDICT (approve / request-changes / comment) back to a PR/MR — the write-back
    /// half of the review loop, via the provider's <c>IPullRequestReviewCapability</c>. The provider
    /// maps the neutral verdict to its own API. <paramref name="body"/> is required for
    /// <see cref="PullRequestReviewVerdict.Comment"/> and <see cref="PullRequestReviewVerdict.RequestChanges"/>
    /// (you can't comment / block with nothing to say) and optional for
    /// <see cref="PullRequestReviewVerdict.Approve"/>. Throws <see cref="InvalidOperationException"/> (400)
    /// for a missing repo / missing required body, or on insufficient write scope (422).
    ///
    /// <para><paramref name="actorUserId"/> opts into per-user attribution (Model B): when set, the
    /// write authenticates AS that user's own linked provider identity instead of the repo's
    /// connection credential — so the review shows up on the provider authored by the human. If they
    /// haven't linked one, <see cref="Messages.Exceptions.ActorIdentityRequiredException"/> is thrown
    /// (mapped to <c>actor_identity_required</c>). Null = use the connection credential (unchanged).</para>
    /// </summary>
    Task<RemotePullRequestReview> SubmitReviewAsync(Guid repositoryId, int number, PullRequestReviewVerdict verdict, string? body, Guid? actorUserId, CancellationToken cancellationToken);
}
