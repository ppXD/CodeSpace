namespace CodeSpace.Messages.Enums;

/// <summary>
/// Who a credential belongs to. The generic governance marker behind "team service credentials":
/// it's provider-agnostic — every provider's person-independent identity (GitLab group / project
/// access token, a GitHub App installation, …) maps to <see cref="TeamService"/>, while an
/// individual's OAuth / PAT is <see cref="Personal"/>. The binding flow prefers TeamService so a
/// repo's connection doesn't hinge on one person.
/// </summary>
public enum CredentialOwnership
{
    /// <summary>Tied to a single user (their OAuth / PAT). Vanishes if that user leaves or revokes.</summary>
    Personal,

    /// <summary>Owned by the team, not a person (group/project token, app installation). Admin-managed,
    /// survives membership changes. Has no <c>OwnerUserId</c>.</summary>
    TeamService,
}
