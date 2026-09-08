namespace CodeSpace.Messages.Review;

/// <summary>
/// An independent judge model's verdict on a deliverable against an acceptance rubric (triad S7 — a data noun,
/// Rule 18.1). Unlike <see cref="CriticVerdict"/> (advisory, fail-open), this feeds an ORACLE: the grader maps a
/// <see cref="Failed"/> verdict to a fail-closed grade-error — a judge that cannot judge never becomes a silent pass.
/// Per-criterion verdicts are BINARY with evidence (never a Likert scale), so the aggregate is auditable and the
/// judge itself is meta-evaluable (the evidence either supports the verdict or it doesn't).
/// </summary>
public sealed record RubricJudgeVerdict
{
    /// <summary>One verdict per rubric criterion, joined by criterion id.</summary>
    public IReadOnlyList<RubricCriterionVerdict> Criteria { get; init; } = Array.Empty<RubricCriterionVerdict>();

    /// <summary>The judge could not produce a valid verdict (no model / call / parse / incomplete echo) — the grader fails CLOSED as a grade-error.</summary>
    public bool Failed { get; init; }

    /// <summary>Why the judge failed (only when <see cref="Failed"/>).</summary>
    public string? FailureDetail { get; init; }

    /// <summary>The bounded provider-observed judge identity. Null when the provider supplied no trusted observation; never inferred from the configured alias.</summary>
    public string? JudgeModel { get; init; }

    /// <summary>The provider-observed producer/judge relation. Only DistinctBackingModel is independently calibrated.</summary>
    public ReviewModelIndependence Independence { get; init; }

    public bool Calibrated => Independence == ReviewModelIndependence.DistinctBackingModel;

    public static RubricJudgeVerdict JudgeFailed(string reason) => new() { Failed = true, FailureDetail = reason };
}

/// <summary>The complete rubric-judge invocation, including producer identity evidence. One data object keeps the evaluator API extensible without accumulating loose parameters.</summary>
public sealed record RubricJudgeRequest
{
    public required Agents.AcceptanceRubric Rubric { get; init; }
    public required string Artifact { get; init; }
    public string? Goal { get; init; }
    public required Guid TeamId { get; init; }
    public ReviewModelIdentity? ProducerModel { get; init; }
}

/// <summary>One criterion's binary verdict: met or not, with the judge's evidence quoted from the artifact.</summary>
public sealed record RubricCriterionVerdict
{
    public required string Id { get; init; }

    public required bool Met { get; init; }

    /// <summary>The judge's evidence — what in the artifact supports (or fails) the requirement. Never executed; the audit trail.</summary>
    public string Evidence { get; init; } = "";
}
