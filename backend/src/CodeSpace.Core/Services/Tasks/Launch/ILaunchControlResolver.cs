using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Launch;

/// <summary>
/// What a resolved route does with each operator control whose effect depends on the route. ONE method for both callers:
/// the launch refuses on a Refused disposition and applies the model clamp, and the route preview reports the same
/// dispositions — so the preview can never state a disposition the launch would not reach.
/// </summary>
public interface ILaunchControlResolver
{
    Task<LaunchControlResolution> ResolveAsync(TaskLaunchRequest request, RoutePlan route, CancellationToken cancellationToken);
}

/// <summary>The dispositions of one launch's route-dependent controls, whether its route grades an operator acceptance floor, and the pooled row the single agent runs on when its pinned model fell outside the allowed pool (null when nothing was clamped).</summary>
public sealed record LaunchControlResolution(IReadOnlyList<LaunchControlDisposition> Dispositions, bool GradesOperatorFloor, ModelDispatchRef? ModelClamp);
