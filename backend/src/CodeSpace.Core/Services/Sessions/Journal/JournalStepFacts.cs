using CodeSpace.Messages.Dtos.Sessions.Journal;

namespace CodeSpace.Core.Services.Sessions.Journal;

/// <summary>
/// The ENRICHMENT facts a <see cref="IJournalFactsSource"/> attaches to one journal step, keyed by the step's id — the
/// data the pure describers cannot see (it lives in the durable records, not the timeline event). The journal walk folds
/// each step's facts onto its <c>JournalStep</c> after describing it, so the Activity spine stays a plain event log while
/// the journal reads richer facts on top. GROWS one nullable field per fact kind (rationale + agent cards now; diffstat
/// next) — a new kind is an additive property + a new source, never a shape change. All-null is the "no facts" case.
/// </summary>
public sealed record JournalStepFacts
{
    /// <summary>The step's authored reasoning line ("why · Evidence: …"), from the supervisor decision payload. Null when the actor authored none.</summary>
    public string? Rationale { get; init; }

    /// <summary>The agents this decision spawned / re-ran, as render-ready cards. Null when the step spawned none.</summary>
    public IReadOnlyList<JournalAgentCard>? Agents { get; init; }

    /// <summary>Field-wise coalesce of two sources' facts for the SAME step — a later source's set field wins, an unset field leaves the earlier one intact. So independent sources (rationale · agents · diffstat) compose onto one step without clobbering each other.</summary>
    public JournalStepFacts Merge(JournalStepFacts other) => new()
    {
        Rationale = other.Rationale ?? Rationale,
        Agents = other.Agents ?? Agents,
    };
}
