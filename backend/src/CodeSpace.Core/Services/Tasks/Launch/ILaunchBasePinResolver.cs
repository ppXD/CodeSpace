using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Launch;

/// <summary>
/// S1 — resolves the launch's immutable base VECTOR: one tip commit per repo the launch touches, resolved ONCE at
/// launch time so the planner, the grounded plan reviewer, and every agent the run dispatches materialize the SAME
/// base regardless of when they clone. The vector rides <c>TaskBuildContext.PinnedShas</c> into every projection.
/// </summary>
public interface ILaunchBasePinResolver
{
    /// <summary>
    /// The per-repo (repositoryId → commit sha) base vector for a launch, over the primary + every related repo.
    /// A repo is UNPINNED (absent from the map) when it has no clone URL (nothing will ever clone it), when it rides
    /// a SESSION-soft ref from <paramref name="sessionBaseRefs"/> (a soft ref's "prior branch, or default if pruned"
    /// disjunction cannot be expressed as one commit), or when its remote is empty. A HARD ref that is missing or an
    /// unreachable remote fails the launch LOUD (the clone would fail identically later). Null when nothing pinned.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>?> ResolveVectorAsync(Guid teamId, TaskLaunchSeed seed, ResolvedAgentProfile profile, IReadOnlyDictionary<Guid, string> sessionBaseRefs, CancellationToken cancellationToken);
}
