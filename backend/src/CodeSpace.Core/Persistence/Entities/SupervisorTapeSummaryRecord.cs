namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// The rolling decision-tape digest row (P1.2 auto-compact) — one per supervisor run (<see cref="SupervisorRunId"/>
/// is unique): the model-written summary of every ledger decision with <c>Sequence ≤ UpToSequence</c>, which the
/// decider's prompt renders in place of those raw rows. DERIVED state: the decision ledger stays the complete tape
/// (bounds / recitation / replay never read this), so losing a row merely costs a re-compaction. Soft run link
/// (no FK), team-scoped like every row.
/// </summary>
public class SupervisorTapeSummaryRecord : IEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid TeamId { get; set; }

    public Guid SupervisorRunId { get; set; }

    /// <summary>The highest ledger <c>Sequence</c> folded into the digest — the prompt renders only decisions AFTER it.</summary>
    public long UpToSequence { get; set; }

    public string Summary { get; set; } = default!;

    public DateTimeOffset CreatedDate { get; set; }

    public DateTimeOffset? UpdatedDate { get; set; }
}
