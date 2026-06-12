using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// Provider-neutral issue shape returned from a create (later: get) operation. GitHub "issue" and
/// GitLab "issue" both normalise here. <see cref="Number"/> is the per-repo human number
/// (GitHub #N / GitLab #N iid), distinct from the provider's global <see cref="ExternalId"/>.
/// Mirrors <see cref="RemotePullRequest"/>'s field conventions (colour-aware labels, nullable author).
/// </summary>
public sealed record RemoteIssue
{
    /// <summary>Stable provider-side identifier (Octokit Id / NGitLab global Id). Used as React key + correlation FK.</summary>
    public required string ExternalId { get; init; }

    /// <summary>Human-facing per-repo number — `#42` on both GitHub and GitLab (GitLab iid).</summary>
    public required int Number { get; init; }

    public required string Title { get; init; }
    public required IssueState State { get; init; }

    public string? Body { get; init; }
    public string? AuthorLogin { get; init; }

    /// <summary>Labels with provider-supplied colours where available (GitLab issue lists return names only → null colour).</summary>
    public IReadOnlyList<LabelRef> Labels { get; init; } = Array.Empty<LabelRef>();

    public required DateTimeOffset CreatedDate { get; init; }
    public required string WebUrl { get; init; }
}
