using System.Text.Json;
using CodeSpace.Core.Failures;
using CodeSpace.Core.Services.Workflows.Failures;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// Runs ANOTHER workflow as a single step — the "for each PR, run the whole review workflow"
/// case that <c>flow.iterate</c> (a single-expression map) deliberately doesn't cover.
///
/// <para>Mechanism (Phase 3): on its first pass the node SUSPENDS with a <c>Subworkflow</c> token
/// carrying the target + inputs; the engine stages a child run (parent_run_id = this run, the
/// node's <c>inputs</c> as the child's payload) and parks the parent. When the child reaches a
/// terminal state the engine resumes the parent with the child's outcome, which the node maps:
/// child Success → this node's outputs ARE the child's outputs; child Failure → this node fails,
/// composing with the node's retry policy + <c>error</c> branch like any other failure. A child that
/// ended on a provider failure a person can repair (a disconnected credential, a revoked token) reaches
/// the node on the same payload, and the node fails carrying it, so the parent's own record — and the
/// remedy built from it — says what is wrong and which child run it happened in.</para>
///
/// <para>The node itself is pure — it never touches the DB. The engine owns the cross-run
/// orchestration (staging, the suspend wait, the deferred dispatch, the completion → resume hook),
/// so a child that can't be started (missing / cross-team / unpublished / nested too deep) surfaces
/// as a clean node failure.</para>
///
/// Config:
///   workflowId — the child workflow's id (required)
///   version    — pin a specific version (optional; defaults to the child's latest)
/// Inputs:
///   inputs     — object passed as the child's payload; keys map to the child's declared inputs
/// Outputs:
///   (dynamic)  — the child's declared outputs, referenced as {{nodes.&lt;id&gt;.outputs.&lt;key&gt;}}
/// </summary>
public sealed class FlowSubworkflowNode : INodeRuntime
{
    public string TypeKey => "flow.subworkflow";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Run sub-workflow",
        Category = "Logic",
        Kind = NodeKind.Regular,
        CanSuspend = true,
        // D2: rerunnable as a from-node ROOT — the "external run" a re-stage mints is a FRESH child WorkflowRun
        // (parent_run_id = the fork), unique by construction like the agent.run re-stage. Re-executing the node on
        // the fork stages a new child through the SAME first-pass path; the original run's child is untouched. The
        // child's own nodes re-run (its side effects governed within the child), so it is not side-effecting here.
        IsRerunnableWhenSuspendable = true,
        IconKey = "workflow",
        Description = "Runs another workflow as a step. The node's inputs become the child's payload; the child's outputs become this node's outputs.",
        ConfigSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "workflowId": { "type": "string", "description": "The child workflow's id (GUID)." },
                "version":    { "type": "integer", "minimum": 1, "description": "Pin a specific version. Defaults to the child's latest." }
              },
              "required": ["workflowId"]
            }
            """),
        InputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "inputs": { "type": "object", "description": "Payload passed to the child workflow. Keys match the child's declared inputs." }
              }
            }
            """),
        // Outputs are the child's declared outputs — dynamic per target workflow, so untyped here.
        OutputSchema = SchemaBuilder.EmptyObject(),
    };

    public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        // Resumed: the child finished. ResumePayload = { status, outputs, error, failure? } (stamped by the
        // engine's completion hook). Map it onto this node's result.
        if (context.ResumePayload.HasValue)
        {
            var payload = context.ResumePayload.Value;
            if (ReadString(payload, "status") == nameof(WorkflowRunStatus.Success))
                return Task.FromResult(NodeResult.Ok(ReadObjectBag(payload, "outputs")));

            var error = ReadString(payload, "error");
            var sentence = $"Sub-workflow run failed: {(string.IsNullOrEmpty(error) ? "see the child run" : error)}";

            // The sentence is the node's own and stays as it was; the provider failure the child ended on, when it recorded one, rides beside it.
            return Task.FromResult(ReadChildFailure(payload) is { } failure ? NodeResult.Fail(sentence, failure) : NodeResult.Fail(sentence));
        }

        // First pass: validate config + suspend with the spec. The engine stages + dispatches the
        // child and writes the Subworkflow wait.
        var workflowId = ReadString(context.Config, "workflowId");
        if (string.IsNullOrWhiteSpace(workflowId) || !Guid.TryParse(workflowId, out _))
            return Task.FromResult(NodeResult.Fail("Config 'workflowId' must be a workflow id (GUID)."));

        var spec = new Dictionary<string, JsonElement> { ["workflowId"] = JsonSerializer.SerializeToElement(workflowId) };

        if (context.Config.TryGetValue("version", out var version) && version.ValueKind == JsonValueKind.Number)
            spec["version"] = version;

        spec["inputs"] = context.Inputs.TryGetValue("inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Object
            ? inputs.Clone()
            : EmptyObject();

        return Task.FromResult(NodeResult.Suspend(new SuspensionToken
        {
            Kind = WorkflowWaitKinds.Subworkflow,
            Payload = JsonSerializer.SerializeToElement(spec),
        }));
    }

    /// <summary>The provider failure the child's run ended on, as the engine's completion hook read it off the child's own record — absent for a child that failed for any other reason, and for a payload stamped before children carried theirs.</summary>
    private static PersistedFailure? ReadChildFailure(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(PersistedFailureJson.PayloadKey, out var failure) ? ProviderFailureCapture.OfRecord(PersistedFailureJson.ReadElement(failure)) : null;

    private static string ReadString(JsonElement bag, string key) =>
        bag.ValueKind == JsonValueKind.Object && bag.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static string ReadString(IReadOnlyDictionary<string, JsonElement> bag, string key) =>
        bag.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Read an object property into an output bag — the child's outputs object becomes this node's outputs.</summary>
    private static Dictionary<string, JsonElement> ReadObjectBag(JsonElement payload, string key)
    {
        var bag = new Dictionary<string, JsonElement>();
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(key, out var obj) || obj.ValueKind != JsonValueKind.Object)
            return bag;

        foreach (var prop in obj.EnumerateObject()) bag[prop.Name] = prop.Value.Clone();
        return bag;
    }

    private static JsonElement EmptyObject() => JsonDocument.Parse("{}").RootElement.Clone();
}
