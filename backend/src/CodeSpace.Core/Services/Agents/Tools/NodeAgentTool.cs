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
/// <para>Only synchronous nodes are tool-callable: a node that SUSPENDS for an async wait (e.g. agent.run)
/// returns a typed error rather than silently parking — a tool call must produce a concrete result. The node
/// runs against a minimal synthetic context (the tool input as its inputs, no upstream scope, no-op
/// observability, the calling run's <see cref="AgentToolCall.CallerPosture"/>); the agent loop / MCP layer owns its own
/// auditing around the call.</para>
///
/// <para>A node that declares a repository input (<see cref="NodeManifest.RepositoryInput"/>) is held, when a run calls
/// it, to the repositories that run is bound to — once, here, for every such node: a write only to one bound writable,
/// a ref of read-only context only at its bound or default branch, and a write also meets the repository's publish
/// policy (<see cref="IAgentRepositoryPolicy"/>). The same check answers <see cref="RefusalAsync"/>, so the MCP layer
/// refuses such a call before it parks it for a human's approval, and runs again when the call executes.</para>
///
/// <para>A call names only the inputs the node's schema declares (the advertised schema says so with
/// <c>additionalProperties: false</c>): an undeclared key reaches no node, so it can neither carry hidden meaning nor make
/// a rejected call look new. A call parked for approval is previewed from the manifest by <see cref="IAgentToolPreviewer"/>.</para>
/// </summary>
public sealed class NodeAgentTool : IAgentTool
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly INodeRuntime _node;
    private readonly INodeInvocationExecutor _invocations;
    private readonly IAgentRepositoryPolicy _repositoryPolicy;
    private readonly IAgentToolPreviewer _previewer;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<string> _declaredInputs;

    public NodeAgentTool(INodeRuntime node, INodeInvocationExecutor invocations, IAgentRepositoryPolicy repositoryPolicy, IAgentToolPreviewer previewer, ILogger logger)
    {
        _node = node;
        _invocations = invocations;
        _repositoryPolicy = repositoryPolicy;
        _previewer = previewer;
        _logger = logger;
        _declaredInputs = AgentToolInputs.Declared(node.Manifest.InputSchema);
        InputSchema = ClosedSchema(node.Manifest.InputSchema);
    }

    public string Kind => _node.TypeKey;
    public string Description => _node.Manifest.Description ?? _node.Manifest.DisplayName;

    /// <summary>The node's input schema, closed to the keys it declares — what <see cref="ValidateInput"/> enforces.</summary>
    public JsonElement InputSchema { get; }

    public JsonElement OutputSchema => _node.Manifest.OutputSchema;

    // Fail-closed via the node's side-effect flag: a read-only node is safe + needs no approval; a side-effecting
    // node is destructive → gated by default (the autonomy tier decides whether to actually ask).
    public bool IsReadOnly => !_node.Manifest.IsSideEffecting;
    public bool IsConcurrencySafe => !_node.Manifest.IsSideEffecting;
    public bool IsDestructive => _node.Manifest.IsSideEffecting;

    // An irreversible node (git.merge_pr) declares this true → the tool can never auto-run; the gate escalates
    // Unleashed's Allow → RequireApproval. Default false leaves every reversible write Allow-able at Unleashed.
    public bool AlwaysRequiresApproval => _node.Manifest.AlwaysRequiresApproval;

    public AgentToolValidation ValidateInput(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return AgentToolValidation.Invalid("Tool input must be a JSON object.");

        var undeclared = input.EnumerateObject().Select(property => property.Name).Where(name => !_declaredInputs.Contains(name, StringComparer.Ordinal)).ToList();

        return undeclared.Count == 0 ? AgentToolValidation.Valid : AgentToolValidation.Invalid(UndeclaredInputs(undeclared));
    }

    public Task<string?> RefusalAsync(AgentToolCall call, CancellationToken cancellationToken) => RepositoryRefusalAsync(call, ToolInputs(call), cancellationToken);

    public Task<ToolCallPreview> PreviewAsync(AgentToolCall call, CancellationToken cancellationToken) => _previewer.PreviewAsync(_node.Manifest, call, ToolInputs(call), cancellationToken);

    public async Task<AgentToolResult> CallAsync(AgentToolCall call, CancellationToken cancellationToken)
    {
        var inputs = ToolInputs(call);

        if (await RepositoryRefusalAsync(call, inputs, cancellationToken).ConfigureAwait(false) is { } refusal) return AgentToolResult.Fail(refusal);

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
            CallerPosture = call.CallerPosture,
        };

        var result = await _invocations.ExecuteAsync(new NodeInvocation(_node.TypeKey, context), cancellationToken).ConfigureAwait(false);

        return result.Status switch
        {
            NodeStatus.Success => OkFromOutputs(result.Outputs),
            NodeStatus.Failure => AgentToolResult.Fail(result.Error ?? $"Tool '{_node.TypeKey}' failed."),
            NodeStatus.Suspended => AgentToolResult.Fail($"Node '{_node.TypeKey}' suspends for an async wait and can't run as a synchronous tool."),
            _ => AgentToolResult.Fail($"Node '{_node.TypeKey}' produced no result ({result.Status})."),
        };
    }

    /// <summary>
    /// The inputs the node is given: the model's, without the act-as-user actor key. ActsAsUser ("act as this CodeSpace
    /// user's own linked provider identity", Model B) is an ENGINE-RESPOND-PATH feature: it is only safe because
    /// WorkflowResumeService runs ActorIdentityRequirementGate first, proving the AUTHENTICATED responder IS that user
    /// before the node spends their stored OAuth token. No such gate runs on this synthetic tool path, so honoring a
    /// model-supplied actor id would let the model author a PR — or forge an APPROVE review — as ANY team member who
    /// linked an identity (per-user impersonation). Dropping it forces actAsUserId → null in the node, so a tool-invoked
    /// write acts as the repo CONNECTION credential, never a specific user — and the approval card, built from the same
    /// inputs, never shows an identity the call will not act as. Generic via the manifest, so every present + future
    /// act-as-user node is covered without naming a key here.
    /// </summary>
    private Dictionary<string, JsonElement> ToolInputs(AgentToolCall call)
    {
        var inputs = call.Input.ValueKind == JsonValueKind.Object
            ? call.Input.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
            : new Dictionary<string, JsonElement>();

        if (_node.Manifest.ActsAsUser is { } actsAsUser) inputs.Remove(actsAsUser.ActorInputKey);

        return inputs;
    }

    private string UndeclaredInputs(IReadOnlyList<string> undeclared) =>
        $"Tool '{_node.TypeKey}' does not take {string.Join(", ", undeclared.Select(name => $"'{name}'"))}. It takes only: {(_declaredInputs.Count == 0 ? "no inputs" : string.Join(", ", _declaredInputs))}.";

    /// <summary><paramref name="schema"/> with <c>additionalProperties: false</c>, so the model is told up front what <see cref="ValidateInput"/> refuses. A schema that is not an object is advertised as it is.</summary>
    private static JsonElement ClosedSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) return schema;

        var closed = schema.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
        closed["additionalProperties"] = JsonSerializer.SerializeToElement(false);

        return JsonSerializer.SerializeToElement(closed);
    }

    /// <summary>
    /// The calling run's hold on the repository the model named in the node's declared repository input. A repository the
    /// run is not bound to — in its team or not, existing or not — is refused as not found before the node runs; a write
    /// to read-only context is refused on the binding's own access; a ref of read-only context and a write to a bound
    /// repository meet the repository's policy. Null when the call may proceed: no calling run (a workflow node,
    /// unchanged), a node that names no repository, or no repository id a node would act on.
    /// </summary>
    private async Task<string?> RepositoryRefusalAsync(AgentToolCall call, IReadOnlyDictionary<string, JsonElement> inputs, CancellationToken cancellationToken)
    {
        if (call.CallerPosture is not { } caller || _node.Manifest.RepositoryInput is not { } input) return null;

        if (!TryReadRepositoryId(inputs, input.InputKey, out var repositoryId)) return null;

        if (AgentRepositoryBinding.Find(caller, repositoryId) is not { } bound) return RefuseUnbound(caller, repositoryId);

        if (input.WritesRepository && !AgentRepositoryBinding.AllowsWrite(bound)) return AgentRepositoryBinding.ReadOnlyContextWrite(repositoryId);

        if (call.TeamId is not { } teamId || UseOf(bound, input, inputs, teamId) is not { } use) return null;

        return await _repositoryPolicy.RefusalAsync(use, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The use the repository's own policy must judge, or null when there is none: a write (its publish guards), or a ref
    /// named on read-only context (its default branch decides). A read at the default branch, or any ref of a writable
    /// repository, needs no look at the repository.
    /// </summary>
    private static AgentRepositoryUse? UseOf(WorkspaceRepositorySpec bound, RepositoryInputSpec input, IReadOnlyDictionary<string, JsonElement> inputs, Guid teamId)
    {
        var requestedRef = ReadRef(inputs, input.RefInputKey);
        var pinsRef = requestedRef is not null && !AgentRepositoryBinding.AllowsWrite(bound);

        return input.WritesRepository || pinsRef ? new AgentRepositoryUse { TeamId = teamId, Bound = bound, Ref = requestedRef, Writes = input.WritesRepository } : null;
    }

    /// <summary>The ref exactly as a node reads it — a JSON string, trimmed — or null for none (the default branch).</summary>
    private static string? ReadRef(IReadOnlyDictionary<string, JsonElement> inputs, string? key)
    {
        if (key is null || !inputs.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.String) return null;

        var trimmed = (value.GetString() ?? "").Trim();

        return trimmed.Length > 0 ? trimmed : null;
    }

    /// <summary>
    /// The repository id exactly as every repository-taking node reads it — a JSON string that parses as a uuid — so the
    /// binding sees every value a node would act on. Anything else a node treats as absent (a command with no checkout)
    /// or rejects as invalid, so no repository is reached and there is nothing to hold.
    /// </summary>
    private static bool TryReadRepositoryId(IReadOnlyDictionary<string, JsonElement> inputs, string key, out Guid repositoryId)
    {
        repositoryId = Guid.Empty;

        return inputs.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out repositoryId);
    }

    private string RefuseUnbound(AgentRunPosture caller, Guid repositoryId)
    {
        _logger.LogWarning("Agent run {RunId}: tool {Tool} named repository {RepositoryId}, which the run is not bound to; refused as not found", caller.RunId, _node.TypeKey, repositoryId);

        return AgentRepositoryBinding.NotFound(repositoryId);
    }

    private static AgentToolResult OkFromOutputs(IReadOnlyDictionary<string, JsonElement> outputs)
    {
        var json = JsonSerializer.SerializeToElement(outputs);
        return AgentToolResult.Ok(json, json.GetRawText().Length);
    }
}
