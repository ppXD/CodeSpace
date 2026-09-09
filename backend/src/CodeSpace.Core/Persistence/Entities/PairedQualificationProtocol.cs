namespace CodeSpace.Core.Persistence.Entities;

/// <summary>The immutable, pre-outcome protocol for one paid paired qualification campaign. The row commits before its first TaskLaunch cell.</summary>
public class PairedQualificationProtocol : IAuditable
{
    public Guid ObservationGroupId { get; set; }
    public Guid TeamId { get; set; }
    public string SuiteDigest { get; set; } = string.Empty;
    public string SuiteVersion { get; set; } = string.Empty;
    public string CodeRevision { get; set; } = string.Empty;
    public Guid ControlModelRowId { get; set; }
    public Guid CandidateModelRowId { get; set; }
    public string? ControlSelectionJson { get; set; }
    public string? CandidateSelectionJson { get; set; }
    public bool RequiresCellAdmission { get; set; }
    public string StatisticsVersion { get; set; } = string.Empty;
    public string Criterion { get; set; } = string.Empty;
    public int SessionsPerCell { get; set; }
    public int MinimumIndependentClusters { get; set; }
    public int MinimumStrata { get; set; }
    public int MinimumRequiredExecutionClusters { get; set; }
    public double MinimumEvaluatorHealth { get; set; }
    public decimal MaxCostUsdPerLaunch { get; set; }
    public double MinimumQualityLift { get; set; }
    public double NonInferiorityMargin { get; set; }
    public double MinimumCostReduction { get; set; }
    public bool RequireDistinctObservedModels { get; set; }
    public string OrderingSeed { get; set; } = string.Empty;
    public string ProtocolDigest { get; set; } = string.Empty;
    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
}
