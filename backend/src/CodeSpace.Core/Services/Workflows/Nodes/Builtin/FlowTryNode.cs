using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// A try/catch scope container. It owns a body subgraph (nodes whose <c>ParentId</c> is this node,
/// rooted at a <c>flow.try_start</c>) and runs it ONCE. If every body node succeeds (or handles its
/// own failure via an <c>error</c> edge), the container routes the run down its default output. If
/// ANY body node fails unhandled, the container CATCHES it — the run routes down the <c>catch</c>
/// handle instead of failing, and the container's <c>error</c> output carries the failure — giving a
/// region-level try/catch boundary (vs. wiring an error edge on every node).
///
/// <para><b>Engine-driven.</b> Like <c>flow.loop</c>, a try is NOT executed through <c>RunAsync</c> —
/// the engine dispatches on <see cref="NodeKind.Try"/> and runs the body sub-walk itself (it needs the
/// graph + ledger). <see cref="RunAsync"/> therefore throws: reaching it means dispatch was bypassed,
/// a bug worth surfacing loudly. The body reuses the loop body machinery, so durable suspend, nested
/// loops, and the parallel wave all work inside a try for free.</para>
/// </summary>
public sealed class FlowTryNode : INodeRuntime
{
    public string TypeKey => "flow.try";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Try / catch",
        Category = "Logic",
        Kind = NodeKind.Try,
        IconKey = "shield",
        Description = "Runs its body once; if any step fails unhandled, routes to the 'catch' handle (the failure becomes data) instead of failing the run.",
        ConfigSchema = SchemaBuilder.Parse("""{ "type": "object", "properties": {} }"""),
        InputSchema = SchemaBuilder.Parse("""{ "type": "object", "properties": {} }"""),
        // On catch, the container exposes the body's failure as {{nodes.<try>.outputs.error.message}}.
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "additionalProperties": true,
              "description": "Empty on success; on catch carries 'error' { message, node } describing the body failure."
            }
            """),
        // The default (null) output is the success path; 'catch' fires when the body failed unhandled.
        Outputs = new[]
        {
            new NodeOutputHandle { Name = WorkflowHandles.Catch, DisplayName = "Catch", Description = "Fires when a body node fails unhandled — the failure routed here instead of failing the run." }
        }
    };

    public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "flow.try is engine-driven (dispatched by NodeKind.Try); RunAsync should never be invoked directly.");
}
