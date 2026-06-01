using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Providers.GitLab;

/// <summary>
/// GitLab has no native single review verdict, so a verdict is posted as a LABELED merge-request note.
/// This pure function builds the note text from the neutral verdict + optional body — isolated so it's
/// unit-tested independently of the (untested, thin) NGitLab note call.
///
/// Native approve / unapprove via GitLab's approvals REST API is a future enhancement that slots in
/// behind the same <c>IPullRequestReviewCapability</c> seam (it needs a raw HTTP call — NGitLab v11
/// exposes approval STATE but no approve action), without touching the verdict contract or the node.
/// </summary>
internal static class GitLabReviewPlan
{
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
