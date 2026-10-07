using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Providers.Capabilities;

/// <summary>
/// Submit a REVIEW VERDICT (approve / request-changes / comment) back to a pull/merge request — the
/// write-back half of the closed review loop. Split out from <see cref="IPullRequestCommentCapability"/>
/// (a plain comment) because a verdict can change approval STATE, which providers gate behind a wider
/// scope and model differently. Rule 7 (ISP): a credential / provider that can comment but not
/// approve still gets the comment capability; only verdict-capable ones enable git.pr_review.
/// A new provider implements just this interface — the registry resolves it by type, no wiring.
/// </summary>
public interface IPullRequestReviewCapability : IProviderCapability
{
    /// <summary>
    /// Submit <paramref name="input"/>'s verdict (with an optional markdown body) to PR/MR <paramref name="number"/>. The
    /// provider maps the neutral verdict to its own API, and a pinned head to its own precondition (GitHub's review
    /// <c>commit_id</c>, GitLab's approve <c>sha</c>). Throws when the bound credential lacks the required scope (mapped to
    /// 422 with the missing-scope hint).
    /// </summary>
    Task<RemotePullRequestReview> SubmitReviewAsync(ProviderContext context, RemoteRepository repository, int number, SubmitPullRequestReviewInput input, CancellationToken cancellationToken);
}
