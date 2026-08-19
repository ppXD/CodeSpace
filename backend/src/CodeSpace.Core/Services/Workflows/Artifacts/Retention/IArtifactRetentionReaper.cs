using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Artifacts;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Retention;

/// <summary>
/// Runs one BOUNDED artifact-retention sweep. Bounded means three separate things, all of them load-bearing: it claims
/// at most <c>BatchSize</c> declarations per sweep; it never reads <c>workflow_artifact</c> as a table, only the rows
/// its claimed declarations name; and every lock it takes is scoped to one declaration's own transaction, so a sweep
/// cannot block a live artifact write however long the backlog is.
/// </summary>
public interface IArtifactRetentionReaper : IScopedDependency
{
    Task<ArtifactRetentionSweepSummary> SweepAsync(CancellationToken cancellationToken);
}
