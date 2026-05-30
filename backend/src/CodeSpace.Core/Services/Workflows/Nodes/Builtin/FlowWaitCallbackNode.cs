using System.Text.Json;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// Pauses the workflow until an external system POSTs to the run's callback URL. The first
/// execution returns <c>Suspend</c> with a Callback token (the engine mints the correlation
/// token; the run-detail UI surfaces the URL). When something POSTs to
/// <c>/api/workflows/callbacks/{token}</c>, the wait resolves with the request body and the
/// resumed pass surfaces it as the <c>body</c> output — so downstream reads
/// <c>{{nodes.&lt;id&gt;.outputs.body}}</c>.
///
/// The async-external-step primitive: kick off a long-running job elsewhere, park here, and let
/// that job call back when it's done.
/// </summary>
public sealed class FlowWaitCallbackNode : INodeRuntime
{
    public string TypeKey => "flow.wait_callback";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Wait for callback",
        Category = "Logic",
        Kind = NodeKind.Regular,
        IconKey = "webhook",
        Description = "Pauses until an external system POSTs to the run's callback URL. Outputs { body } — the posted payload.",
        ConfigSchema = SchemaBuilder.EmptyObject(),
        InputSchema = SchemaBuilder.EmptyObject(),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": { "body": { "description": "The JSON body the external system posted to the callback URL." } }
            }
            """),
    };

    public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        // Resumed pass: an external POST resolved the wait. Surface the posted body.
        if (context.ResumePayload.HasValue)
        {
            var outputs = new Dictionary<string, JsonElement> { ["body"] = context.ResumePayload.Value };
            return Task.FromResult(NodeResult.Ok(outputs));
        }

        // First pass: park on a Callback wait. The engine mints the token; nothing wakes it until
        // the callback URL is hit.
        return Task.FromResult(NodeResult.Suspend(new SuspensionToken
        {
            Kind = WorkflowWaitKinds.Callback,
            Payload = JsonSerializer.SerializeToElement(new { }),
        }));
    }
}
