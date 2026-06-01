using CodeSpace.Messages.Enums;
using Octokit;

namespace CodeSpace.Core.Services.Providers.GitHub;

/// <summary>
/// Pure translation of the provider-neutral <see cref="PullRequestReviewVerdict"/> to GitHub's
/// native review event. GitHub has a first-class review verdict, so the mapping is 1:1 — isolated
/// here as a pure function so it's unit-tested independently of the (untestable) Octokit HTTP call.
/// </summary>
internal static class GitHubReviewMapping
{
    public static PullRequestReviewEvent ToEvent(PullRequestReviewVerdict verdict) => verdict switch
    {
        PullRequestReviewVerdict.Approve => PullRequestReviewEvent.Approve,
        PullRequestReviewVerdict.RequestChanges => PullRequestReviewEvent.RequestChanges,
        PullRequestReviewVerdict.Comment => PullRequestReviewEvent.Comment,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unknown review verdict"),
    };
}
