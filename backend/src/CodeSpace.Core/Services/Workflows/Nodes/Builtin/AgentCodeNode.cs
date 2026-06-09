using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// Runs a coding agent (Codex, Claude Code, …) as a workflow step. On its first pass it builds an
/// <see cref="AgentTask"/> from config and SUSPENDS with an <c>AgentRun</c> token; the engine creates
/// the durable run, dispatches the executor (which streams the harness in its sandbox), and parks this
/// node. When the agent run reaches a terminal state the engine resumes this node with
/// <c>{ status, summary, changedFiles, branch, error }</c>, which it maps: Succeeded → these become the
/// node's outputs; otherwise the node fails, composing with retry + the <c>error</c> branch like any
/// node failure.
///
/// The node is pure — it never touches the DB or spawns a process. The engine + AgentRunService own the
/// run lifecycle, so any failure (unknown harness, sandbox error, timeout) surfaces as a clean node
/// failure.
///
/// Config: goal · harness · model (required) · runnerKind? · timeoutSeconds? · network? · readOnly?
/// Outputs: status · summary · changedFiles · branch
/// </summary>
public sealed class AgentCodeNode : INodeRuntime
{
    public string TypeKey => "agent.code";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Run coding agent",
        Category = "Agent",
        Kind = NodeKind.Regular,
        IconKey = "agent",
        Description = "Runs a coding agent (Codex, Claude Code, …) as a step. Streams its progress live; the run's result becomes this node's output.",
        ConfigSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "goal":           { "type": "string", "description": "What the agent should do (the prompt)." },
                "harness":        { "type": "string", "description": "Agent harness kind, e.g. \"codex-cli\"." },
                "model":          { "type": "string", "description": "Model id within the harness's catalog." },
                "runnerKind":     { "type": "string", "description": "Sandbox runner (e.g. \"local\"). Defaults to the deployment default." },
                "timeoutSeconds": { "type": "integer", "minimum": 1, "description": "Wall-clock cap for the run." },
                "network":        { "type": "boolean", "description": "Allow network access in the sandbox." },
                "readOnly":       { "type": "boolean", "description": "Analysis-only — the agent may not write." }
              },
              "required": ["goal", "harness", "model"]
            }
            """),
        InputSchema = SchemaBuilder.EmptyObject(),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "status":       { "type": "string" },
                "summary":      { "type": "string" },
                "changedFiles": { "type": "array", "items": { "type": "string" } },
                "branch":       { "type": "string" }
              }
            }
            """),
    };

    public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        // Resumed: the agent run finished. ResumePayload = { status, summary, changedFiles, branch, error }.
        if (context.ResumePayload.HasValue) return Task.FromResult(MapResult(context.ResumePayload.Value));

        var goal = ReadString(context.Config, "goal");
        var harness = ReadString(context.Config, "harness");
        var model = ReadString(context.Config, "model");

        if (string.IsNullOrWhiteSpace(goal)) return Fail("Config 'goal' is required.");
        if (string.IsNullOrWhiteSpace(harness)) return Fail("Config 'harness' is required.");
        if (string.IsNullOrWhiteSpace(model)) return Fail("Config 'model' is required.");

        var task = new AgentTask
        {
            Goal = goal,
            Harness = harness,
            Model = model,
            RunnerKind = ReadOptionalString(context.Config, "runnerKind"),
            TimeoutSeconds = ReadInt(context.Config, "timeoutSeconds") ?? 1800,
            Permissions = new AgentPermissions
            {
                Network = ReadBool(context.Config, "network") ? AgentNetworkAccess.On : AgentNetworkAccess.Off,
                WriteScope = ReadBool(context.Config, "readOnly") ? AgentWriteScope.ReadOnly : AgentWriteScope.Workspace,
            },
        };

        return Task.FromResult(NodeResult.Suspend(new SuspensionToken
        {
            Kind = WorkflowWaitKinds.AgentRun,
            Payload = JsonSerializer.SerializeToElement(task, AgentJson.Options),
        }));
    }

    /// <summary>Map the resumed agent-run outcome onto this node's result. Succeeded → outputs; anything else → a clean node failure.</summary>
    private static NodeResult MapResult(JsonElement payload)
    {
        if (ReadString(payload, "status") != nameof(AgentRunStatus.Succeeded))
        {
            var error = ReadString(payload, "error");
            return NodeResult.Fail($"Agent run did not succeed: {(string.IsNullOrEmpty(error) ? ReadString(payload, "status") : error)}");
        }

        var outputs = new Dictionary<string, JsonElement> { ["status"] = JsonSerializer.SerializeToElement(nameof(AgentRunStatus.Succeeded)) };
        CopyIfPresent(payload, "summary", outputs);
        CopyIfPresent(payload, "changedFiles", outputs);
        CopyIfPresent(payload, "branch", outputs);

        return NodeResult.Ok(outputs);
    }

    private static Task<NodeResult> Fail(string message) => Task.FromResult(NodeResult.Fail(message));

    private static string ReadString(IReadOnlyDictionary<string, JsonElement> bag, string key) =>
        bag.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string ReadString(JsonElement bag, string key) =>
        bag.ValueKind == JsonValueKind.Object && bag.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string? ReadOptionalString(IReadOnlyDictionary<string, JsonElement> bag, string key)
    {
        var s = ReadString(bag, key);
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static int? ReadInt(IReadOnlyDictionary<string, JsonElement> bag, string key) =>
        bag.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static bool ReadBool(IReadOnlyDictionary<string, JsonElement> bag, string key) =>
        bag.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.True;

    private static void CopyIfPresent(JsonElement payload, string key, Dictionary<string, JsonElement> outputs)
    {
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(key, out var v)) outputs[key] = v.Clone();
    }
}
