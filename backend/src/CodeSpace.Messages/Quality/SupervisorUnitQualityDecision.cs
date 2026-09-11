namespace CodeSpace.Messages.Quality;

/// <summary>
/// ONE plan unit's <c>QualityPolicy</c> reading, carried from the turn fold to the prompt, to the durable decision
/// row, and to the Room (P22-9b, Rule 18.1 — a data noun). The unit id lives HERE and never inside
/// <see cref="QualityDecisionInput"/>: the policy still cannot see which unit it is deciding about (the purity
/// invariant is untouched), while every consumer of the recommendation can say which unit it is about.
///
/// <para><see cref="Facts"/> travels WITH the recommendation on purpose. A mechanism with no facts beside it is a
/// verdict a reader has to take on trust — and the facts are exactly what a reader (or the model, which is invited
/// to reject the recommendation) needs in order to name the evidence the policy missed. It also makes the durable
/// row self-describing: a later reader can re-run <c>QualityPolicy.Decide</c> over the recorded facts and see
/// whether the table has since changed its mind, without replaying the run.</para>
///
/// <para>This is a RECOMMENDATION, never an instruction: nothing in the supervisor lane branches on
/// <see cref="Mechanism"/>. The verb roster and the action mask are untouched by it, so the mechanism can never
/// mask a verb the model may emit nor offer one it may not.</para>
/// </summary>
public sealed record SupervisorUnitQualityDecision
{
    /// <summary>The plan-local subtask id this reading is about — the same id the recitation, the dependency gate and the retry payload all key on.</summary>
    public required string SubtaskId { get; init; }

    /// <summary>The mechanism the recorded evidence chose for this unit.</summary>
    public required QualityMechanism Mechanism { get; init; }

    /// <summary>WHY, citing the facts that matched — <c>QualityDecision.Reason</c> verbatim, never a re-worded copy.</summary>
    public required string Reason { get; init; }

    /// <summary>The exact facts <see cref="Mechanism"/> was computed from, so the recommendation can be audited (and rejected) on its own evidence.</summary>
    public required QualityDecisionInput Facts { get; init; }
}
