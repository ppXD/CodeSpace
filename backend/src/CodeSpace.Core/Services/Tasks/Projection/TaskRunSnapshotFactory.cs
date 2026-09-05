using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.Messages.Tasks;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Tasks.Projection;

/// <summary>
/// Default <see cref="ITaskRunSnapshotFactory"/> — the flat pipeline: resolve the builder by the route's
/// projection kind → build the definition → start it as a snapshot run → stamp the route provenance → return the
/// handle. Holds no state and no per-kind logic: the ONLY dispatch is
/// <c>_registry.Resolve(context.Route.ProjectionKind)</c>, so a new projection strategy plugs in by registering
/// its builder, with zero edit here (the generic spine).
/// </summary>
public sealed class TaskRunSnapshotFactory : ITaskRunSnapshotFactory, IScopedDependency
{
    /// <summary>Web defaults (camelCase, case-insensitive read) for the persisted route provenance, so the column reads like the API's own JSON. <c>RoutePlan</c> is all open strings / numbers — no enum converter needed.</summary>
    private static readonly JsonSerializerOptions RouteJson = new(JsonSerializerDefaults.Web);

    private readonly ITaskProjectionRegistry _registry;
    private readonly IRunFromSnapshotStarter _starter;
    private readonly CodeSpaceDbContext _db;

    public TaskRunSnapshotFactory(ITaskProjectionRegistry registry, IRunFromSnapshotStarter starter, CodeSpaceDbContext db)
    {
        _registry = registry;
        _starter = starter;
        _db = db;
    }

    public async Task<TaskRunHandle> CreateAndRunAsync(TaskBuildContext context, Guid teamId, Guid actorUserId, SessionAssignment? session, CancellationToken cancellationToken)
    {
        var builder = _registry.Resolve(context.Route.ProjectionKind);

        var definition = builder.Build(context);

        var launchPayloadJson = BuildLaunchPayload(context.Seed);

        // The pre-resolved session binding (the launch service opens it) threads straight onto the run; NULL leaves
        // the run session-less, byte-identical to pre-session behaviour.
        var runId = await _starter.StartFromSnapshotAsync(definition, teamId, actorUserId, launchPayloadJson, ScopeRepositoryIds(context.AgentProfile), context.Route.ProjectionKind, session, cancellationToken).ConfigureAwait(false);

        await StampRouteProvenanceAsync(runId, WithEffectiveAutonomy(context), cancellationToken).ConfigureAwait(false);

        return new TaskRunHandle { RunId = runId, ProjectionKind = context.Route.ProjectionKind };
    }

    /// <summary>
    /// Records the FULL routing decision on the staged run beside its denormalised <c>projection_kind</c> — the
    /// tier, recipe, bounds preset + caps, whether the classifier (not the operator) chose the tier, its confidence
    /// and rationale, and any capability degrade. Without it the run keeps only "which builder ran" and a reader
    /// could never say WHY it got the depth it got.
    ///
    /// <para>A targeted single-column UPDATE, NOT a tracked mutation. The starter already committed the row, and by
    /// the time it returns the post-commit dispatcher may already have CAS-ed the run Pending→Enqueued from another
    /// context — a tracked save then loses the optimistic-concurrency race and throws
    /// (<c>DbUpdateConcurrencyException</c>: 0 rows affected), failing an otherwise perfectly good launch over
    /// provenance. This column is write-once and no other writer ever touches it, so it can never be in genuine
    /// contention; skipping the concurrency token is correct rather than merely convenient.</para>
    /// </summary>
    /// <summary>
    /// Carry the run's RESOLVED tier onto the provenance the stamp persists. The router produced the ceiling; only the
    /// launch knows what the operator asked for and therefore what <c>ClampAutonomy</c> settled on — so the effective
    /// tier is stamped HERE, at the one place holding both. Without it <c>route_plan_jsonb</c> records what the run was
    /// ALLOWED and never what it GOT, and a reader cannot tell a declined network from a denied one. A projection with
    /// no agent profile (or a blank tier) leaves the field blank — "unknown", exactly as a pre-field run reads.
    /// </summary>
    private static RoutePlan WithEffectiveAutonomy(TaskBuildContext context) =>
        context.AgentProfile?.AutonomyLevel is { Length: > 0 } tier ? context.Route with { EffectiveAutonomy = tier } : context.Route;

    private async Task StampRouteProvenanceAsync(Guid runId, RoutePlan route, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(route, RouteJson);

        await _db.WorkflowRun.Where(r => r.Id == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RoutePlanJson, json), cancellationToken).ConfigureAwait(false);
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
