namespace CodeSpace.Messages.Agents.Benchmark;

/// <summary>Which campaign's runtime to observe. The arms are NAMED rather than positional on purpose: swapping control for candidate would produce a different manifest and read as a substituted runtime.</summary>
public sealed record QualificationRuntimeCollectRequest
{
    /// <summary>The campaign's own identity — and, as a per-campaign random value, the HMAC salt every credential fingerprint in the manifest is taken under.</summary>
    public required Guid ObservationGroupId { get; init; }

    public required Guid TeamId { get; init; }

    public required Guid ControlModelRowId { get; init; }

    public required Guid CandidateModelRowId { get; init; }
}
