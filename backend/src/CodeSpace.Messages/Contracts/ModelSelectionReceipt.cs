namespace CodeSpace.Messages.Contracts;

/// <summary>The source that won one launch-time model decision.</summary>
public enum ModelSelectionSource
{
    OperatorPin = 0,
    QualificationEvidence = 1,
    DeclaredPrior = 2,
}

/// <summary>A frozen, replay-safe explanation of one launch-time model choice. It carries no credential secret.</summary>
public sealed record ModelSelectionReceipt
{
    public const string CurrentPolicyVersion = "empirical-model-selection/v1";

    public string PolicyVersion { get; init; } = CurrentPolicyVersion;
    public required ModelSelectionSource Source { get; init; }
    public required Guid ModelCredentialModelId { get; init; }
    public required string Mode { get; init; }
    public required string CapabilityKey { get; init; }
    public Guid? QualificationReceiptId { get; init; }
    public string? SuiteDigest { get; init; }
    public string? EvidenceVersion { get; init; }
    public double? SolveRateLowerBound { get; init; }
    /// <summary>The ranking score after time-decaying empirical evidence toward the versioned prior.</summary>
    public double? AgeAdjustedScore { get; init; }
    public int? SampleSize { get; init; }
    public double? EvaluatorHealth { get; init; }
    public DateTimeOffset? EvidenceEffectiveFrom { get; init; }
    public DateTimeOffset? EvidenceExpiresAt { get; init; }
}
