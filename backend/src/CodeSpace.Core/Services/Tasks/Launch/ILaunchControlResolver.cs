using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Launch;

/// <summary>
/// What a resolved route does with each operator control whose effect depends on the route. ONE method for both callers:
/// the launch refuses on a Refused disposition and applies the model clamp, and the route preview reports the same
/// dispositions and the same acceptance verdict — so the preview can never state what the launch would not do.
/// </summary>
public interface ILaunchControlResolver
{
    Task<LaunchControlResolution> ResolveAsync(TaskLaunchRequest request, TaskLaunchSeed seed, RoutePlan route, CancellationToken cancellationToken);
}
