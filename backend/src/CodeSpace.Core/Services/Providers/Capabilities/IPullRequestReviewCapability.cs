using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;

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
    /// Submit <paramref name="verdict"/> (with an optional markdown <paramref name="body"/>) to PR/MR
    /// <paramref name="number"/>. The provider maps the neutral verdict to its own API. Throws when the
    /// bound credential lacks the required scope (mapped to 422 with the missing-scope hint).
    /// </summary>
    Task<RemotePullRequestReview> SubmitReviewAsync(ProviderContext context, RemoteRepository repository, int number, PullRequestReviewVerdict verdict, string? body, CancellationToken cancellationToken);
}
