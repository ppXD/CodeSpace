using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Projection;

/// <summary>
/// The task layer's entry point: project a <see cref="TaskBuildContext"/> into a workflow definition and START
/// it as a one-shot snapshot run (no <c>workflow</c> / <c>workflow_version</c> row). Resolves the builder by
/// <c>context.Route.ProjectionKind</c> via <see cref="ITaskProjectionRegistry"/>, builds the (always-valid)
/// definition, and hands it to <c>IRunFromSnapshotStarter</c> — so the whole projection→run path is generic
/// over the open projection-kind string with zero per-kind branching (Rule 16 — the orchestration lives here,
/// callers stay thin).
/// </summary>
public interface ITaskRunSnapshotFactory
{
    /// <summary>
    /// Resolve the builder for <paramref name="context"/>.Route.ProjectionKind, build its definition, validate +
    /// freeze + dispatch it through <c>IRunFromSnapshotStarter.StartFromSnapshotAsync</c>, and return the
    /// <see cref="TaskRunHandle"/> (run id + projection kind). Throws when no builder is registered for the kind,
    /// or <c>WorkflowValidationException</c> if the built definition is somehow invalid (a builder contract bug).
    /// <para><paramref name="session"/> is the pre-resolved WorkSession binding stamped onto the run; NULL = a
    /// session-less run (byte-identical to pre-session behaviour). Positional + explicit so every launch path
    /// consciously decides its session binding.</para>
    /// </summary>
    Task<TaskRunHandle> CreateAndRunAsync(TaskBuildContext context, Guid teamId, Guid actorUserId, SessionAssignment? session, CancellationToken cancellationToken);
}
