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
