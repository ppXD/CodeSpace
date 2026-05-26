namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Frozen snapshot of one variable value at a workflow_run's start. Together with
/// <c>workflow_run.release_hash_at_run</c> + the immutable <c>workflow_version</c>, this gives
/// the run total replay-time reproducibility for plain values.
///
/// <para>Storage rules (enforced by the DB CHECK constraint):
///   • <c>ValueType = "Secret"</c> → <c>ValuePlain</c> is NULL. The name is recorded
///     for audit, but the secret value is NEVER snapshotted. At replay time the engine
///     re-resolves secrets from the current <c>variable</c> table — rotation is a feature.
///   • Everything else → <c>ValuePlain</c> holds the JSON-encoded value, frozen forever.
///     A replay reads from this column; subsequent edits to the source variable have no
///     effect on the historical run's reproduction.
/// </para>
///
/// <para>Per-run row count is small (typically &lt;20). Normalised storage (rather than
/// JSONB blob on workflow_run) keeps the run table itself small for fast run-list
/// pagination, and lets the "which runs used this secret" audit query be a standard
/// btree-indexed WHERE rather than a JSONB containment match.</para>
/// </summary>
public class WorkflowRunVariable
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }

    /// <summary>Scope discriminator: "Wf" | "Team" | "Input" | future scopes. Plain string for forward compat.</summary>
    public string Scope { get; set; } = default!;

    public string Name { get; set; } = default!;

    /// <summary>Value-type discriminator: same string as <c>Messages.Enums.VariableValueType</c>.</summary>
    public string ValueType { get; set; } = default!;

    /// <summary>JSON-encoded value for non-secret types; NULL for Secret rows.</summary>
    public string? ValuePlain { get; set; }

    public DateTimeOffset CapturedAt { get; set; }

    public WorkflowRun Run { get; set; } = default!;
}
