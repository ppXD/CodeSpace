namespace CodeSpace.Messages.Enums;

/// <summary>
/// How an INDEPENDENT reviewer model is applied to a producer's output — the generic adversarial-review primitive: a
/// second model verifies / improves what a planner / supervisor / agent produced (the "send my plan to another model"
/// pattern, generalized). Default <see cref="None"/> ⇒ no review (byte-identical to before the critic existed).
/// </summary>
public enum ReviewMode
{
    /// <summary>No review — the producer's output is used verbatim.</summary>
    None = 0,

    /// <summary>GATE: an independent reviewer SCORES / approves the output and surfaces concrete issues. At the planner stage the verdict ANNOTATES the plan (a human still reviews it) — it never discards a usable plan.</summary>
    Gate,

    /// <summary>IMPROVE (reflection): an independent reviewer CRITIQUES the output, and the critique is fed BACK to the producer for ONE bounded revision — usually a better result than a single pass.</summary>
    Improve,
}
