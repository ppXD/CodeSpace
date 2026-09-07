using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Mediation;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Messages.Commands.Tasks;

/// <summary>Resolve and persist a routing decision for the exact launch input. Opens no session, stages no run and grants no execution permission.</summary>
public sealed record PreviewTaskRouteCommand : TaskLaunchInput, ICommand<TaskRoutePreviewResult>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.RunsLaunch;
}
