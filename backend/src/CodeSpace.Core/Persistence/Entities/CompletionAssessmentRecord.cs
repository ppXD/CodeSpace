namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// P2a-4: one APPEND-ONLY row per compose of a terminal contract-era run — the durable "what the protocol would
/// have said" record the Shadow sweep writes and P2b's terminal CAS will bind to. <see cref="LegacyIsSolved"/>
/// snapshots the legacy scorecard ladder AT COMPOSE TIME so the degraded-inflation delta is a standing query.
/// </summary>
public class CompletionAssessmentRecord : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid WorkflowRunId { get; set; }
    public string EnforcementMode { get; set; } = string.Empty;
    public string Basis { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string Verification { get; set; } = string.Empty;
    public string AssessmentJson { get; set; } = string.Empty;
    public bool LegacyIsSolved { get; set; }
    public int RejectionCount { get; set; }
    public int ContractErrorCount { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
}
