using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Tasks.Timeline;

/// <summary>
/// Derives a run's narrative timeline from its durable ledgers — the Activity-timeline data layer. It does the
/// team-scope precheck ONCE (a foreign / absent run → null, which the handler maps to 404 — existence is never
/// leaked), then fans out across every <see cref="IRunTimelineSource"/> and merges their events by OccurredAt.
/// READ-ONLY.
/// </summary>
public interface IRunTimelineProjector : IScopedDependency
{
    /// <summary>Project the run's merged, chronologically-sorted events, or <c>null</c> when the run does not belong to the team (404-conflate, no existence leak). A single source throwing degrades to fewer events — it never sinks the projection.</summary>
    Task<IReadOnlyList<RunTimelineEvent>?> ProjectAsync(Guid runId, Guid teamId, CancellationToken cancellationToken);
}
