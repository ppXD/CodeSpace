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
}
