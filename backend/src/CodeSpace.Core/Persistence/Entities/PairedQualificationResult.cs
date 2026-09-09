namespace CodeSpace.Core.Persistence.Entities;

/// <summary>The immutable terminal seal over one paired protocol, its exact durable observation census, and the statistics derived from it.</summary>
public sealed class PairedQualificationResult : IAuditable
{
    public Guid ObservationGroupId { get; set; }
    public string ProtocolDigest { get; set; } = string.Empty;
    public string EvidenceDigest { get; set; } = string.Empty;
    public string ResultDigest { get; set; } = string.Empty;
    public string StatisticsVersion { get; set; } = string.Empty;
    public int ExpectedObservationCount { get; set; }
    public int ObservationCount { get; set; }
    public bool QualifiedForCapabilityClaim { get; set; }
    public string OutcomeJson { get; set; } = "{}";
    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
}
