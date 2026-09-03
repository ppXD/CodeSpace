using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Tasks.RoutePreview;
using CodeSpace.Messages.Commands.Tasks;
using CodeSpace.Messages.Tasks;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Tasks;

/// <summary>
/// Thin dispatcher (Rule 16): sources the team from <see cref="ICurrentTeam"/> and the actor from
/// <see cref="ICurrentUser"/> (NEVER the body — tenancy fail-closed), folds the command onto the SAME
/// <see cref="TaskLaunchRequest"/> the launch handler builds, and delegates. The caps + autonomy-ceiling
/// projection reuses <see cref="LaunchTaskCommandHandler.BuildCapsOverride"/> so a previewed bound is the bound
/// the launch would actually route under.
/// </summary>
public sealed class PreviewTaskRouteCommandHandler : IRequestHandler<PreviewTaskRouteCommand, TaskRoutePreviewResult>
{
    private readonly ITaskRoutePreviewService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public PreviewTaskRouteCommandHandler(ITaskRoutePreviewService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public Task<TaskRoutePreviewResult> Handle(PreviewTaskRouteCommand request, CancellationToken cancellationToken) =>
        _service.PreviewAsync(new TaskLaunchRequest
        {
            TeamId = _currentTeam.Id!.Value,
            ActorUserId = _currentUser.Id!.Value,
            SurfaceKind = request.SurfaceKind,
            TaskText = request.TaskText,
            RepositoryId = request.RepositoryId,
            RelatedRepositories = request.RelatedRepositories,
            BaseBranch = request.BaseBranch,
            RequestedEffort = request.Effort,
            RequestedRecipe = request.Recipe,
            CapsOverride = LaunchTaskCommandHandler.BuildCapsOverride(request.Caps, request.AutonomyCeiling),
        }, cancellationToken);
}
