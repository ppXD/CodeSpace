namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// P2 (v4.3): one row per run — the MONOTONIC completion-ledger version the count-blind writers advance via an
/// atomic upsert-increment (<c>CompletionLedgerVersionBump</c>). Deliberately its own table rather than a
/// workflow_run column: the run row carries an xmin concurrency token and is tracked by the engine for the whole
/// turn, so a side-writer's UPDATE there aborts the engine's own save. No audit columns — the row IS the version,
/// nothing else; readers treat a missing row as version 0.
/// </summary>
public class CompletionLedgerHead
{
    public Guid WorkflowRunId { get; set; }
    public long Version { get; set; }
}
