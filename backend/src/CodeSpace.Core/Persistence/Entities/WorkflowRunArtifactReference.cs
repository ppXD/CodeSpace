namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Run-owned semantic reference to an exact immutable <see cref="ArtifactObject"/>. This row says what the bytes
/// mean to one run/lineage; it is not storage state and does not by itself prove verification, delivery or handoff.
/// Stable facts are immutable and supersession is a one-way pointer to a later reference.
/// </summary>
public class WorkflowRunArtifactReference : IEntity<Guid>
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid WorkflowRunId { get; set; }
    public string? NodeId { get; set; }
    public string IterationKey { get; set; } = string.Empty;
    public Guid? WorkPlanId { get; set; }
    public int? PlanVersion { get; set; }
    public string? WorkUnitId { get; set; }
    public string? WorkUnitContractHash { get; set; }
    public long? RequirementRevision { get; set; }
    public Guid? ExecutionAttemptId { get; set; }
    public int? ExecutionAttemptOrdinal { get; set; }
    public int? ExecutionGeneration { get; set; }
    public string Role { get; set; } = string.Empty;
    public string LogicalPath { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public bool Required { get; set; }
    public ArtifactRetention Retention { get; set; } = ArtifactRetention.Run;
    public DateTimeOffset? ExpiresAt { get; set; }
    public Guid? SupersededByReferenceId { get; set; }
    public Guid ArtifactObjectId { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }

    public WorkflowRun WorkflowRun { get; set; } = default!;
    public WorkPlan? WorkPlan { get; set; }
    public ArtifactObject ArtifactObject { get; set; } = default!;
    public WorkflowRunArtifactReference? SupersededByReference { get; set; }
}

public enum ArtifactRetention
{
    Ephemeral,
    Run,
    Team,
    Compliance,
    Permanent,
}
