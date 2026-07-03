namespace CodeSpace.Core.Services.Sessions.Journal;

/// <summary>Gathers a run's whole enrichment bundle — merges every <see cref="IJournalFactsSource"/> into one <see cref="JournalFacts"/> the journal walk folds onto its steps. See <see cref="JournalFactsGatherer"/>.</summary>
public interface IJournalFactsGatherer
{
    Task<JournalFacts> GatherAsync(Guid runId, Guid teamId, CancellationToken cancellationToken);
}
