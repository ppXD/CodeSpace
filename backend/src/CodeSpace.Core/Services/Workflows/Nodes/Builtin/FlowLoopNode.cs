using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// A loop container (mirrors Dify's Loop node): it owns a body subgraph (nodes whose
/// <c>ParentId</c> is this node, rooted at a <c>flow.loop_start</c>) and re-runs it once per
/// iteration until a termination condition is met or the iteration cap is hit. Mutable loop
/// variables thread state across passes; the body sees them as <c>{{loop.&lt;name&gt;}}</c>.
///
/// <para><b>Engine-driven.</b> Unlike a normal node, a loop is NOT executed through
/// <c>RunAsync</c> — the engine dispatches on <see cref="NodeKind.Loop"/> and runs the body
/// sub-walk per iteration itself (it needs the graph + ledger, which a node never sees).
/// <see cref="RunAsync"/> therefore throws: reaching it means the dispatch was bypassed, which
/// is a bug worth surfacing loudly rather than silently no-op'ing past the loop body.</para>
/// </summary>
public sealed class FlowLoopNode : INodeRuntime
{
    public string TypeKey => "flow.loop";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Loop",
        Category = "Logic",
        Kind = NodeKind.Loop,
        IconKey = "repeat",
        Description = "Repeats its body until a termination condition is met or the max-iterations cap is reached. Holds loop variables that carry state across passes.",
        ConfigSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "loopVariables": {
                  "type": "array",
                  "description": "Mutable state carried across iterations, read in the body as {{loop.<name>}}.",
                  "items": {
                    "type": "object",
                    "properties": {
                      "name":   { "type": "string" },
                      "type":   { "type": "string", "enum": ["String", "Number", "Boolean", "Object", "Array"] },
                      "ref":    { "type": "string", "description": "Init from a {{...}} reference (the 'Variable' source)." },
                      "value":  { "description": "Init from a constant literal (the 'Constant' source)." },
                      "update": { "type": "string", "description": "Optional {{...}} re-evaluated at each pass end to set the next value." }
                    },
                    "required": ["name"]
                  }
                },
                "termination": {
                  "type": "object",
                  "description": "Stops the loop when met.",
                  "properties": {
                    "logic":      { "type": "string", "enum": ["and", "or"], "default": "and" },
                    "conditions": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "ref":   { "type": "string", "description": "{{...}} resolved against the loop scope." },
                          "op":    { "type": "string", "enum": ["eq", "neq", "contains", "not_contains", "startsWith", "endsWith", "is_empty", "is_not_empty"] },
                          "value": { "type": "string" }
                        },
                        "required": ["ref", "op"]
                      }
                    }
                  }
                },
                "maxIterations": { "type": "integer", "minimum": 1, "default": 10, "description": "Hard cap — the runaway guard." }
              }
            }
            """),
        InputSchema = SchemaBuilder.Parse("""{ "type": "object", "properties": {} }"""),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "iterations":        { "type": "integer", "description": "How many passes ran." },
                "terminationReason": { "type": "string",  "description": "\"condition\" or \"maxIterations\"." }
              },
              "description": "Plus one property per loop variable, holding its final value."
            }
            """)
    };

    public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "flow.loop is engine-driven (dispatched by NodeKind.Loop); RunAsync should never be invoked directly.");
}
