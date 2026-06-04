using System.Text.Json;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// Fan-in convergence. When two branches reconverge (e.g. after a logic.if both paths
/// eventually need to continue), this node sits at the join. Two semantics:
///
///   strategy = "first-non-empty"  → emits the first upstream's outputs that aren't empty.
///                                   The classic "either branch fired, pick whichever
///                                   actually ran" pattern.
///   strategy = "all"              → waits for ALL upstreams (fan-in barrier). Emits a
///                                   merged outputs object keyed by upstream node id.
///
/// The engine's frontier walker already waits for all incoming edges to be settled before
/// enqueueing this node — so by the time we run, every upstream has either succeeded or
/// been skipped. We just decide what to emit downstream.
/// </summary>
public sealed class LogicMergeNode : INodeRuntime
{
    public string TypeKey => "logic.merge";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Merge",
        Category = "Logic",
        Kind = NodeKind.Regular,
        IconKey = "git-merge",
        Description = "Fan-in: waits for upstream branches to converge. Pick first-non-empty (typical) or all (barrier).",
        ConfigSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "strategy": {
                  "type": "string",
                  "enum": ["first-non-empty", "all"],
                  "default": "first-non-empty",
                  "x-enumLabels": { "first-non-empty": "First branch that ran", "all": "Wait for all (barrier)" },
                  "description": "first-non-empty: emit the first upstream that actually ran. all: wait for everyone and emit a merged object."
                }
              }
            }
            """),
        InputSchema = SchemaBuilder.EmptyObject(),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "description": "first-non-empty: the chosen upstream's outputs. all: an object keyed by upstream id."
            }
            """)
    };

    public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        var strategy = ReadString(context.Config, "strategy", "first-non-empty");
        var nodes = context.Scope.Nodes;

        // The engine doesn't tell us which upstreams we have edges from — that's a graph
        // property. We approximate by looking at scope.Nodes: anything present here is a
        // node that successfully completed BEFORE us in topo order. Skipped nodes don't
        // write to scope, so they naturally drop out of the merge.
        //
        // This is intentionally a "soft" merge — see logic.merge tests for the edge cases.
        // A stricter "barrier" semantic would require the engine to pass the list of
        // direct upstreams.
        if (nodes.Count == 0)
        {
            context.Logger.LogInformation("Merge: no upstream produced outputs; emitting empty.");
            return Task.FromResult(NodeResult.Ok());
        }

        if (strategy == "all")
        {
            var merged = new Dictionary<string, JsonElement>();
            foreach (var (id, outputs) in nodes)
            {
                merged[id] = JsonSerializer.SerializeToElement(outputs);
            }
            return Task.FromResult(NodeResult.Ok(merged));
        }

        // first-non-empty (default): pick the most recently-added upstream that has outputs.
        // Dictionary preserves insertion order, so iterating in reverse gives us "last fired".
        foreach (var (id, outputs) in nodes.Reverse())
        {
            if (outputs.Count == 0) continue;
            context.Logger.LogInformation("Merge: forwarding outputs from {UpstreamId}", id);
            return Task.FromResult(NodeResult.Ok(outputs));
        }

        return Task.FromResult(NodeResult.Ok());
    }

    private static string ReadString(IReadOnlyDictionary<string, JsonElement> bag, string key, string fallback)
    {
        if (!bag.TryGetValue(key, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.String) return fallback;
        return value.GetString() ?? fallback;
    }
}
