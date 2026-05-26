using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// Provider-neutral pull/merge request shape. GitHub calls these "pull requests",
/// GitLab "merge requests" — we normalise to PR for the API surface. Anything
/// that doesn't translate cleanly (e.g. GitLab's draft prefix in the title, GitHub's
/// review state) is dropped here and surfaced lazily if the UI grows to need it.
/// </summary>
public sealed record RemotePullRequest
{
    /// <summary>Stable provider-side identifier (Octokit Id / NGitLab Id). Used as React key + future correlation FK.</summary>
    public required string ExternalId { get; init; }

    /// <summary>Human-facing number — `#42` on GitHub, `!42` on GitLab. Per-repo, not globally unique.</summary>
    public required int Number { get; init; }

    public required string Title { get; init; }
    public required PullRequestState State { get; init; }

    public required string SourceBranch { get; init; }
    public required string TargetBranch { get; init; }

    public string? AuthorLogin { get; init; }
    public string? AuthorAvatarUrl { get; init; }

    public required int CommentsCount { get; init; }

    public required DateTimeOffset CreatedDate { get; init; }
    public required DateTimeOffset UpdatedDate { get; init; }
    public DateTimeOffset? MergedDate { get; init; }
    public DateTimeOffset? ClosedDate { get; init; }

    public required string WebUrl { get; init; }

    /// <summary>
    /// Labels with provider-supplied colours so the UI can render the same coloured pills
    /// the operator sees on the provider natively. Empty list when the PR has no labels.
    /// See <see cref="LabelRef"/> for the colour-encoding contract.
    /// </summary>
    public IReadOnlyList<LabelRef> Labels { get; init; } = Array.Empty<LabelRef>();

    /// <summary>
    /// PR/MR description body. Markdown source as provided by the provider — GitHub returns
    /// CommonMark, GitLab returns GFM-ish markdown. The SPA renders it as plain pre-wrap text
    /// today (we deliberately don't ship a markdown renderer until there's a use-case beyond
    /// "read the description"). Only populated by GetPullRequestAsync — list calls leave it
    /// null to keep the list payload small.
    /// </summary>
    public string? Body { get; init; }

    /// <summary>Commit count for the PR head vs target. Available on the detail fetch.</summary>
    public int? CommitsCount { get; init; }

    /// <summary>Additions/deletions on the diff. Surfaced for the "+N -M" badge in detail view.</summary>
    public int? Additions { get; init; }
    public int? Deletions { get; init; }
    public int? ChangedFilesCount { get; init; }

    /// <summary>Logins of users assigned to the PR. Detail-only; empty array on list responses.</summary>
    public IReadOnlyList<string> Assignees { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Logins of explicitly-requested reviewers (GitHub). On GitLab, the reviewers
    /// field is supported too. Approver/approval state is intentionally NOT modelled
    /// yet — that's a follow-up when we wire review actions.
    /// </summary>
    public IReadOnlyList<string> RequestedReviewers { get; init; } = Array.Empty<string>();

    /// <summary>Title of the milestone the PR is associated with. Null when unassigned.</summary>
    public string? MilestoneTitle { get; init; }

    /// <summary>
    /// Number of completed task-list items (e.g. <c>- [x] done</c>) in the body. Pre-computed
    /// at the provider layer so the SPA can show a "N of M tasks" / "M tasks done" badge on
    /// the PR list without us shipping the full body string for every row. Null when there
    /// are no task-list items in the body (badge hidden) — distinct from 0 (badge shows
    /// "0 of M").
    /// </summary>
    public int? TasksCompleted { get; init; }

    /// <summary>Total task-list items in the body. Pairs with <see cref="TasksCompleted"/>.</summary>
    public int? TasksTotal { get; init; }
}
