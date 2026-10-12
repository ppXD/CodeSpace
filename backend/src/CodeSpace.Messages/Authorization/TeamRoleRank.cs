using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Authorization;

/// <summary>
/// Explicit authority ordering for <see cref="TeamRole"/>.
///
/// <para>This table exists because the enum's own member order is REVERSED — Owner=0, Admin=1,
/// Member=2, Viewer=3 — so <c>role &gt;= TeamRole.Member</c> reads as "at least Member" and means the
/// exact opposite. Every comparison goes through <see cref="Of"/>; nothing may compare
/// <see cref="TeamRole"/> values directly.</para>
///
/// <para>Ranks are spaced by 10 so a tier can be inserted between two existing ones without
/// renumbering the rest.</para>
/// </summary>
public static class TeamRoleRank
{
    public const int Owner = 40;
    public const int Admin = 30;
    public const int Member = 20;
    public const int Viewer = 10;

    public static int Of(TeamRole role) => role switch
    {
        TeamRole.Owner => Owner,
        TeamRole.Admin => Admin,
        TeamRole.Member => Member,
        TeamRole.Viewer => Viewer,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "unranked TeamRole — a new role must be given a rank here before it can be authorized")
    };

    /// <summary>
    /// Rank is a reach, not a threshold: an actor may act on anything strictly below them, and on something AT their
    /// own rank only when it is their own — themselves, or an invitation they sent. Never above, never across: two
    /// Admins who can each undo the other turn a disagreement into a race.
    /// </summary>
    public static bool Reaches(TeamRole actor, TeamRole subject, bool isOwn) => Of(subject) < Of(actor) || (Of(subject) == Of(actor) && isOwn);

    /// <summary>
    /// Revoking a pending invitation raises nobody — at worst its sender sends it again — so it reaches every invitation
    /// up to the actor's own rank, whoever sent it, and one the actor sent at any rank (sent as Owner, say, before a
    /// demotion). Narrower than that, an Owner invitation was in reach of its sender alone, and once the sender left or
    /// handed the team on, a leaked link to the top seat could not be withdrawn.
    /// </summary>
    public static bool MayRevokeInvitation(TeamRole actor, TeamRole invitationRole, bool isOwn) => Of(invitationRole) <= Of(actor) || isOwn;

    /// <summary>
    /// Regenerating hands the caller a working link to the role the invitation grants, so it is acting at that role and
    /// follows <see cref="Reaches"/> — except for an Owner, who may regenerate any: nobody ranks above an Owner to
    /// arbitrate, and an Owner could invite at Owner outright.
    /// </summary>
    public static bool MayRegenerateInvitation(TeamRole actor, TeamRole invitationRole, bool isOwn) => Of(actor) == Owner || Reaches(actor, invitationRole, isOwn);
}
