using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Providers.GitLab;

/// <summary>The GitLab approval-state change a verdict drives, alongside its note.</summary>
internal enum GitLabApprovalAction
{
    /// <summary>No approval-state change — just the note (a neutral Comment).</summary>
    None,

    /// <summary>Natively approve the MR (NGitLab <c>IMergeRequestClient.Approve</c>).</summary>
    Approve,

    /// <summary>Retract any existing approval. GitLab has no "request changes" verdict, so request_changes
    /// maps to unapprove (<c>POST /unapprove</c>) — the truest equivalent of withdrawing approval.</summary>
    Unapprove
}

/// <summary>
/// What to do when an <c>approve</c> verdict meets the MR's CURRENT approval state. GitLab's approve
/// endpoint is NOT idempotent — re-approving an already-approved MR returns a bare 401, the same code
/// it returns when the actor genuinely can't approve (author of the MR, or role below Developer). So
/// we read the state first and disambiguate instead of firing blind and surfacing a confusing 401.
/// </summary>
internal enum GitLabApproveDecision
{
    /// <summary>Not yet approved by this actor, and they're eligible → call approve.</summary>
    Approve,

    /// <summary>This actor already approved → idempotent no-op (re-running the node must not error).</summary>
    AlreadyApproved,

    /// <summary>This actor can't approve at all (they authored the MR, or their role is too low) →
    /// surface a clear failure rather than firing an approve that GitLab would reject with a bare 401.</summary>
    CannotApprove
}

/// <summary>
/// Pure translation of the provider-neutral <see cref="PullRequestReviewVerdict"/> to GitLab's two
/// review primitives: an approval-state change (<see cref="ActionFor"/>) and a labeled MR note
/// (<see cref="NoteFor"/>). GitLab has no single native "review verdict", so a verdict becomes
/// {approve | unapprove | nothing} PLUS a note carrying the reasoning (a native approve carries no
/// text). Isolated as pure functions so they're unit-tested independently of the thin NGitLab /
/// raw-HTTP calls that apply them.
/// </summary>
internal static class GitLabReviewPlan
{
    /// <summary>The approval-state change a verdict drives on GitLab.</summary>
    public static GitLabApprovalAction ActionFor(PullRequestReviewVerdict verdict) => verdict switch
    {
        PullRequestReviewVerdict.Approve => GitLabApprovalAction.Approve,
        PullRequestReviewVerdict.RequestChanges => GitLabApprovalAction.Unapprove,
        PullRequestReviewVerdict.Comment => GitLabApprovalAction.None,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unknown review verdict"),
    };

    /// <summary>Decide an <c>approve</c> against the MR's current approval state (read just before).
    /// Already-approved short-circuits to a no-op so re-running the node is idempotent; ineligibility
    /// (author / low role) is caught here so it surfaces as a clear failure, not a bare 401.</summary>
    public static GitLabApproveDecision DecideApprove(bool userHasApproved, bool userCanApprove) =>
        userHasApproved ? GitLabApproveDecision.AlreadyApproved
        : userCanApprove ? GitLabApproveDecision.Approve
        : GitLabApproveDecision.CannotApprove;

    /// <summary>The labeled note posted alongside the state change — it carries the reasoning text a
    /// native approve has no field for.</summary>
    public static string NoteFor(PullRequestReviewVerdict verdict, string? body) => verdict switch
    {
        PullRequestReviewVerdict.Approve => Compose("✅ Approved", body),
        PullRequestReviewVerdict.RequestChanges => Compose("🛑 Changes requested", body),
        PullRequestReviewVerdict.Comment => body ?? "",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unknown review verdict"),
    };

    private static string Compose(string header, string? body) =>
        string.IsNullOrWhiteSpace(body) ? $"**{header}**" : $"**{header}**\n\n{body}";
}
