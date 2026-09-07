using CodeSpace.Messages.Commands.Tasks;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.RoutePreview;

public interface ITaskRouteSnapshotService
{
    Task<TaskRoutePreviewResult> CreateAsync(TaskLaunchRequest request, TaskLaunchSeed seed, CancellationToken cancellationToken);
    Task<TaskRouteSnapshotDecision> ReadAsync(TaskLaunchRequest request, TaskLaunchSeed seed, CancellationToken cancellationToken);
    Task<LaunchTaskResult> ConsumeAsync(TaskRouteSnapshotConsumption consumption, CancellationToken cancellationToken);
}

public sealed record TaskRouteSnapshotDecision(RoutePlan Route, LaunchTaskResult? PreviousResult);

/// <summary>StageAsync must perform only local transactional staging; model and Git preparation happens before consumption takes its row lock.</summary>
public sealed record TaskRouteSnapshotConsumption(TaskLaunchRequest Request, TaskLaunchSeed Seed, Func<Task<LaunchTaskResult>> StageAsync);
