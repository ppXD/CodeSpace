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
/// It may reference an Agent persona (<c>agentDefinitionId</c>): the node only carries the reference; the
/// dispatch-time resolver merges the persona's system prompt + model into the task (staying pure, no DB).
/// With a persona, <c>goal</c> is the task-specific addition to its prompt (optional); without one, <c>goal</c>
/// is required. <c>harness</c> is always required (a persona is harness-agnostic); <c>model</c> is always
/// optional (blank → the persona's model → the harness default).
///
/// Config: harness (required) · agentDefinitionId? · goal (required unless a persona is set) · model? · runnerKind? · timeoutSeconds? · network? · readOnly?
/// Inputs: repositoryId? (the repo to clone into the workspace — pick or bind from the trigger)
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
                "agentDefinitionId": { "type": "string", "format": "uuid", "x-selector": "agent", "description": "Pick an Agent persona — its system prompt + model become the defaults for this run (its prompt prepends the goal below). Leave empty to configure the run inline." },
                "goal":           { "type": "string", "description": "What the agent should do (the prompt). Required unless a persona is selected, in which case it's the task-specific addition to the persona's prompt." },
                "harness":        { "type": "string", "x-selector": "harness", "description": "Which coding-agent CLI runs the task (e.g. Codex, Claude Code). Pick from the available harnesses." },
                "model":          { "type": "string", "description": "Model id within the harness's catalog. Leave empty to use the persona's model, or the harness default." },
                "modelCredentialId": { "type": "string", "format": "uuid", "x-selector": "modelCredential", "description": "Model credential the agent authenticates with. Leave empty to use the persona's default, or the team/operator default." },
                "tools":          { "type": "array", "items": { "type": "string" }, "description": "Tool allow-list the agent is restricted to (e.g. Read, Grep, Bash). Empty = the harness default. Added to (not replacing) the persona's tools; enforced by harnesses that support an allow-list (Claude Code), carried otherwise (Codex restricts via sandbox)." },
                "runnerKind":     { "type": "string", "description": "Sandbox runner (e.g. \"local\"). Defaults to the deployment default." },
                "timeoutSeconds": { "type": "integer", "minimum": 1, "description": "Wall-clock cap for the run." },
                "network":        { "type": "boolean", "description": "Allow network access in the sandbox." },
                "readOnly":       { "type": "boolean", "description": "Analysis-only — the agent may not write." }
              },
              "required": ["harness"]
            }
            """),
        InputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "repositoryId": { "type": "string", "format": "uuid", "x-selector": "repository", "description": "The repository the agent works in — cloned into its workspace before it runs. Pick one, or switch to Expression to bind it from the trigger (e.g. {{trigger.repositoryId}}). Leave empty for an analysis-only run with no repo." }
              }
            }
            """),
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

        if (!TryReadAgentDefinitionId(context, out var agentDefinitionId)) return Fail("Config 'agentDefinitionId' must be an agent persona id (uuid).");

        if (!TryReadModelCredentialId(context, out var modelCredentialId)) return Fail("Config 'modelCredentialId' must be a model credential id (uuid).");

        if (string.IsNullOrWhiteSpace(harness)) return Fail("Config 'harness' is required.");

        // A persona supplies the prompt floor (its system prompt), so 'goal' is only required without one.
        // The dispatch-time resolver composes the persona's prompt + this goal and supplies the model.
        if (agentDefinitionId is null && string.IsNullOrWhiteSpace(goal)) return Fail("Config 'goal' is required when no agent persona is selected.");

        if (!TryReadRepositoryId(context, out var repositoryId)) return Fail("Input 'repositoryId' must be a repository id (uuid).");

        var task = new AgentTask
        {
            Goal = goal,
            Harness = harness,
            Model = ReadOptionalString(context.Config, "model"),
            AgentDefinitionId = agentDefinitionId,
            ModelCredentialId = modelCredentialId,
            Tools = ReadStringArray(context.Config, "tools"),
            RepositoryId = repositoryId,
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

    /// <summary>Read the optional <c>agentDefinitionId</c> config. Absent / empty → no persona (null, a pure-inline run). Present-but-malformed → false (a clean node failure).</summary>
    private static bool TryReadAgentDefinitionId(NodeRunContext context, out Guid? agentDefinitionId)
    {
        agentDefinitionId = null;

        var raw = ReadString(context.Config, "agentDefinitionId");

        if (string.IsNullOrWhiteSpace(raw)) return true;

        if (!Guid.TryParse(raw, out var id)) return false;

        agentDefinitionId = id;
        return true;
    }

    /// <summary>Read the optional <c>modelCredentialId</c> config (a node-level override of the persona/team default). Absent / empty → null. Present-but-malformed → false (a clean node failure).</summary>
    private static bool TryReadModelCredentialId(NodeRunContext context, out Guid? modelCredentialId)
    {
        modelCredentialId = null;

        var raw = ReadString(context.Config, "modelCredentialId");

        if (string.IsNullOrWhiteSpace(raw)) return true;

        if (!Guid.TryParse(raw, out var id)) return false;

        modelCredentialId = id;
        return true;
    }

    /// <summary>Read the optional <c>repositoryId</c> input. Absent / empty → no repo (null, an analysis-only run). Present-but-malformed → false (a clean node failure).</summary>
    private static bool TryReadRepositoryId(NodeRunContext context, out Guid? repositoryId)
    {
        repositoryId = null;

        if (!context.Inputs.TryGetValue("repositoryId", out var value) || value.ValueKind == JsonValueKind.Null) return true;

        var raw = value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        if (string.IsNullOrWhiteSpace(raw)) return true;

        if (!Guid.TryParse(raw, out var id)) return false;

        repositoryId = id;
        return true;
    }

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

    /// <summary>Read an optional string-array config field. Absent → null (inherit the harness default); present → the string elements (blanks skipped), preserving "[]" = no tools.</summary>
    private static IReadOnlyList<string>? ReadStringArray(IReadOnlyDictionary<string, JsonElement> bag, string key)
    {
        if (!bag.TryGetValue(key, out var v) || v.ValueKind != JsonValueKind.Array) return null;

        return v.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    private static void CopyIfPresent(JsonElement payload, string key, Dictionary<string, JsonElement> outputs)
    {
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(key, out var v)) outputs[key] = v.Clone();
    }
}
