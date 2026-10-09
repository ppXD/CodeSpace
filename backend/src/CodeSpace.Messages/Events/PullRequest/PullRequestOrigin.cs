using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Events.PullRequest;

/// <summary>
/// Who wrote a pull request and where its head lives — what a trigger needs to tell a member's change from an
/// outsider's. A fork's head branch can carry the same name as the base branch, so without these a workflow sees
/// <c>sourceBranch: main</c> and cannot know the code came from someone else's repository.
/// </summary>
public sealed record PullRequestOrigin
{
    /// <summary>The PR author's provider user id — the user <see cref="AuthorAssociation"/> describes. Not the actor of this delivery: a maintainer reopening an outsider's PR is not its author.</summary>
    public string? AuthorExternalId { get; init; }

    public PullRequestAuthorAssociation AuthorAssociation { get; init; } = PullRequestAuthorAssociation.Unknown;

    /// <summary>The head branch lives in another repository than the one the PR targets. False when the payload does not say.</summary>
    public bool IsFork { get; init; }

    /// <summary>The head repository's full path (<c>evil/repo</c>). Null when the provider omits it — a deleted fork, or an older payload.</summary>
    public string? HeadRepositoryFullName { get; init; }

    /// <summary>The target repository's visibility as the provider reported it on this delivery. Null when the payload does not carry it.</summary>
    public RepositoryVisibility? RepositoryVisibility { get; init; }

    /// <summary>
    /// Who pushed the commits this event carries, by provider user id — GitHub's <c>sender</c> on a synchronize, GitLab's
    /// <c>user</c> on an update that moved the head. Null on an open or reopen, which pushes nothing: whoever reopened a PR
    /// did not write its code.
    /// </summary>
    public string? PusherExternalId { get; init; }

    /// <summary>How <see cref="PusherExternalId"/> stands with the repository. Neither provider says on the payload, so it is Unknown until looked up — and only when a trigger has to decide on it.</summary>
    public PullRequestAuthorAssociation PusherAssociation { get; init; } = PullRequestAuthorAssociation.Unknown;
}

/// <summary>
/// A pull-request event an outsider can cause — an open, a reopen, a push of new commits — and so the one a trigger's
/// authors filter and debounce apply to. A merge needs a member's hand, so a merged event is not one.
/// </summary>
public interface IPullRequestOriginEvent
{
    Guid RepositoryId { get; }

    int Number { get; }

    /// <summary>The commit the PR's head is at in this event — what one run of a trigger is FOR. Null when the payload does not say.</summary>
    string? HeadSha { get; }

    /// <summary>Settable because GitLab names the author but not their standing: dispatch completes the association before deciding on it.</summary>
    PullRequestOrigin Origin { get; set; }
}
