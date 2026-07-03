namespace CodeSpace.Core.Services.Sessions.Journal;

/// <summary>
/// The whole-run enrichment bundle — every step id that has authored facts, mapped to its <see cref="JournalStepFacts"/>.
/// The <see cref="IJournalFactsGatherer"/> merges all sources into one of these; the journal walk looks each step up by id
/// and folds its facts on. A step id absent from the map has no facts (<see cref="For"/> returns null) — the common case,
/// so most steps stay bare. Immutable snapshot for one walk.
/// </summary>
public sealed record JournalFacts
{
    public IReadOnlyDictionary<string, JournalStepFacts> ByStepId { get; init; } = new Dictionary<string, JournalStepFacts>();

    public static readonly JournalFacts Empty = new();

    /// <summary>This step's facts, or null when it has none — the walk enriches only when non-null.</summary>
    public JournalStepFacts? For(string stepId) => ByStepId.TryGetValue(stepId, out var facts) ? facts : null;
}
