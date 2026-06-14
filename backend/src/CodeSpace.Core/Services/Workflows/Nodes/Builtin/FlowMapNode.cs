using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// A fan-out container (the planner+parallel-subagents centerpiece): it binds a collection via its
/// <c>items</c> INPUT and runs its body subgraph (nodes whose <c>ParentId</c> is this node, rooted at a
/// <c>flow.map_start</c>) ONCE PER ELEMENT. The N element-branches run as a bounded-parallel batch;
/// each branch sees its element as <c>{{item}}</c> / <c>{{index}}</c> (the same Iteration scope
/// <c>flow.iterate</c> sets). Each branch's TERMINAL body node output becomes that element's result,
/// and the per-element results reduce into a keyed array — <c>scope.Nodes[mapId] = { &lt;resultKey&gt;:
/// [...ordered by element index...], count, failed }</c> — that a downstream synthesizer reads as
/// <c>{{nodes.&lt;map&gt;.outputs.results}}</c> / <c>results[0]</c> / <c>results.length</c>.
///
/// <para><b>Engine-driven.</b> Like <c>flow.loop</c>, a map is NOT executed through <c>RunAsync</c> —
/// the engine dispatches on <see cref="NodeKind.Map"/> and runs the per-element body sub-walks itself
/// (it needs the graph + ledger, which a node never sees). <see cref="RunAsync"/> therefore throws:
/// reaching it means the dispatch was bypassed, a bug worth surfacing loudly.</para>
///
/// <para><b>PR1 scope.</b> Bodies are SYNCHRONOUS — a node that suspends inside a map branch is not yet
/// supported; the engine fails the branch with a clear message (durable parallel-branch resume is PR2).</para>
/// </summary>
public sealed class FlowMapNode : INodeRuntime
{
    public string TypeKey => "flow.map";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Map",
        Category = "Logic",
        Kind = NodeKind.Map,
        IconKey = "git-fork",
        Description = "Fans a collection out into parallel branches, runs the body once per element, and collects the per-element results into an array a downstream step reads.",
        ConfigSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "maxParallelism": { "type": "integer", "minimum": 1, "maximum": 64, "description": "How many element-branches run at once. Empty inherits the engine default." },
                "errorHandling": { "type": "string", "enum": ["terminate", "continue"], "default": "terminate", "x-enumLabels": { "terminate": "Fail the map if a branch fails", "continue": "Record a failure marker and keep going" } },
                "resultKey": { "type": "string", "default": "results", "description": "Output key the collected array lands under, read as {{nodes.<map>.outputs.<resultKey>}}." }
              }
            }
            """),
        InputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "items": { "type": "array", "description": "The collection to fan out over — usually {{nodes.<planner>.outputs.json.subtasks}}. Empty = zero branches (a no-op)." }
              },
              "required": ["items"]
            }
            """),
        // Dynamic shape: the reduced array's key is author-configurable (resultKey) and each element's
        // result is the branch terminal's own output shape, which varies per workflow. No enumerated
        // `properties` on purpose — that signals the validator to treat {{nodes.<map>.outputs.X}} as
        // dynamic (like flow.loop) and skip the strict output-key check, so referencing the result array
        // (and results[i] / results.length) validates.
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "additionalProperties": true,
              "description": "The reduced array under the configured resultKey (default 'results'), plus 'count' (elements fanned out) and 'failed' (branches that failed under continue-on-error)."
            }
            """)
    };

    public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "flow.map is engine-driven (dispatched by NodeKind.Map); RunAsync should never be invoked directly.");
}
