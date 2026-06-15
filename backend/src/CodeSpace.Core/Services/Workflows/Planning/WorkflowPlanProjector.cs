using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Dtos.Workflows.Planning;

namespace CodeSpace.Core.Services.Workflows.Planning;

/// <summary>
/// Projects a <see cref="PlannedWorkflow"/> onto a FIXED, safe graph (Rule 18.3 — one impl beside the
/// abstraction). The model NEVER names a node type or an edge; the projector owns the skeleton and only
/// folds the plan's DATA in as config + a baked default input. The emitted graph:
///
/// <code>
/// trigger.manual ─▶ flow.wait_approval ─▶ logic.if(approved)
///                                            ├─true ▶ flow.map(items = {{input.subtasks}})
///                                            │           └ map_start ▶ [coding ? agent.code : llm.complete]
///                                            │        ▶ synthesizer (llm.complete) ▶ End
///                                            └─false ▶ Rejected (End)
/// </code>
///
/// <para>The subtasks reach the map as a baked array DEFAULT on a declared <c>subtasks</c> Input
/// (<c>{{input.subtasks}}</c>): the engine's BuildInputScope populates the value from the default when a
/// run supplies none, so the map fans out for real. Each branch reads its element as
/// <c>{{item.title}}</c> / <c>{{item.instruction}}</c>.</para>
///
/// <para>The projection ALWAYS validates (DefinitionValidator) — the planning service enforces that before
/// returning, and a unit test pins it. Nothing here runs: it is a definition the human must save+run.</para>
/// </summary>
public sealed partial class WorkflowPlanProjector : IWorkflowPlanProjector, IScopedDependency
{
    private const string CodingKind = "coding";

    public WorkflowDefinition Project(PlannedWorkflow plan)
    {
        var bodyTypeKey = ResolveBodyTypeKey(plan);

        return new WorkflowDefinition
        {
            Inputs = new[] { SubtasksInput(plan) },
            Nodes = BuildNodes(plan, bodyTypeKey),
            Edges = BuildEdges(),
        };
    }

    /// <summary>The per-branch body node type the recommended-kind switch decides — <c>coding</c> ⇒ <c>agent.code</c>, anything else ⇒ <c>llm.complete</c>. Shared by the one-shot and coordinated projections so the switch never drifts.</summary>
    private static string ResolveBodyTypeKey(PlannedWorkflow plan) =>
        string.Equals(plan.RecommendedWorkflowKind, CodingKind, StringComparison.OrdinalIgnoreCase) ? "agent.code" : "llm.complete";

    /// <summary>The declared <c>subtasks</c> Input whose DEFAULT bakes the plan's subtasks as a resolvable array — the source the map's <c>{{input.subtasks}}</c> binding fans out over.</summary>
    private static WorkflowVariable SubtasksInput(PlannedWorkflow plan) => new()
    {
        Name = "subtasks",
        Label = "Subtasks",
        Description = "The planned subtasks the workflow fans out over. Edit before running to change the plan.",
        Schema = Json("""{ "type": "array", "items": { "type": "object" } }"""),
        Default = SerializeSubtasks(plan.Subtasks),
        Required = false,
    };

    private static IReadOnlyList<NodeDefinition> BuildNodes(PlannedWorkflow plan, string bodyTypeKey) => new List<NodeDefinition>
    {
        new() { Id = "start", TypeKey = "trigger.manual", Label = "Start", Config = Empty(), Inputs = Empty() },

        new() { Id = "approve", TypeKey = "flow.wait_approval", Label = "Review plan",
                Config = Json($$"""{ "prompt": {{JsonString(BuildApprovalPrompt(plan))}} }"""), Inputs = Empty() },

        new() { Id = "gate", TypeKey = "logic.if", Label = "Approved?",
                Config = Json("""{ "condition": "{{nodes.approve.outputs.approved}} == true" }"""), Inputs = Empty() },

        new() { Id = "map", TypeKey = "flow.map", Label = "Run each subtask", Config = Empty(),
                Inputs = Json("""{ "items": "{{input.subtasks}}" }""") },
        new() { Id = "map_start", TypeKey = "flow.map_start", ParentId = "map", Config = Empty(), Inputs = Empty() },
        BodyNode(bodyTypeKey),

        new() { Id = "synth", TypeKey = "llm.complete", Label = "Synthesize results",
                Config = Json("""{ "provider": "Anthropic" }"""),
                Inputs = Json("""{ "systemPrompt": "Combine the per-subtask results into one concise summary.", "userPrompt": "Per-subtask results:\n{{nodes.map.outputs.results}}" }""") },

        new() { Id = "done", TypeKey = "builtin.terminal", Label = "Done", Config = Empty(), Inputs = Empty() },
        new() { Id = "rejected", TypeKey = "builtin.terminal", Label = "Rejected", Config = Empty(), Inputs = Empty() },
    };

    /// <summary>The per-branch body node — the only place the recommended-kind switch decides a node type (coding → agent.code, else → llm.complete). Each reads its element as {{item.title}}/{{item.instruction}}.</summary>
    private static NodeDefinition BodyNode(string bodyTypeKey) => bodyTypeKey == "agent.code"
        ? new() { Id = "body", TypeKey = "agent.code", ParentId = "map", Label = "Agent",
                  // harness from CodexHarness.HarnessKind (the const that owns the wire string) so a rename can't silently drift (Rule 8); an anon object keeps the {{item.*}} refs literal without brace-escaping.
                  Config = JsonSerializer.SerializeToElement(new { goal = "{{item.title}}: {{item.instruction}}", harness = CodexHarness.HarnessKind, autonomyLevel = "Confined", readOnly = true }),
                  Inputs = Empty() }
        : new() { Id = "body", TypeKey = "llm.complete", ParentId = "map", Label = "Work the subtask",
                  Config = Json("""{ "provider": "Anthropic" }"""),
                  Inputs = Json("""{ "userPrompt": "{{item.title}}: {{item.instruction}}" }""") };

    private static IReadOnlyList<EdgeDefinition> BuildEdges() => new List<EdgeDefinition>
    {
        new() { From = "start", To = "approve" },
        new() { From = "approve", To = "gate" },
        new() { From = "gate", To = "map", SourceHandle = "true" },
        new() { From = "gate", To = "rejected", SourceHandle = "false" },
        new() { From = "map", To = "synth" },
        new() { From = "synth", To = "done" },
        new() { From = "map_start", To = "body" },   // map body: start → single terminal body node (per-element result)
    };

    private static string BuildApprovalPrompt(PlannedWorkflow plan)
    {
        var lines = plan.Subtasks.Select((s, i) => $"{i + 1}. {s.Title}");
        return $"Goal: {plan.Goal}\n\nPlanned subtasks:\n{string.Join("\n", lines)}\n\nApprove to run, or reject.";
    }

    /// <summary>
    /// Bake the subtasks with camelCase keys so the per-branch body node resolves <c>{{item.title}}</c> /
    /// <c>{{item.instruction}}</c> — the VariableResolver's element-property lookup is CASE-SENSITIVE, so the
    /// baked keys must match the lowercase refs exactly (and the planner's own schema property names).
    /// </summary>
    private static JsonElement SerializeSubtasks(IReadOnlyList<PlannedSubtask> subtasks) =>
        JsonSerializer.SerializeToElement(subtasks, CamelCase);

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static JsonElement Empty() => JsonDocument.Parse("{}").RootElement.Clone();
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    /// <summary>JSON-encode a string so it can be embedded inside a raw JSON config literal (escapes quotes/newlines).</summary>
    private static string JsonString(string value) => JsonSerializer.Serialize(value);
}
