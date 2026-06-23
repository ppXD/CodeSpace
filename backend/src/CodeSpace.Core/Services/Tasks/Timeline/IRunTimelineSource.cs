using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Tasks.Timeline;

/// <summary>
/// One contributor to a run's narrative timeline — it reads ITS OWN durable ledger slice (the run-record ledger,
/// the supervisor decision tape, the agent event log, …) for a given (run, team) and emits the
/// <see cref="RunTimelineEvent"/> rows it knows how to derive. The projector fans out across EVERY registered
/// source and merges them by OccurredAt — there is no registry / resolve-by-key dispatch: a source either has
/// events for the run or contributes none (an empty list). A NEW event source plugs in as a dropped
/// <see cref="IScopedDependency"/> impl the projector's injected <c>IEnumerable</c> picks up with ZERO projector
/// edit (Rule 7 — narrow + additive).
///
/// <para>Scoped because every source reads scoped DB. READ-ONLY — a source never writes or mutates the engine. A
/// source that throws is caught per-source by the projector (a broken source degrades to fewer events, never 500s).</para>
/// </summary>
public interface IRunTimelineSource : IScopedDependency
{
    /// <summary>This source's stable provenance key, stamped on every <see cref="RunTimelineEvent.SourceKey"/> (e.g. "run-record", "supervisor", "agent-events"). Also the cross-source sort tie-break.</summary>
    string SourceKey { get; }

    /// <summary>Contribute this source's events for the run. Returns an empty list when it has nothing (e.g. a non-supervisor run for the supervisor source). The (run, team) is already tenancy-checked by the projector.</summary>
    Task<IReadOnlyList<RunTimelineEvent>> ContributeAsync(RunTimelineContext context, CancellationToken cancellationToken);
}
