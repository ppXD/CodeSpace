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
        DisplayName = "Merge branches",
        Category = "Logic",
        Kind = NodeKind.Regular,
        IconKey = "git-merge",
        Description = "Waits for parallel branches to rejoin here. Choose the first branch that produced output (typical), or wait for all of them.",
        ConfigSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "strategy": {
                  "type": "string",
                  "enum": ["first-non-empty", "all"],
                  "default": "first-non-empty",
                  "x-control": "radioCards",
                  "x-enumLabels": { "first-non-empty": "First branch that ran", "all": "Wait for all (barrier)" },
                  "x-optionConsequence": { "first-non-empty": "Emits the first upstream branch that actually ran; the others are ignored.", "all": "Waits for every upstream branch, then emits one merged object." },
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

        // Join only our DIRECT graph predecessors, not every node that happened to complete earlier in the run.
        // The engine hands us those predecessor ids (context.IncomingNodeIds — the source of every edge into us);
        // we intersect them with scope.Nodes (which holds only nodes that actually completed, so skipped branches
        // drop out for free). Insertion order in scope.Nodes is completion order, and Where preserves it — so
        // first-non-empty's Reverse() still means "the last predecessor that fired".
        var incoming = context.IncomingNodeIds.ToHashSet();
        var nodes = context.Scope.Nodes.Where(kv => incoming.Contains(kv.Key)).ToList();

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

        // first-non-empty (default): pick the most recently-completed predecessor that has outputs.
        // scope.Nodes preserves completion order, so iterating our filtered list in reverse gives "last fired".
        foreach (var (id, outputs) in Enumerable.Reverse(nodes))
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
