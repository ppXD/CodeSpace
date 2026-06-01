namespace CodeSpace.Messages.Enums;

/// <summary>
/// Provider-neutral verdict for a review submitted back to a pull/merge request. Each provider's
/// capability translates it to its own API:
///   • GitHub — a native review event (APPROVE / REQUEST_CHANGES / COMMENT).
///   • GitLab — no native "request changes" verdict, so it's mapped to approve / unapprove + note / note.
/// A new provider just maps these three; the enum is the stable contract the workflow node speaks.
/// </summary>
public enum PullRequestReviewVerdict
{
    /// <summary>Approve the PR/MR.</summary>
    Approve,

    /// <summary>Request changes — block the PR/MR (and surface the reason).</summary>
    RequestChanges,

    /// <summary>A neutral review comment — no approval state change.</summary>
    Comment
}
