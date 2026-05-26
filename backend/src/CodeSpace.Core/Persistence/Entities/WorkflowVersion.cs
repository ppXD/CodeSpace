namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Immutable per-save snapshot of a workflow definition. Composite key (WorkflowId,
/// Version). Workflow_run FKs back here so a run replay can reconstruct the exact JSON
/// the engine ran against — even if the workflow has been edited many times since.
/// </summary>
public class WorkflowVersion
{
    public Guid WorkflowId { get; set; }
    public int Version { get; set; }
    public string DefinitionJson { get; set; } = default!;

    /// <summary>
    /// SHA-256 hex of the canonicalised <see cref="DefinitionJson"/>. Computed in application
    /// code via <c>DefinitionHash.Compute</c> at INSERT time. Empty string only for legacy rows
    /// that haven't been touched; every new save populates it.
    /// </summary>
    public string DefinitionHash { get; set; } = string.Empty;

    /// <summary>
    /// Immutability anchor. Set at INSERT time alongside <see cref="DefinitionHash"/>. Once
    /// non-null, the row is frozen — the <c>workflow_version_enforce_immutability</c> trigger
    /// rejects any UPDATE/DELETE at the DB layer.
    /// </summary>
    public DateTimeOffset? CommittedAt { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }

    public Workflow Workflow { get; set; } = default!;
}
