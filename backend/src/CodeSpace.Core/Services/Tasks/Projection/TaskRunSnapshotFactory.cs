using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Projection;

/// <summary>
/// Default <see cref="ITaskRunSnapshotFactory"/> — the flat pipeline: resolve the builder by the route's
/// projection kind → build the definition → start it as a snapshot run → return the handle. Holds no state and
/// no per-kind logic: the ONLY dispatch is <c>_registry.Resolve(context.Route.ProjectionKind)</c>, so a new
/// projection strategy plugs in by registering its builder, with zero edit here (the generic spine).
/// </summary>
public sealed class TaskRunSnapshotFactory : ITaskRunSnapshotFactory, IScopedDependency
{
    private readonly ITaskProjectionRegistry _registry;
    private readonly IRunFromSnapshotStarter _starter;

    public TaskRunSnapshotFactory(ITaskProjectionRegistry registry, IRunFromSnapshotStarter starter)
    {
        _registry = registry;
        _starter = starter;
    }

    public async Task<TaskRunHandle> CreateAndRunAsync(TaskBuildContext context, Guid teamId, Guid actorUserId, SessionAssignment? session, CancellationToken cancellationToken)
    {
        var builder = _registry.Resolve(context.Route.ProjectionKind);

        var definition = builder.Build(context);

        var launchPayloadJson = BuildLaunchPayload(context.Seed);

        // The pre-resolved session binding (the launch service opens it) threads straight onto the run; NULL leaves
        // the run session-less, byte-identical to pre-session behaviour.
        var runId = await _starter.StartFromSnapshotAsync(definition, teamId, actorUserId, launchPayloadJson, ScopeRepositoryIds(context.AgentProfile), context.Route.ProjectionKind, session, cancellationToken).ConfigureAwait(false);

        return new TaskRunHandle { RunId = runId, ProjectionKind = context.Route.ProjectionKind };
    }

    /// <summary>
    /// The launch SCOPE repo set this run was launched against — the agent profile's primary repo plus its related
    /// (multi-repo) repos, distinct. Empty when the projection has no agent profile / no repo. A multi-repo launch
    /// (<c>TaskLaunchRequest.RelatedRepositories</c> → <c>BuildAgentProfile</c>) populates the profile's related repos,
    /// so the scope folds them in here — the set a session-branch resolver later scans per repo.
    /// </summary>
    private static IReadOnlyList<Guid> ScopeRepositoryIds(ResolvedAgentProfile? profile)
    {
        if (profile is null) return [];

        var ids = new List<Guid>();
        if (profile.RepositoryId is { } primary) ids.Add(primary);
        if (profile.RelatedRepositories is { } related) ids.AddRange(related.Select(r => r.RepositoryId));

        return ids.Distinct().ToList();
    }

    /// <summary>The launch payload the run sees as <c>{{trigger.*}}</c> — the seed's goal so a trigger.manual projection can echo it. The builder bakes everything it needs into the definition, so this is provenance, not the binding source.</summary>
    private static string BuildLaunchPayload(TaskLaunchSeed seed) =>
        JsonSerializer.Serialize(new { goal = seed.Goal });
}
