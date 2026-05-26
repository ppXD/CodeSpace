using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Providers.Capabilities;

/// <summary>
/// Write operations on pull/merge requests. Split out from <see cref="IPullRequestCatalogCapability"/>
/// (which is read-only) so credentials scoped for read-only PR access can still do that
/// while only WRITE-capable credentials enable workflow steps like "post review comment".
/// Rule 7 (ISP) — keep the interface narrow so providers can degrade gracefully.
/// </summary>
public interface IPullRequestCommentCapability : IProviderCapability
{
    /// <summary>
    /// Post a top-level conversation comment on a PR/MR. Markdown body. The returned remote
    /// id is the provider's stable comment id — for future "update existing review comment"
    /// flows. Posting is not idempotent — every call creates a new comment.
    /// </summary>
    Task<RemotePullRequestComment> PostCommentAsync(ProviderContext context, RemoteRepository repository, int number, string body, CancellationToken cancellationToken);
}
