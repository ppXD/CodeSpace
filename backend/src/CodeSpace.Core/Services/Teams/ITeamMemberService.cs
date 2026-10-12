using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Teams;

/// <summary>Who is in the current team and what they may do. Team scope comes from <c>ICurrentTeam</c>, never a body.</summary>
public interface ITeamMemberService
{
    Task ChangeRoleAsync(Guid userId, TeamRole role, CancellationToken cancellationToken);
    Task RemoveAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The caller removing their own membership. Needs no permission; still cannot strand the team.</summary>
    Task LeaveAsync(CancellationToken cancellationToken);

    Task TransferOwnershipAsync(Guid toUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Throws unless <paramref name="userId"/> has standing in the current team — a member, or an instance Admin, who
    /// stands in every team as an Owner — and the caller may act on them: themselves, or someone below them. A user
    /// without standing is refused exactly like an id that names nobody. Holds their standing as
    /// <see cref="HoldMembershipAsync"/> does.
    /// </summary>
    Task EnsureMayActOnAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether <paramref name="userId"/> has standing in the live team <paramref name="teamId"/> — a membership row there,
    /// or the instance Admin role — and, for a member, keeps them one until the caller's transaction ends: their leaving or
    /// removal waits for it to commit, then retires what it wrote in their name. For a write in someone's name that must
    /// not land after they have gone, such as a token they own; the check before a slow provider call is not enough on its
    /// own. Outside a transaction it holds nothing.
    /// </summary>
    Task<bool> HoldMembershipAsync(Guid teamId, Guid userId, CancellationToken cancellationToken);
}
