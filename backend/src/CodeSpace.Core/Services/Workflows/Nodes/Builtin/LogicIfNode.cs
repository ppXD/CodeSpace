using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// Binary branch. One config string (the condition expression) → routes to "true" or
/// "false" output handle based on evaluation. Downstream nodes wire to specific handles
/// via <c>EdgeDefinition.SourceHandle</c>. The engine's frontier walker handles the rest:
/// edges on the unchosen handle become "dead" and their downstream nodes are skipped.
///
/// Grammar for the condition is documented on <see cref="ConditionEvaluator"/>. Examples:
///   {{trigger.number}} &gt; 100
///   {{nodes.fetch.outputs.files}} is_not_empty
///   {{trigger.state}} == "open"
/// </summary>
public sealed class LogicIfNode : INodeRuntime
{
    public string TypeKey => "logic.if";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "If / else",
        Category = "Logic",
        Kind = NodeKind.Regular,
        IconKey = "split",
        Description = "Branches the workflow on a condition. Connect downstream nodes to the 'true' or 'false' handle.",
        ConfigSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "condition": {
                  "type": "string",
                  "description": "Expression to evaluate. Examples: {{trigger.number}} > 100  ·  {{nodes.fetch.outputs.files}} is_not_empty  ·  {{trigger.state}} == \"open\""
                }
              },
              "required": ["condition"]
            }
            """),
        InputSchema = SchemaBuilder.EmptyObject(),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "matched": { "type": "boolean" }
              }
            }
            """),
        Outputs = new[]
        {
            new NodeOutputHandle { Name = "true",  DisplayName = "True",  Description = "Fires when the condition evaluates truthy." },
            new NodeOutputHandle { Name = "false", DisplayName = "False", Description = "Fires when the condition evaluates falsy." }
        }
    };

    public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        var condition = ReadString(context.Config, "condition");
        if (string.IsNullOrWhiteSpace(condition))
            return Task.FromResult(NodeResult.Fail("Config 'condition' is required."));

        var matched = ConditionEvaluator.Evaluate(condition, context.Scope);

        context.Logger.LogInformation("Condition evaluated to {Matched}: {Condition}", matched, condition);

        var outputs = new Dictionary<string, JsonElement>
        {
            ["matched"] = JsonSerializer.SerializeToElement(matched)
        };

        // Route only down the matching branch. The unchosen branch's downstream nodes get
        // Skipped — see WorkflowEngine.ShouldSkip / EnqueueDownstreamWhenReady.
        return Task.FromResult(NodeResult.Route(new[] { matched ? "true" : "false" }, outputs));
    }

    private static string ReadString(IReadOnlyDictionary<string, JsonElement> bag, string key)
    {
        if (!bag.TryGetValue(key, out var value)) return "";
        if (value.ValueKind != JsonValueKind.String) return "";
        return value.GetString() ?? "";
    }
}
