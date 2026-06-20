using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Issues;

/// <summary>
/// Issue read + write operations against bound repositories. Each method does the full preflight (repo
/// lookup, credential null-check, scope check; writes add Write-role + Model-B actor attribution) then
/// invokes the provider's <c>IIssueCatalogCapability</c> (reads) or <c>IIssueWriteCapability</c> (writes).
/// Consumers (Mediator handlers, workflow nodes, future chat tool-calls, integration tests) don't see any
/// of that wiring — mirrors <c>IPullRequestService</c>.
/// </summary>
public interface IIssueService
{
    /// <summary>
    /// LIST issues filtered by state, one page at a time, via the provider's <c>IIssueCatalogCapability</c>.
    /// Read preflight (repo lookup, credential null-check, read-scope check) — no actor attribution.
    /// </summary>
    Task<IReadOnlyList<RemoteIssue>> ListAsync(Guid repositoryId, Guid teamId, IssueState? state, int page, int perPage, CancellationToken cancellationToken);

    /// <summary>Total open + closed issue counts for the repository — one provider round-trip. Same read preflight as <see cref="ListAsync"/>.</summary>
    Task<RemoteIssueCounts> GetCountsAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken);

    /// <summary>
    /// CREATE an issue via the provider's <c>IIssueWriteCapability</c>. Throws
    /// <see cref="InvalidOperationException"/> (400) for a missing repo / blank title, on insufficient write
    /// scope (422), or when the provider rejects the repo (mapped from its 4xx). <paramref name="actorUserId"/>
    /// opts into per-user attribution (Model B) exactly like <c>IPullRequestService.OpenPullRequestAsync</c>.
    /// </summary>
    Task<RemoteIssue> CreateAsync(Guid repositoryId, Guid teamId, CreateIssueInput input, Guid? actorUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Post a comment on issue <paramref name="number"/> via the provider's <c>IIssueWriteCapability</c>. Same
    /// preflight + Model B actor attribution as <see cref="CreateAsync"/>. Throws
    /// <see cref="InvalidOperationException"/> (400) for a missing repo / blank body, on insufficient write
    /// scope (422), or when the provider rejects it (mapped from its 4xx).
    /// </summary>
    Task<RemoteIssueComment> CommentAsync(Guid repositoryId, Guid teamId, int number, string body, Guid? actorUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Close issue <paramref name="number"/> via the provider's <c>IIssueWriteCapability</c>. Same preflight +
    /// Model B actor attribution as <see cref="CreateAsync"/>. Throws <see cref="InvalidOperationException"/>
    /// (400) for a missing repo, on insufficient write scope (422), or when the provider rejects it.
    /// </summary>
    Task<RemoteIssue> CloseAsync(Guid repositoryId, Guid teamId, int number, Guid? actorUserId, CancellationToken cancellationToken);
}
