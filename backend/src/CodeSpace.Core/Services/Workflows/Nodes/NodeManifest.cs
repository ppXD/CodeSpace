using System.Text.Json;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Nodes;

/// <summary>
/// Static, immutable descriptor of a node TYPE. Built once by the node's constructor and
/// returned from <c>INodeRuntime.Manifest</c>. Drives the editor palette, config form
/// rendering on the frontend, and write-time validation against <c>NodeDefinition.Config</c>.
///
/// Schemas are kept as raw <see cref="JsonElement"/> rather than a typed JsonSchema class
/// so plugin authors can use whichever JSON-schema library they like (or hand-author the
/// JSON). The engine validates schemas opaquely — it just checks "does input X match
/// declared shape Y" via a small embedded validator.
/// </summary>
public sealed record NodeManifest
{
    public required string DisplayName { get; init; }

    /// <summary>UI grouping. e.g. "Triggers", "AI", "Git", "Logic". Frontend palette uses this for sectioning.</summary>
    public required string Category { get; init; }

    public required NodeKind Kind { get; init; }

    /// <summary>JSON Schema for the node's static <c>Config</c> object.</summary>
    public required JsonElement ConfigSchema { get; init; }

    /// <summary>JSON Schema for the resolved <c>Inputs</c> bag the engine passes to RunAsync.</summary>
    public required JsonElement InputSchema { get; init; }

    /// <summary>JSON Schema for the <c>Outputs</c> bag the node returns.</summary>
    public required JsonElement OutputSchema { get; init; }

    public string? Description { get; init; }

    /// <summary>Optional icon-name key the frontend can map to a lucide-react icon.</summary>
    public string? IconKey { get; init; }

    /// <summary>
    /// Named output handles. When omitted (null), the node has a single default handle ("out")
    /// and the engine follows every outgoing edge after a successful run. When set, this
    /// declares the named handles a branch / multi-output node emits (e.g. ["true", "false"]
    /// for logic.if). Edges target a specific handle via <c>EdgeDefinition.SourceHandle</c>;
    /// the engine fires only edges matching the routing hints in <c>NodeResult.RoutingHints</c>.
    /// </summary>
    public IReadOnlyList<NodeOutputHandle>? Outputs { get; init; }

    /// <summary>
    /// Opt-in marker for nodes whose <c>RunAsync</c> performs an externally-visible side
    /// effect (posting a PR comment, sending a Slack message, creating a GitHub issue). The
    /// engine USES this signal to refuse auto-resume after an abandoned-worker scenario —
    /// re-executing a side-effecting node on retry produces duplicate effects, which is worse
    /// than failing the run loudly and letting the operator decide.
    ///
    /// <para>Default <c>false</c>: pure-computation nodes (logic.if, logic.merge, flow.iterate,
    /// trigger.*) and read-only fetchers (git.fetch_pr_diff) leave it false. Side-effecting
    /// nodes (git.post_pr_comment, http.request with POST/PUT/DELETE/PATCH, llm.complete when
    /// it has billing implications) set it true.</para>
    /// </summary>
    public bool IsSideEffecting { get; init; }
}

/// <summary>
/// One named output handle on a multi-output node. <see cref="Name"/> is the wire identifier
/// edges reference; <see cref="DisplayName"/> is for the editor only. Examples:
///   logic.if         → [{ Name: "true" }, { Name: "false" }]
///   logic.switch     → [{ Name: "case_a" }, { Name: "case_b" }, { Name: "default" }]
///   git.fetch_pr_diff → null (single default handle, just emits outputs to every downstream)
/// </summary>
public sealed record NodeOutputHandle
{
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
}
