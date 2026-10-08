namespace CodeSpace.Messages.Agents.Benchmark;

/// <summary>
/// What a critic-on arm's output review did to each result, crossed with the objective grade. A review that FLAGGED
/// (<c>output-flagged</c>) and one that could not examine the result (<c>output-unreviewed</c>) both hold it for a human,
/// but only the first is the critic's judgement — the second is the fail-closed burden of a review that never reached a
/// verdict — so they are counted apart, and neither is a miss.
/// </summary>
public sealed record CriticInterventionTally
{
    /// <summary>Flagged, revised, and the final change passed and shipped: the critic's productive value (including false alarms the revise cleared — the oracle cannot split the two).</summary>
    public required int CatchAndResolve { get; init; }

    /// <summary>Flagged and held a broken change for a human: value.</summary>
    public required int TrueHold { get; init; }

    /// <summary>Flagged and held a correct change for a human: the critic's burden.</summary>
    public required int FalseHold { get; init; }

    /// <summary>The review could not examine a broken change and held it: no verdict, but nothing broken shipped.</summary>
    public required int TrueUnreviewedHold { get; init; }

    /// <summary>The review could not examine a correct change and held it: the fail-closed burden.</summary>
    public required int FalseUnreviewedHold { get; init; }

    /// <summary>The review stayed silent on a broken change, which shipped: a miss.</summary>
    public required int Missed { get; init; }
}
