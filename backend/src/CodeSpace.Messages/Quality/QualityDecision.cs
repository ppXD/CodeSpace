namespace CodeSpace.Messages.Quality;

/// <summary>
/// The outcome of one <c>QualityPolicy</c> evaluation (P22, Rule 18.1 — a data noun): the mechanism to spend the
/// next increment on, plus the EVIDENCE that chose it. <see cref="Reason"/> is not a label — it cites the concrete
/// input facts (the numbers and dispositions that made the row match), so a reader of a durable decision can tell
/// WHY without replaying the policy, and so a stop can never be an unexplained stop (the P22 invariant: every stop
/// — goal met, cost exhausted, no progress — has evidence).
/// </summary>
public sealed record QualityDecision
{
    /// <summary>The mechanism the recorded evidence chose.</summary>
    public required QualityMechanism Mechanism { get; init; }

    /// <summary>WHY, citing the input facts that matched (never a bare label). Never blank — every ordered row authors one.</summary>
    public required string Reason { get; init; }

    /// <summary>
    /// The expected marginal value of spending the next increment through <see cref="Mechanism"/>, when it is
    /// KNOWN. Today only the stops know it — a stop's marginal value is exactly <c>0</c> (no further spend can
    /// change the outcome). Every non-stop mechanism records <c>null</c>: "not yet measurable", NOT "zero" and not
    /// an invented estimate. P22-9c's same-budget ablation harness is what fills these in with measured values;
    /// until then a null here is the honest reading and consumers must treat it as unknown.
    /// </summary>
    public double? ExpectedMarginalValue { get; init; }
}
