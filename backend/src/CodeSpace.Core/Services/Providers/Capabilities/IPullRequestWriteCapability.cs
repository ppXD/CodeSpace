using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Providers.Capabilities;

/// <summary>
/// OPEN a pull/merge request between two existing branches — the creation half of the write surface,
/// split from <see cref="IPullRequestReviewCapability"/> (verdict write-back) and
/// <see cref="IPullRequestCommentCapability"/> (a comment) because creating a request is a distinct,
/// higher-privilege action providers gate behind a write role. Rule 7 (ISP): a provider / credential
/// that can comment or review but not open still gets those capabilities; only open-capable ones enable
/// the git.open_pr node. A new provider implements just this interface — the registry resolves it by type.
///
/// <para>This opens the request between branches that ALREADY exist on the remote; it does not push code.</para>
/// </summary>
public interface IPullRequestWriteCapability : IProviderCapability
{
    /// <summary>
    /// Open a pull/merge request per <paramref name="input"/> (title + source/target branch + optional body /
    /// draft) and return the created request. The provider maps the neutral input onto its own API. Throws
    /// when the bound credential lacks the required scope (mapped to 422 with the missing-scope hint) or the
    /// branches / repo are invalid (mapped from the provider's 4xx).
    /// </summary>
    Task<RemotePullRequest> OpenPullRequestAsync(ProviderContext context, RemoteRepository repository, OpenPullRequestInput input, CancellationToken cancellationToken);

    /// <summary>
    /// MERGE the open pull/merge request <paramref name="number"/> per <paramref name="input"/> (merge method +
    /// optional commit title/message + delete-source-branch). The provider maps the neutral input onto its own
    /// API. Throws when the bound credential lacks the required scope (mapped to 422 with the missing-scope hint)
    /// or the request can't be merged (conflicts / not mergeable / already merged → mapped from the provider's 4xx).
    /// </summary>
    Task<RemotePullRequestMergeResult> MergePullRequestAsync(ProviderContext context, RemoteRepository repository, int number, MergePullRequestInput input, CancellationToken cancellationToken);
}
