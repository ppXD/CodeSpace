namespace CodeSpace.Messages.Contracts;

/// <summary>Whether one frozen qualification round can be attributed to its selected credentialed-model row.</summary>
public enum ModelQualificationAttribution
{
    LegacyUnknown = 0,
    Bound = 1,
    UnboundSelection = 2,
    MissingObservation = 3,
    InconsistentObservation = 4,
    NonLaunchPath = 5,
}

/// <summary>Typed, versioned model evidence minted from one immutable qualification round.</summary>
public sealed record ModelQualificationEvidence
{
    public const string CurrentVersion = "model-qualification/v1";

    public string Version { get; init; } = CurrentVersion;
    public Guid? ModelCredentialModelId { get; init; }
    public string? RequestedModel { get; init; }
    public string? ObservedModel { get; init; }
    public ModelQualificationAttribution Attribution { get; init; } = ModelQualificationAttribution.LegacyUnknown;
    public int SampleSize { get; init; }
    public int ObservedCellCount { get; init; }
    public double SolveRateLowerBound { get; init; }
    public double EvaluatorHealth { get; init; }
}
