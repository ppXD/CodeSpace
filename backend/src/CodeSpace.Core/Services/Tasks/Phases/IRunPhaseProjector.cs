using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Tasks.Phases;

namespace CodeSpace.Core.Services.Tasks.Phases;

/// <summary>
/// Derives a run's UI-facing phase tree from its durable substrate — the background-tasks UI data layer. It does
/// the team-scope precheck ONCE (a foreign / absent run → null, which the handler maps to 404 — existence is never
/// leaked), then fans out across every <see cref="IRunPhaseSource"/> and merges their rows by Order. READ-ONLY.
/// </summary>
public interface IRunPhaseProjector : IScopedDependency
{
    /// <summary>Project the run's merged phase list, or <c>null</c> when the run does not belong to the team (404-conflate, no existence leak). A single source throwing degrades to fewer phases — it never sinks the projection.</summary>
    Task<IReadOnlyList<RunPhase>?> ProjectAsync(Guid runId, Guid teamId, CancellationToken cancellationToken);
}
