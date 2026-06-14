using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Tools;

/// <summary>
/// Projects a workflow <see cref="INodeRuntime"/> onto the <see cref="IAgentTool"/> fabric — the bridge that
/// turns CodeSpace's SCM/workflow nodes (git.open_pr, agent.run_command, fetch-diff/checks, …) into
/// model-callable tools for both the MCP server and the future native loop, with ONE definition. The node's
/// manifest IS the tool schema; its <see cref="NodeManifest.IsSideEffecting"/> flag drives the fail-closed risk
/// declarations (side-effecting → destructive → approval-gated; read-only → concurrency-safe, no approval).
///
/// <para>Only synchronous nodes are tool-callable: a node that SUSPENDS for an async wait (e.g. agent.code)
/// returns a typed error rather than silently parking — a tool call must produce a concrete result. The node
/// runs against a minimal synthetic context (the tool input as its inputs, no upstream scope, no-op
/// observability); the agent loop / MCP layer owns its own auditing around the call.</para>
/// </summary>
public sealed class NodeAgentTool : IAgentTool
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly INodeRuntime _node;
    private readonly ILogger _logger;

    public NodeAgentTool(INodeRuntime node, ILogger logger)
    {
        _node = node;
        _logger = logger;
    }

    public string Kind => _node.TypeKey;
    public string Description => _node.Manifest.Description ?? _node.Manifest.DisplayName;
    public JsonElement InputSchema => _node.Manifest.InputSchema;
    public JsonElement OutputSchema => _node.Manifest.OutputSchema;

    // Fail-closed via the node's side-effect flag: a read-only node is safe + needs no approval; a side-effecting
    // node is destructive → gated by default (the autonomy tier decides whether to actually ask).
    public bool IsReadOnly => !_node.Manifest.IsSideEffecting;
    public bool IsConcurrencySafe => !_node.Manifest.IsSideEffecting;
    public bool IsDestructive => _node.Manifest.IsSideEffecting;

    public AgentToolValidation ValidateInput(JsonElement input) =>
        input.ValueKind == JsonValueKind.Object ? AgentToolValidation.Valid : AgentToolValidation.Invalid("Tool input must be a JSON object.");

    public async Task<AgentToolResult> CallAsync(AgentToolCall call, CancellationToken cancellationToken)
    {
        var inputs = call.Input.ValueKind == JsonValueKind.Object
            ? call.Input.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
            : new Dictionary<string, JsonElement>();

        // Strip the act-as-user actor key from model-controlled input. ActsAsUser ("act as this CodeSpace user's
        // own linked provider identity", Model B) is an ENGINE-RESPOND-PATH feature: it is only safe because
        // WorkflowResumeService runs ActorIdentityRequirementGate first, proving the AUTHENTICATED responder IS
        // that user before the node spends their stored OAuth token. No such gate runs on this synthetic tool path,
        // so honoring a model-supplied actor id would let the model author a PR — or forge an APPROVE review — as
        // ANY team member who linked an identity (per-user impersonation). Dropping it forces actAsUserId → null in
        // the node, so a tool-invoked write acts as the repo CONNECTION credential, never a specific user. Generic
        // via the manifest, so every present + future act-as-user node is covered without naming a key here.
        if (_node.Manifest.ActsAsUser is { } actsAsUser) inputs.Remove(actsAsUser.ActorInputKey);

        // Stamp the run's team onto the synthetic scope's sys.team_id so repo-touching nodes resolve within it.
        // The Guid is serialized as a JSON STRING element, byte-for-byte like WorkflowEngine.BuildSysScope, so
        // NodeScopeReader.TryReadTeamId (which requires ValueKind==String + Guid.TryParse) reads it back.
        // A null team leaves Sys empty → no team_id → repo nodes fail closed (today's behavior).
        var sys = call.TeamId is { } teamId
            ? new Dictionary<string, JsonElement> { [SystemScopeKeys.TeamId] = JsonSerializer.SerializeToElement(teamId) }
            : new Dictionary<string, JsonElement>();

        var context = new NodeRunContext
        {
            Inputs = inputs,
            Config = new Dictionary<string, JsonElement>(),
            RawInputs = call.Input,
            RawConfig = EmptyObject,
            Scope = new NodeRunScope { Trigger = new Dictionary<string, JsonElement>(), Sys = sys },
            Logger = _logger,
            Observability = NodeObservability.NoOp,
        };

        var result = await _node.RunAsync(context, cancellationToken).ConfigureAwait(false);

        return result.Status switch
        {
            NodeStatus.Success => OkFromOutputs(result.Outputs),
            NodeStatus.Failure => AgentToolResult.Fail(result.Error ?? $"Tool '{_node.TypeKey}' failed."),
            NodeStatus.Suspended => AgentToolResult.Fail($"Node '{_node.TypeKey}' suspends for an async wait and can't run as a synchronous tool."),
            _ => AgentToolResult.Fail($"Node '{_node.TypeKey}' produced no result ({result.Status})."),
        };
    }

    private static AgentToolResult OkFromOutputs(IReadOnlyDictionary<string, JsonElement> outputs)
    {
        var json = JsonSerializer.SerializeToElement(outputs);
        return AgentToolResult.Ok(json, json.GetRawText().Length);
    }
}
