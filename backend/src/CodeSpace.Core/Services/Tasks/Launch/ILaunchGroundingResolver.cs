using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Launch;

/// <summary>
/// The grounding a launch is primed with — shared by the launch and its route preview, because the route is classified
/// on it too: a follow-up turn has to reach the classifier AS a follow-up, which it cannot when the prior-turn digest is
/// only composed after routing.
/// </summary>
public interface ILaunchGroundingResolver
{
    /// <summary>On a CONTINUE, the session's prior-turn digest composed over any seed grounding; on a fresh launch, only the seed's own grounding (null for chat).</summary>
    Task<string?> ResolveAsync(TaskLaunchRequest request, TaskLaunchSeed seed, CancellationToken cancellationToken);
}
