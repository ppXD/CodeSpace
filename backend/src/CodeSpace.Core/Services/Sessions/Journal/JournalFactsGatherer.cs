using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Sessions.Journal;

/// <summary>
/// Default <see cref="IJournalFactsGatherer"/> — runs EVERY registered <see cref="IJournalFactsSource"/> for the run and
/// merges their per-step facts into one bundle (Rule 16 thin composition). Autofac injects the whole <c>IEnumerable</c>,
/// so a new source is purely a dropped impl — the gatherer never names a concrete one. When two sources contribute to the
/// SAME step id (a decision that has both a rationale and staged agents) their facts <see cref="JournalStepFacts.Merge"/>
/// field-wise, so they compose rather than clobber. Scoped — the sources it drives do bounded reads.
/// </summary>
public sealed class JournalFactsGatherer : IJournalFactsGatherer, IScopedDependency
{
    private readonly IReadOnlyList<IJournalFactsSource> _sources;

    public JournalFactsGatherer(IEnumerable<IJournalFactsSource> sources)
    {
        _sources = sources.ToList();
    }

    public async Task<JournalFacts> GatherAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var merged = new Dictionary<string, JournalStepFacts>();

        foreach (var source in _sources)
        {
            var contributed = await source.GatherAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

            foreach (var (stepId, facts) in contributed)
                merged[stepId] = merged.TryGetValue(stepId, out var existing) ? existing.Merge(facts) : facts;
        }

        return merged.Count == 0 ? JournalFacts.Empty : new JournalFacts { ByStepId = merged };
    }
}
