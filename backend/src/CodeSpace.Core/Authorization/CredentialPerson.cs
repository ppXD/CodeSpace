using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Authorization;

/// <summary>
/// The person a Personal credential belongs to, as the rules about acting on it see them: whether they are the one asking, and the role they
/// stand as in the team — null when they have none, because they left and the credential has not been retired yet. Built in one place, from the
/// credential's row and <see cref="TeamStanding.RolesActedOnAsync"/>, by everything that says beforehand what a person may do about a
/// credential (the provider remedy and the credential list), so the two cannot read the same row two ways. A credential with no person — a
/// team's own, or a token stored with no owner — has none, and the callers pass null.
/// </summary>
public sealed record CredentialPerson(bool IsCaller, TeamRole? Standing)
{
    /// <summary><paramref name="standing"/> is <see cref="TeamStanding.RolesActedOnAsync"/>'s answer for the credential's team; a person it does not name stands as no one.</summary>
    public static CredentialPerson? Of(CredentialOwnership ownership, Guid? ownerUserId, Guid? callerId, IReadOnlyDictionary<Guid, TeamRole> standing) =>
        ownership == CredentialOwnership.Personal && ownerUserId is { } owner ? new CredentialPerson(owner == callerId, standing.TryGetValue(owner, out var role) ? role : null) : null;
}
