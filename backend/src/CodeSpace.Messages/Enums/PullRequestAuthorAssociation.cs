namespace CodeSpace.Messages.Enums;

/// <summary>
/// How a pull request's AUTHOR stands with the repository it targets — the one fact that tells a member's change
/// from an outsider's. Normalised across providers: GitHub reports it on the payload (<c>author_association</c>);
/// GitLab does not, so it is the author's project access level, looked up when a trigger needs it.
/// </summary>
public enum PullRequestAuthorAssociation
{
    /// <summary>Not reported, or the lookup failed. Never trusted as a member.</summary>
    Unknown,

    /// <summary>No standing: GitHub <c>NONE</c> / <c>MANNEQUIN</c>, or a GitLab user with no role or a role below Developer.</summary>
    None,

    /// <summary>Has had changes merged before but holds no role: GitHub <c>CONTRIBUTOR</c> / <c>FIRST_TIME_CONTRIBUTOR</c> / <c>FIRST_TIMER</c>.</summary>
    Contributor,

    /// <summary>Holds a role on the repository or its owner: GitHub <c>OWNER</c> / <c>MEMBER</c> / <c>COLLABORATOR</c>; GitLab Developer or above.</summary>
    Member
}
