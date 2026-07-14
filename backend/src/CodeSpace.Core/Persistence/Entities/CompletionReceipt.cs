namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// P2a-2 (R): one durable receipt row — WHAT HAPPENED against one requirement. Append-only, exactly-once per
/// (run, kind, requirement_ref, attempt_id, target_key): a crash-replayed producer lands on the same row.
/// <see cref="TargetKey"/> materializes the admission law "cardinality counts DISTINCT targets" as a constraint
/// (<c>TargetRef</c> when the receipt names one, else <c>attempt:{attempt_id}</c>). <see cref="EnvelopeJson"/>
/// carries the FULL canonical <c>ReceiptEnvelope</c> — the envelope is the truth.
/// </summary>
public class CompletionReceipt : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid WorkflowRunId { get; set; }
    public string RequirementRef { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public Guid AttemptId { get; set; }
    public string TargetKey { get; set; } = string.Empty;
    public string EnvelopeJson { get; set; } = string.Empty;
    public DateTimeOffset ObservedAt { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
}
