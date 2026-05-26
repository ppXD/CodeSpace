namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// The on-disk + on-DB shape of a workflow. Serialized to <c>workflow.definition_jsonb</c>
/// and rolled into <c>workflow_version</c> on save. The exact field names here are part of
/// the engine's contract — renaming a property breaks every existing workflow JSON in the
/// database, including operator-authored templates committed to git. See the SchemaVersion
/// note for the freeze policy.
///
/// Adding NEW optional properties is non-breaking and does not bump <see cref="SchemaVersion"/>;
/// a node-type-extensible system handles new behaviors via new node types, not new top-level
/// fields. The current <see cref="SchemaVersion"/> is 1 and is intended to stay 1 forever
/// — a future v2 would only happen if we materially change the graph data model itself
/// (e.g. introducing typed ports, named output groups, sub-workflows).
/// </summary>
public sealed record WorkflowDefinition
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Frozen integer. Validator rejects any other value at write time so a forward-incompatible
    /// definition can't be stored. A v2 migration would re-write existing rows.
    /// </summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>All nodes in the graph, including the (single) trigger and (≥1) terminals.</summary>
    public required IReadOnlyList<NodeDefinition> Nodes { get; init; }

    /// <summary>Directed edges between nodes. May carry a <c>Condition</c> string for branch nodes.</summary>
    public required IReadOnlyList<EdgeDefinition> Edges { get; init; }

    /// <summary>
    /// Per-run parameters the workflow accepts. Supplied at trigger time:
    ///  - Manual run: editor renders a form from these schemas
    ///  - HTTP trigger: body keys map by name
    ///  - Event trigger: trigger payload maps by name
    /// Exposed under <c>{{input.&lt;name&gt;}}</c>. Required inputs without a value (or default)
    /// cause the run to fail at start with a validation error.
    /// </summary>
    public IReadOnlyList<WorkflowVariable> Inputs { get; init; } = Array.Empty<WorkflowVariable>();

    /// <summary>
    /// Declared workflow outputs — what callers of the workflow see after Success. Filled by
    /// the Terminal node's Inputs map at run time: each Output's Name maps to a value the
    /// Terminal supplies via <c>{{ref}}</c>. Persisted to <c>workflow_run.OutputsJson</c>.
    /// </summary>
    public IReadOnlyList<WorkflowVariable> Outputs { get; init; } = Array.Empty<WorkflowVariable>();

    // The workflow definition is pure structure (graph + IO contract). Per-team and
    // per-workflow operational variable values live in the `variable` table (scope=Workflow
    // / scope=Team) and are accessed by name at run time, not declared in the definition JSON.
}
