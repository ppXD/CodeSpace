namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// P2a-2 (R): one durable requirement row — WHAT one run owes, one row per (run, kind, requirement_ref), upserted
/// never duplicated (ux_completion_requirement). <see cref="EnvelopeJson"/> carries the FULL canonical
/// <c>RequirementEnvelope</c>; the indexed columns exist for query, the envelope is the truth. Soft links only —
/// the ledger outlives the run, matching <see cref="PublishManifest"/>.
/// </summary>
public class CompletionRequirement : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid WorkflowRunId { get; set; }
    public string RequirementRef { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string EnvelopeJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
}
