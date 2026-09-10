namespace CodeSpace.Messages.Quality;

/// <summary>
/// The outcome of one <c>QualityPolicy</c> evaluation (P22, Rule 18.1 — a data noun): the mechanism to spend the
/// next increment on, plus the EVIDENCE that chose it. <see cref="Reason"/> is not a label — it cites the concrete
/// input facts (the numbers and dispositions that made the row match), so a reader of a durable decision can tell
/// WHY without replaying the policy, and so a stop can never be an unexplained stop (the P22 invariant: every stop
/// — goal met, cost exhausted, no progress, human-waived — has evidence).
///
/// <para><b>No expected-value field, on purpose.</b> A field that is <c>0</c> for a stop and <c>null</c> for
/// everything else is not a measurement — it is the mechanism enum restated in a numeric field a consumer could
/// mistake for one. P22-9c's same-budget ablation harness is what produces real per-mechanism values; the field
/// arrives WITH them, carrying numbers somebody measured.</para>
/// </summary>
public sealed record QualityDecision
{
    /// <summary>The mechanism the recorded evidence chose.</summary>
    public required QualityMechanism Mechanism { get; init; }

    /// <summary>WHY, citing the input facts that matched (never a bare label). Never blank — every ordered row authors one.</summary>
    public required string Reason { get; init; }
}
