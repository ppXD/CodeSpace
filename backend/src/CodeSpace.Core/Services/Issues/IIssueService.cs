using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Issues;

/// <summary>
/// Issue write operations against bound repositories. Does the full preflight (repo lookup, credential
/// null-check, write-scope + Write-role check, Model-B actor attribution) then invokes the provider's
/// <c>IIssueWriteCapability</c>. Consumers (workflow nodes, future chat tool-calls, integration tests)
/// don't see any of that wiring — mirrors <c>IPullRequestService</c>.
/// </summary>
public interface IIssueService
{
    /// <summary>
    /// CREATE an issue via the provider's <c>IIssueWriteCapability</c>. Throws
    /// <see cref="InvalidOperationException"/> (400) for a missing repo / blank title, on insufficient write
    /// scope (422), or when the provider rejects the repo (mapped from its 4xx). <paramref name="actorUserId"/>
    /// opts into per-user attribution (Model B) exactly like <c>IPullRequestService.OpenPullRequestAsync</c>.
    /// </summary>
    Task<RemoteIssue> CreateAsync(Guid repositoryId, CreateIssueInput input, Guid? actorUserId, CancellationToken cancellationToken);
}
