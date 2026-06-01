using System.Text.Json;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// Pauses the workflow until a person acts on an interactive chat affordance (a card button posted by
/// <c>chat.post_message</c>). The first execution parks on an Action wait keyed by the <c>token</c>
/// input — the SAME token the card carries — so a button click resolves exactly this wait (see
/// <c>IWorkflowResumeService.ResumeByActionTokenAsync</c>). The resumed pass surfaces the decision as
/// outputs <c>{ action, by, comment }</c>; downstream branches on <c>{{nodes.&lt;id&gt;.outputs.action}}</c>
/// with a logic.if. The token is minted + output by <c>chat.post_message</c> and wired into this input.
///
/// Generic sibling of <see cref="FlowWaitApprovalNode"/> / <see cref="FlowWaitCallbackNode"/>; the only
/// new ingredient is the caller-supplied correlation token, which couples the card to the wait.
/// </summary>
public sealed class FlowWaitActionNode : INodeRuntime
{
    public string TypeKey => "flow.wait_action";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Wait for action",
        Category = "Logic",
        Kind = NodeKind.Regular,
        IconKey = "mouse-pointer-click",
        Description = "Pauses until someone clicks a chat card button. Outputs { action, by, comment } — branch on 'action'. Wire its 'token' input from the chat.post_message that posted the card.",
        ConfigSchema = SchemaBuilder.EmptyObject(),
        InputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "token": { "type": "string", "minLength": 1, "description": "The card's action token — wire from chat.post_message's `token` output." }
              },
              "required": ["token"]
            }
            """),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "action":  { "type": "string" },
                "by":      { "type": "string" },
                "comment": { "type": "string" }
              }
            }
            """),
    };

    public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        // Resumed pass: a button click resolved the wait — surface the decision as outputs.
        if (context.ResumePayload.HasValue)
        {
            var decision = context.ResumePayload.Value;
            var outputs = new Dictionary<string, JsonElement>
            {
                ["action"]  = ReadOr(decision, "action", JsonSerializer.SerializeToElement("")),
                ["by"]      = ReadOr(decision, "by", JsonSerializer.SerializeToElement("")),
                ["comment"] = ReadOr(decision, "comment", JsonSerializer.SerializeToElement("")),
            };
            return Task.FromResult(NodeResult.Ok(outputs));
        }

        // First pass: park on an Action wait keyed by the supplied token (the card carries the same one).
        if (!TryReadToken(context, out var token))
            return Task.FromResult(NodeResult.Fail("Input 'token' missing or empty — wire it from the chat.post_message that posted the card."));

        var payload = JsonSerializer.SerializeToElement(new { token });
        return Task.FromResult(NodeResult.Suspend(new SuspensionToken { Kind = WorkflowWaitKinds.Action, CorrelationToken = token, Payload = payload }));
    }

    private static bool TryReadToken(NodeRunContext context, out string token)
    {
        token = "";
        if (!context.Inputs.TryGetValue("token", out var v) || v.ValueKind != JsonValueKind.String) return false;
        token = v.GetString() ?? "";
        return token.Length > 0;
    }

    private static JsonElement ReadOr(JsonElement obj, string key, JsonElement fallback) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) ? v.Clone() : fallback;
}
