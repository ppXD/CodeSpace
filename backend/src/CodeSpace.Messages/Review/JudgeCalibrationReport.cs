using CodeSpace.Messages.Agents;

namespace CodeSpace.Messages.Review;

public enum JudgeCalibrationCategory
{
    SoundControl = 0,
    SelfPraiseWithoutEvidence = 1,
    PolishedWrongAnswer = 2,
    UnsupportedCitation = 3,
    PartialCriterionCoverage = 4,
    ArtifactPromptInjection = 5,
    ContradictoryEvidence = 6,
    KeywordSubstitution = 7,
}

/// <summary>One owner-labelled adversarial artifact. The fixed label is the independent oracle used to measure a rubric judge.</summary>
public sealed record JudgeCalibrationCase
{
    public required string Id { get; init; }
    public required JudgeCalibrationCategory Category { get; init; }
    public required AcceptanceRubric Rubric { get; init; }
    public required string Artifact { get; init; }
    public required bool ExpectedPassed { get; init; }
}

/// <summary>One immutable-in-memory observation. Consumers persist the complete report as evidence; missing or failed judgments stay in its denominator.</summary>
public sealed record JudgeCalibrationObservation
{
    public required string CaseId { get; init; }
    public required JudgeCalibrationCategory Category { get; init; }
    public required string EvaluatorGeneration { get; init; }
    public required bool ExpectedPassed { get; init; }
    public bool? ActualPassed { get; init; }
    public string? JudgeModel { get; init; }
    public ReviewModelIndependence Independence { get; init; }
    public string? FailureDetail { get; init; }
}

public sealed record BinomialConfidenceInterval(double Lower, double Upper);

/// <summary>Confusion counts and Wilson intervals for one provider-observed judge identity and evaluator generation.</summary>
public sealed record JudgeCalibrationStratum
{
    public required string EvaluatorGeneration { get; init; }
    public string? JudgeModel { get; init; }
    public int SampleSize { get; init; }
    public int EvaluatedSampleSize { get; init; }
    public int IndependentSampleSize { get; init; }
    public int TruePositives { get; init; }
    public int TrueNegatives { get; init; }
    public int FalsePositives { get; init; }
    public int FalseNegatives { get; init; }
    public double EvaluatorHealth { get; init; }
    public BinomialConfidenceInterval? FalsePositiveRate95 { get; init; }
    public BinomialConfidenceInterval? FalseNegativeRate95 { get; init; }
}

public sealed record JudgeCalibrationReport
{
    public required string CorpusGeneration { get; init; }
    public required string CorpusDigest { get; init; }
    public required IReadOnlyList<JudgeCalibrationObservation> Observations { get; init; }
    public required IReadOnlyList<JudgeCalibrationStratum> Strata { get; init; }
}
