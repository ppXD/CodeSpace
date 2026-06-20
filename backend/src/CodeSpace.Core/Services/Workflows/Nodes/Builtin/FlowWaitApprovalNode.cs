using System.Text.Json;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// Pauses the workflow until a human approves or rejects. The first execution returns
/// <c>Suspend</c> with an Approval token (the run parks as Suspended with no timer); a person
/// then POSTs the decision to the run's resume endpoint, which resolves the wait with a
/// <c>{ approved, comment, by }</c> payload and re-dispatches. The resumed pass surfaces that
/// decision as outputs, so downstream nodes branch on
/// <c>{{nodes.&lt;id&gt;.outputs.approved}}</c> via a logic.if.
/// </summary>
public sealed class FlowWaitApprovalNode : INodeRuntime
{
    public string TypeKey => "flow.wait_approval";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Wait for approval",
        Category = "Logic",
        Kind = NodeKind.Regular,
        CanSuspend = true,
        IconKey = "user-check",
        Description = "Pauses until a human approves or rejects. Outputs { approved, comment, by } — branch on 'approved' with an If/else.",
        ConfigSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "prompt": { "type": "string", "description": "The question shown to the approver (e.g. 'Deploy to production?')." }
              }
            }
            """),
        InputSchema = SchemaBuilder.EmptyObject(),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "approved": { "type": "boolean" },
                "comment":  { "type": "string" },
                "by":       { "type": "string" }
              }
            }
            """),
    };

    public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        // Resumed pass: a human resolved the wait. Surface the decision as outputs.
        if (context.ResumePayload.HasValue)
        {
            var decision = context.ResumePayload.Value;
            var outputs = new Dictionary<string, JsonElement>
            {
                ["approved"] = ReadOr(decision, "approved", JsonSerializer.SerializeToElement(false)),
                ["comment"]  = ReadOr(decision, "comment", JsonSerializer.SerializeToElement("")),
                ["by"]       = ReadOr(decision, "by", JsonSerializer.SerializeToElement("")),
            };
            return Task.FromResult(NodeResult.Ok(outputs));
        }

        // First pass: park the run on an Approval wait (no timer — wakes only on the decision). The prompt is persisted
        // to the wait payload + rendered VERBATIM on the run-detail surface (a HUMAN surface outliving the run), so read
        // it from the REDACTED config — a {{team.SECRET}} in the approval prompt becomes a "[REDACTED: path]" marker, not
        // plaintext (same invariant FlowDecisionNode upholds; falls back to Config only off the engine path).
        var prompt = ReadString(context.RedactedConfig ?? context.Config, "prompt");
        var payload = JsonSerializer.SerializeToElement(new { prompt });
        return Task.FromResult(NodeResult.Suspend(new SuspensionToken { Kind = WorkflowWaitKinds.Approval, Payload = payload }));
    }

    private static JsonElement ReadOr(JsonElement obj, string key, JsonElement fallback) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) ? v.Clone() : fallback;

    private static string ReadString(IReadOnlyDictionary<string, JsonElement> bag, string key) =>
        bag.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
