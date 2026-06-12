namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// Provider-neutral request to CREATE an issue on a repository. Maps onto GitHub's
/// <c>NewIssue { Title, Body, Labels }</c> (Octokit, label list) and GitLab's
/// <c>IssueCreate { ProjectId, Title, Description, Labels }</c> (NGitLab, comma-joined label string).
/// The neutral <see cref="Labels"/> is a list precisely because the two SDKs disagree on shape.
/// </summary>
public sealed record CreateIssueInput
{
    public required string Title { get; init; }

    /// <summary>Optional markdown body (GitHub <c>Body</c> / GitLab <c>Description</c>). Provider default when null.</summary>
    public string? Body { get; init; }

    /// <summary>Optional label names to attach. Empty list = no labels.</summary>
    public IReadOnlyList<string> Labels { get; init; } = Array.Empty<string>();
}
