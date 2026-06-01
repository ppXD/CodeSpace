using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// Provider-neutral result of submitting a review back to a PR/MR. <see cref="ExternalId"/> is the
/// provider's id for the created review/note when there is a single one (e.g. a GitHub review, or a
/// GitLab note); it is null when the action has no single object (e.g. a bare GitLab approve).
/// </summary>
public sealed record RemotePullRequestReview
{
    /// <summary>The verdict that was submitted (echoed for the node output / display mirror).</summary>
    public required PullRequestReviewVerdict Verdict { get; init; }

    public string? ExternalId { get; init; }

    public string? WebUrl { get; init; }
}
