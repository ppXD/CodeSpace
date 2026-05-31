using System.Text.Json;

namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// One node entry in the definition's <c>nodes</c> array. Carries:
///  - identity (<see cref="Id"/>, <see cref="TypeKey"/>)
///  - design-time configuration (<see cref="Config"/>) — static values like prompts,
///    severity thresholds, branch conditions; whatever the node's manifest declared as
///    its <c>ConfigSchema</c>
///  - run-time inputs (<see cref="Inputs"/>) — keys map to the node's manifest
///    <c>InputSchema</c>; values can be literals, <c>{{ref}}</c> template strings, or
///    <c>{"$ref":"…"}</c> JsonRef objects pointing at upstream outputs.
///
/// The split is deliberate: Config is what the workflow author types into the form;
/// Inputs is the wiring between nodes. UI editors render them separately.
/// </summary>
public sealed record NodeDefinition
{
    /// <summary>Stable id, scoped to this definition. Edges reference this. Author-readable (e.g. "fetch_diff", "review_llm").</summary>
    public required string Id { get; init; }

    /// <summary>Manifest type key from <c>INodeRuntime.TypeKey</c> (e.g. "trigger.pr.opened", "llm.complete").</summary>
    public required string TypeKey { get; init; }

    /// <summary>Optional human label shown in the editor. Falls back to manifest <c>DisplayName</c>.</summary>
    public string? Label { get; init; }

    /// <summary>
    /// Container ownership. When set, this node is part of another node's body subgraph — today
    /// only a <c>flow.loop</c> container, whose body runs once per iteration. <c>null</c> for a
    /// top-level node (the default). Null-omitted from the canonical content hash, so existing
    /// definitions are byte-identical until a node is actually placed inside a container.
    /// </summary>
    public string? ParentId { get; init; }

    /// <summary>Per-node static configuration. Shape validated against the node's manifest <c>ConfigSchema</c>.</summary>
    public JsonElement Config { get; init; } = default;

    /// <summary>Per-node dynamic inputs. Each value may be a literal, a <c>{{ref}}</c> template, or a <c>{"$ref":...}</c> object.</summary>
    public JsonElement Inputs { get; init; } = default;

    /// <summary>
    /// Optional retry-on-failure policy. <c>null</c> = run the node exactly once (default).
    /// When set, the engine re-runs the node after a failure per <see cref="RetryPolicy"/>.
    /// Null-omitted from the canonical content hash, so adding it to an existing node only
    /// changes the hash once the operator actually configures a policy.
    /// </summary>
    public RetryPolicy? Retry { get; init; }

    /// <summary>
    /// Editor-only canvas position (pixel coords on the workflow canvas). Optional — when
    /// null, the UI auto-lays out via a simple top-to-bottom DAG walk so older / hand-written
    /// JSON definitions still open cleanly. The engine ignores this field entirely; it only
    /// affects the visual layout in the editor.
    /// </summary>
    public NodePosition? Position { get; init; }

    /// <summary>
    /// Editor-only explicit size (pixels) for a container node (today only a <c>flow.loop</c> box the
    /// user resized by its corner). Optional — null means the editor auto-sizes the container to fit
    /// its body. Like <see cref="Position"/>, the engine ignores these; null-omitted from the content
    /// hash so a workflow's hash is unchanged until a box is actually resized.
    /// </summary>
    public double? Width { get; init; }

    /// <summary>Editor-only explicit height — see <see cref="Width"/>.</summary>
    public double? Height { get; init; }
}

public sealed record NodePosition
{
    public required double X { get; init; }
    public required double Y { get; init; }
}
