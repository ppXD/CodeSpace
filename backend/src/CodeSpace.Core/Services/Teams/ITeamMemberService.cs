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
}
