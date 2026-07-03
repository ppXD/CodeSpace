using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Sessions.Journal;

/// <summary>
/// Contributes ENRICHMENT facts for a run's journal steps — the reads the pure describers can't do (a step's authored
/// rationale, its spawned agents, its diffstat all live in the durable records, not the timeline event). A source reads
/// its own records for the run and returns facts keyed by the SAME step id the timeline emits (e.g. a supervisor decision
/// keys by <c>SupervisorDecisionTimelineMap.EventId</c>), so the gatherer can match them to steps without any positional
/// coupling. The <see cref="IJournalFactsGatherer"/> injects EVERY registered source and merges them — a NEW fact kind
/// plugs in as a dropped impl with ZERO gatherer edit (the timeline-source / describer pattern, applied to facts).
///
/// <para>Scoped — a source does bounded DB reads (unlike the pure describers). Returns an EMPTY map for a run it has no
/// facts about, so a run with no supervisor tape / no agents simply gets no enrichment, never an error.</para>
/// </summary>
public interface IJournalFactsSource : IScopedDependency
{
    Task<IReadOnlyDictionary<string, JournalStepFacts>> GatherAsync(Guid runId, Guid teamId, CancellationToken cancellationToken);
}
