namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// Directed edge from one node to another. The optional <see cref="Condition"/> turns an
/// edge into a branch — the engine only follows the edge when the condition string
/// (resolved against the same scope variables as inputs) evaluates truthy.
///
/// Conditions are intentionally minimal: a <c>{{ref}}</c>-able expression compared
/// against literals via JavaScript-style truthy. Complex conditions belong on a "logic.if"
/// node, not on the edge — keeps the edge model simple.
/// </summary>
public sealed record EdgeDefinition
{
    /// <summary>Source node id. Must match a <see cref="NodeDefinition.Id"/>.</summary>
    public required string From { get; init; }

    /// <summary>Target node id. Must match a <see cref="NodeDefinition.Id"/>.</summary>
    public required string To { get; init; }

    /// <summary>
    /// Which output handle on the source node this edge originates from. Null = the node's
    /// default single output ("out"). Branch nodes declare multiple output handles in their
    /// manifest ("true"/"false" for logic.if, case names for logic.switch) — the edge has
    /// to say which one it represents so the engine knows whether to fire it.
    /// </summary>
    public string? SourceHandle { get; init; }

    /// <summary>
    /// Which input handle on the target node this edge feeds into. Reserved for future
    /// multi-input nodes (e.g. a "compare" node with "left"/"right" handles). Null =
    /// the node's default single input ("in").
    /// </summary>
    public string? TargetHandle { get; init; }

    /// <summary>
    /// Optional. When set, evaluated truthy/falsy at run time; the engine follows the edge
    /// only if truthy. When null, the edge is unconditional. Largely superseded by
    /// SourceHandle for branch nodes — prefer routing-by-handle over edge conditions when
    /// the source is a branch node. Edge conditions are still useful for "advanced" guards
    /// on simple-output nodes.
    /// </summary>
    public string? Condition { get; init; }
}
