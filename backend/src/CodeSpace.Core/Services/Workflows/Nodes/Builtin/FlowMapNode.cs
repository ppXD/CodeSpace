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
/// <para><b>Suspending bodies.</b> A map branch body may SUSPEND (an approval, an <c>agent.run</c> run, a
/// sub-workflow, a timer): each branch parks its own durable wait under <c>&lt;mapId&gt;#&lt;i&gt;</c>, the
/// map suspends until ALL branches resolve, and each branch resumes EXACTLY ONCE from its own wait (a settled
/// sibling is replayed from the ledger, never re-run). Nesting is supported — a map (or a loop / try) inside a
/// map branch parks under the composed iteration key <c>&lt;outerMapId&gt;#&lt;i&gt;/&lt;innerId&gt;#&lt;j&gt;</c>
/// and resumes the same way.</para>
///
/// <para><b>Nested-map iteration scope: the inner element SHADOWS the outer (by design).</b> Because a map
/// branch reads its element through the FIXED names <c>{{item}}</c> / <c>{{index}}</c>, a map nested inside
/// another map's branch sees ITS OWN element as <c>{{item}}</c> / <c>{{index}}</c> — the OUTER map's element
/// is NOT in scope by default. This is intentional, not a gap: inheriting the outer would collide on the very
/// same keys (the inner would win anyway), so the contract is clean shadowing. To use the outer element inside
/// an inner branch, pass it down explicitly — bind it into the inner map's <c>items</c> shape, or read a
/// pre-map node's output via <c>{{nodes.&lt;id&gt;.outputs.X}}</c> (the branch scope preserves the outer Nodes
/// bag). Contrast <c>flow.loop</c>, whose NAMED <c>{{loop.&lt;var&gt;}}</c> scope lives in a separate slot and
/// so coexists with an enclosing iterate's <c>{{item}}</c> — which is why a loop body INHERITS the enclosing
/// iteration while a map body shadows it. (Engine: <c>WorkflowEngine.BuildMapBranchScope</c> vs
/// <c>BuildLoopScope</c>.)</para>
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
                "errorHandling": { "type": "string", "enum": ["terminate", "continue"], "default": "terminate", "title": "If a branch fails", "x-control": "radioCards", "x-enumLabels": { "terminate": "Fail the map if a branch fails", "continue": "Keep going, mark failures" }, "x-optionConsequence": { "terminate": "If any element-branch fails, the whole map fails and emits no results.", "continue": "A failed branch records a failure marker; the rest keep running and the map still succeeds." } },
                "resultKey": { "type": "string", "default": "results", "description": "Output key the collected array lands under, read as {{nodes.<map>.outputs.<resultKey>}}." }
              }
            }
            """),
        InputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "items": { "type": "array", "description": "The collection to fan out over — usually {{nodes.<planner>.outputs.json.subtasks}}. Required: bind a non-empty collection. A missing or empty binding is a validation error." }
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
