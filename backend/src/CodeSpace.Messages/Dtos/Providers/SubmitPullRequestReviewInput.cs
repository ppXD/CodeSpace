using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// Provider-neutral request to submit a review verdict on a pull/merge request. Maps onto GitHub's
/// <c>PullRequestReviewCreate { Event, Body, CommitId }</c> and GitLab's approve (<c>sha</c>) / unapprove plus the review
/// note.
/// </summary>
public sealed record SubmitPullRequestReviewInput
{
    /// <summary>The verdict to submit.</summary>
    public required PullRequestReviewVerdict Verdict { get; init; }

    /// <summary>The review body — required for request-changes and comment, optional for approve.</summary>
    public string? Body { get; init; }

    /// <summary>
    /// Review only while the head is still this commit (GitHub review <c>commit_id</c>; GitLab approve <c>sha</c>): a head
    /// that moved fails the review instead of approving commits nobody reviewed. Null reviews whatever the head is.
    /// </summary>
    public string? ExpectedHeadSha { get; init; }
}
