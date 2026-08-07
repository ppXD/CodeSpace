namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// P1 (v4.3): one APPENDED row per change of a requirement's current envelope — the history
/// <see cref="CompletionRequirement"/>'s in-place upsert used to destroy. <see cref="Revision"/> is a table-wide
/// identity, so per-key order is <c>ORDER BY Revision</c> with no per-key counter to race. Append-only: nothing
/// ever updates or deletes a row; a receipt binding to a specific revision builds on this in a later slice.
/// </summary>
public class CompletionRequirementRevision : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }
    public long Revision { get; set; }
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
