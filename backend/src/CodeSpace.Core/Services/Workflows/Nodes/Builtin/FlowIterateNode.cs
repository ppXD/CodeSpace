using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// "For each item in array, evaluate a template, collect results into a new array." A pure
/// mapping primitive — does NOT execute a subgraph per item. That's Dify's iteration-as-
/// subgraph pattern; we'll ship it as a sibling node <c>flow.subworkflow</c> in a follow-up.
/// The split keeps the two cases honest:
///
///   flow.iterate     — "I have N strings; build N comment bodies and post them all."
///                      The transformation is a single expression.
///   flow.subworkflow — "I have N PRs; run the AI Code Review workflow on each."
///                      The transformation is an entire subgraph.
///
/// 80% of "I have a list, do X to each" reduces to flow.iterate. The other 20% (true
/// fan-out parallelism with multi-node per-item logic) is what flow.subworkflow will cover.
///
/// Inputs:
///   items      — the source array (JSON array of any value type)
///   itemAs     — variable name to expose each element under (default: "item")
/// Config:
///   template   — string template evaluated per-iteration. Has access to:
///                  {{item}}        — the current element (whole value)
///                  {{item.x}}      — property access if element is an object
///                  {{index}}       — 0-based position
///                  + all normal scope refs (trigger.*, nodes.*, env.*)
/// Outputs:
///   results    — array of evaluated values
///   count      — number of items processed
/// </summary>
public sealed class FlowIterateNode : INodeRuntime
{
    public string TypeKey => "flow.iterate";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "For each (map)",
        Category = "Logic",
        Kind = NodeKind.Regular,
        IconKey = "repeat",
        Description = "Maps an array through a template expression. Use flow.subworkflow when each iteration needs a multi-node subgraph.",
        ConfigSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "template": {
                  "type": "string",
                  "x-long": true,
                  "description": "Evaluated per item. Access via {{item}}, {{item.field}}, {{index}}."
                }
              },
              "required": ["template"]
            }
            """),
        InputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "items":  { "type": "array",  "description": "Array to iterate over." },
                "itemAs": { "type": "string", "title": "Item variable name", "description": "Variable name for the current element. Defaults to 'item'." }
              },
              "required": ["items"]
            }
            """),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "results": { "type": "array",   "description": "Per-iteration template results." },
                "count":   { "type": "integer", "description": "Number of items processed." }
              }
            }
            """)
    };

    public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        if (!context.Inputs.TryGetValue("items", out var itemsElement) || itemsElement.ValueKind != JsonValueKind.Array)
            return Task.FromResult(NodeResult.Fail("Input 'items' is required and must be an array."));

        // CRITICAL: pull the template from RAW config, not pre-resolved Config. The engine
        // resolves Config against the OUTER scope before calling us, which means {{item}} /
        // {{index}} in the template would already be empty by the time we got here. We need
        // the original template-with-placeholders so we can re-resolve it per iteration with
        // a scope that has item + index populated.
        var rawTemplate = ReadRawString(context.RawConfig, "template");
        if (string.IsNullOrEmpty(rawTemplate))
            return Task.FromResult(NodeResult.Fail("Config 'template' is required."));

        var itemAs = ReadString(context.Inputs, "itemAs");
        if (string.IsNullOrEmpty(itemAs)) itemAs = "item";

        var results = new List<JsonElement>();
        var index = 0;

        foreach (var item in itemsElement.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var iterationScope = BuildIterationScope(context.Scope, itemAs, item, index);

            var templateElement = JsonSerializer.SerializeToElement(rawTemplate);
            var resolved = VariableResolver.Resolve(templateElement, iterationScope);
            results.Add(resolved.Clone());

            index++;
        }

        context.Logger.LogInformation("flow.iterate produced {Count} result(s) from template {Template}", results.Count, rawTemplate);

        var outputs = new Dictionary<string, JsonElement>
        {
            ["results"] = JsonSerializer.SerializeToElement(results),
            ["count"] = JsonSerializer.SerializeToElement(results.Count)
        };

        return Task.FromResult(NodeResult.Ok(outputs));
    }

    // Exposed via InternalsVisibleTo for unit tests that pin the scope-inheritance
    // contract (Projects + SecretPaths must propagate so {{project.X.Y}} resolves
    // inside the iteration body AND the Terminal secret-leak guard's SecretPaths
    // set still recognises tainted refs reached through iterated templates).
    internal static NodeRunScope BuildIterationScope(NodeRunScope outer, string itemAs, JsonElement item, int index)
    {
        // Put per-iteration vars in the dedicated Iteration slot — VariableResolver reads
        // bare {{item}} / {{index}} through it without polluting trigger.* (which would be
        // confusing to operators) or nodes.* (which is read-only output bookkeeping).
        // The outer scope is left fully intact.
        var iteration = new Dictionary<string, JsonElement>
        {
            [itemAs] = item.Clone(),
            ["index"] = JsonSerializer.SerializeToElement(index)
        };

        // Per-iteration scope inherits every read-side bag from the outer scope so that
        // {{project.<slug>.X}}, {{team.X}}, {{wf.X}}, {{input.X}}, {{sys.X}}, and
        // {{trigger.X}} all keep working inside the iterated subgraph — and so the
        // Terminal secret-leak guard's SecretPaths set carries over (an iterated template
        // referencing {{team.API_KEY}} must still be blocked from a Terminal payload).
        // Previously the constructor listed each field explicitly and silently dropped
        // Projects + SecretPaths because they were added to NodeRunScope after this
        // builder was written; passing them through here closes that gap. The Nodes bag
        // is still copied entry-by-entry below because it's a mutable IDictionary that
        // accumulates outputs as the iteration body executes.
        var scope = new NodeRunScope
        {
            Trigger = outer.Trigger,
            Team = outer.Team,
            Wf = outer.Wf,
            Input = outer.Input,
            Sys = outer.Sys,
            Projects = outer.Projects,
            SecretPaths = outer.SecretPaths,
            Iteration = iteration
        };

        foreach (var (k, v) in outer.Nodes) scope.Nodes[k] = v;
        return scope;
    }

    private static string ReadString(IReadOnlyDictionary<string, JsonElement> bag, string key)
    {
        if (!bag.TryGetValue(key, out var value)) return "";
        if (value.ValueKind != JsonValueKind.String) return "";
        return value.GetString() ?? "";
    }

    private static string ReadRawString(JsonElement bag, string key)
    {
        if (bag.ValueKind != JsonValueKind.Object) return "";
        if (!bag.TryGetProperty(key, out var value)) return "";
        if (value.ValueKind != JsonValueKind.String) return "";
        return value.GetString() ?? "";
    }
}
