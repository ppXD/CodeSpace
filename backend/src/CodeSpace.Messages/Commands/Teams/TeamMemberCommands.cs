using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Teams;

/// <summary>Move someone between roles. Clamped both ways — the actor may not touch, nor grant, above their own rank.</summary>
public sealed record ChangeTeamMemberRoleCommand : ICommand<Unit>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.MembersManage;

    /// <summary>Bound from the route (Rule 17), never the body.</summary>
    public Guid UserId { get; init; }

    public required TeamRole Role { get; init; }
}

public sealed record RemoveTeamMemberCommand : ICommand<Unit>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.MembersManage;

    public Guid UserId { get; init; }
}

/// <summary>
/// The caller removing their OWN membership. Deliberately not gated on members.manage: a Viewer who
/// cannot leave a team they were added to has been locked in, which is not what the read-only role
/// is for.
/// </summary>
public sealed record LeaveTeamCommand : ICommand<Unit>, IRequireTeamMembership;

/// <summary>
/// Hand the team over. The only Owner-tier write there is — an Owner is the one thing an Admin must
/// not be able to install, including themselves.
/// </summary>
public sealed record TransferTeamOwnershipCommand : ICommand<Unit>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.TeamManage;

    public required Guid ToUserId { get; init; }
}
