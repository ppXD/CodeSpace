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
}
