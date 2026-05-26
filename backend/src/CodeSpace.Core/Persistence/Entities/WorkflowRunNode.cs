using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Read-only projection over <see cref="WorkflowRunRecord"/>. The underlying
/// <c>workflow_run_node</c> SQL view aggregates node.* lifecycle events from the ledger into
/// the latest-state row shape that <see cref="WorkflowRun"/> consumers (the run-detail UI,
/// integration tests) expect.
///
/// Read-only: there is no INSERT/UPDATE/DELETE path through EF for this entity — the source
/// of truth is the ledger, and the engine writes <see cref="WorkflowRunRecord"/> rows via
/// <c>IRunRecordLogger</c>. Querying via <c>_db.WorkflowRunNode</c> hits the view.
///
/// Audit fields are deliberately absent (the ledger IS the audit trail).
/// </summary>
public class WorkflowRunNode
{
    public Guid RunId { get; set; }
    public string NodeId { get; set; } = default!;
    public string IterationKey { get; set; } = string.Empty;

    public NodeStatus Status { get; set; } = NodeStatus.Pending;
    public string InputsJson { get; set; } = "{}";
    public string OutputsJson { get; set; } = "{}";
    public string? Error { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
