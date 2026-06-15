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

    public async Task<TaskRunHandle> CreateAndRunAsync(TaskBuildContext context, Guid teamId, Guid actorUserId, CancellationToken cancellationToken)
    {
        var builder = _registry.Resolve(context.Route.ProjectionKind);

        var definition = builder.Build(context);

        var launchPayloadJson = BuildLaunchPayload(context.Seed);

        var runId = await _starter.StartFromSnapshotAsync(definition, teamId, actorUserId, launchPayloadJson, cancellationToken).ConfigureAwait(false);

        return new TaskRunHandle { RunId = runId, ProjectionKind = context.Route.ProjectionKind };
    }

    /// <summary>The launch payload the run sees as <c>{{trigger.*}}</c> — the seed's goal so a trigger.manual projection can echo it. The builder bakes everything it needs into the definition, so this is provenance, not the binding source.</summary>
    private static string BuildLaunchPayload(TaskLaunchSeed seed) =>
        JsonSerializer.Serialize(new { goal = seed.Goal });
}
