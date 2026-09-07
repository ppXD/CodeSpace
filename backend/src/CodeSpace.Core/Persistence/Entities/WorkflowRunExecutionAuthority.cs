namespace CodeSpace.Core.Persistence.Entities;

/// <summary>Append-only authority, separate from the mutable run row so issuance never invalidates the engine's xmin.</summary>
public sealed class WorkflowRunExecutionAuthority
{
    public Guid WorkflowRunId { get; set; }
    public Guid TeamId { get; set; }
    public string ReceiptJson { get; set; } = default!;
    public DateTimeOffset IssuedAt { get; set; }
}
