using System.Text.Json;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Tasks;
using CodeSpace.Messages.Commands.Tasks;
using CodeSpace.Messages.Tasks;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Tasks;

/// <summary>
/// Thin dispatcher (Rule 16): sources the team from <see cref="ICurrentTeam"/> and the actor from
/// <see cref="ICurrentUser"/> (NEVER the request body — tenancy fail-closed), folds the command + the opaque
/// <see cref="LaunchContext.Raw"/> into a <see cref="TaskLaunchRequest"/>, and delegates to
/// <see cref="ITaskLaunchService"/>. No DbContext, no business logic — the team-scope repo check + seed/route/
/// project orchestration all live in the service. The handler NEVER deserializes <c>Raw</c>; it only carries it
/// through to the request's surface payload for the resolved seed provider to read (the no-hardcode keystone).
/// </summary>
public sealed class LaunchTaskCommandHandler : IRequestHandler<LaunchTaskCommand, LaunchTaskResult>
{
    private readonly ITaskLaunchService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public LaunchTaskCommandHandler(ITaskLaunchService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public Task<LaunchTaskResult> Handle(LaunchTaskCommand request, CancellationToken cancellationToken) =>
        _service.LaunchAsync(new TaskLaunchRequest
        {
            TeamId = _currentTeam.Id!.Value,
            ActorUserId = _currentUser.Id!.Value,
            SurfaceKind = request.SurfaceKind,
            TaskText = request.TaskText,
            ContinueSessionId = request.SessionId,
            RepositoryId = request.RepositoryId,
            RelatedRepositories = request.RelatedRepositories,
            BaseBranch = request.BaseBranch,
            RequestedEffort = request.Effort,
            Autonomy = request.Autonomy,
            Overrides = BuildOverrides(request),
            CapsOverride = BuildCapsOverride(request.Caps),
            AllowedModelIds = request.AllowedModelIds,
            SurfacePayload = BuildSurfacePayload(request),
        }, cancellationToken);

    /// <summary>Project the operator's safety-budget caps onto the router's <c>CapsOverride</c> seam. Null / empty ⇒ null (the launch service then leaves the router override unset — byte-identical to the preset-only path). A set-but-invalid cap fails LOUD (<see cref="ArgumentException"/>) here rather than silently degrading to "no cap" downstream. Internal (not private) so the mapping + empty-collapse + reject is unit-pinned directly (InternalsVisibleTo), like <c>TaskLaunchService.BuildAgentProfile</c>.</summary>
    internal static RouteCaps? BuildCapsOverride(TaskCapsOverride? caps)
    {
        if (caps is not { IsEmpty: false }) return null;

        caps.Validate();
        return caps.ToRouteCaps();
    }

    private static TaskExecutionOverrides BuildOverrides(LaunchTaskCommand request) => new()
    {
        Harness = request.Harness,
        Model = request.Model,
        AgentDefinitionId = request.AgentDefinitionId,
        RunnerKind = request.RunnerKind,
        ModelCredentialId = request.ModelCredentialId,
    };

    /// <summary>Carries the opaque <c>LaunchContext.Raw</c> through under its surface-kind key for the resolved seed provider to read — the handler never interprets it. Absent context ⇒ an empty payload.</summary>
    private static IReadOnlyDictionary<string, JsonElement> BuildSurfacePayload(LaunchTaskCommand request)
    {
        if (request.LaunchContext == null) return new Dictionary<string, JsonElement>();

        return new Dictionary<string, JsonElement> { [request.LaunchContext.SurfaceKind] = request.LaunchContext.Raw };
    }
}
