using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Tasks.Projection.Builders.PlanMap;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Projection.Builders.PlanMapDynamic;

/// <summary>
/// The <c>plan-map-dynamic</c> projection — the MODEL-AUTHORED sibling of <c>plan-map-synth</c>. It shares the
/// EXACT planner→map→agent→synth→done skeleton (inherited from <see cref="PlanMapBuilderBase"/>, so the two
/// cannot drift), specializing ONLY the four points where the model-authored shape differs:
///
/// <para>
///   • the planner's <c>responseSchema</c> is an OBJECT-ARRAY <c>{ subtasks: [{ name?, goal, mode }] }</c> (mode the
///     hard enum bound research|code) — each subtask carries a model-chosen MODE, not just a goal string;
///   • the planner prompt instructs the model to decompose AND tag each subtask research-vs-code;
///   • the body agent's goal binds from <c>{{item.goal}}</c> (the authored subtask goal);
///   • the body agent's mode binds from <c>{{item.mode}}</c> — the node maps it to permissions (research = read-only
///     + no branch; code = workspace write + push its own branch) UNDER the autonomy-tier + explicit-override
///     precedence, so the autonomy-ceiling clamp still bounds it and the MODEL decides each agent's intent instead
///     of the builder hardcoding it.
/// </para>
///
/// <para>Self-registers via <see cref="ISingletonDependency"/>; this is a NEW opt-in sibling projection, so
/// plan-map-synth stays byte-identical (the shared base is behaviour-preserving — both variants emit exactly what
/// they did before the extraction).</para>
/// </summary>
public sealed class PlanMapDynamicDefinitionBuilder : PlanMapBuilderBase, ISingletonDependency
{
    public override string ProjectionKind => TaskProjectionKinds.PlanMapDynamic;

    /// <summary>A <c>subtasks</c> array of per-agent SPECS (<c>name?</c>, <c>goal</c>, <c>mode</c>), the shape <c>flow.map</c>'s items binding reads. <c>mode</c> is the HARD enum bound (research|code); the node maps any other value to its safe default, never throwing.</summary>
    protected override object SubtasksResponseSchema() => new
    {
        type = "object",
        properties = new
        {
            subtasks = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string" },
                        goal = new { type = "string" },
                        mode = new { type = "string", @enum = new[] { "research", "code" } },
                    },
                    required = new[] { "goal", "mode" },
                },
            },
        },
        required = new[] { "subtasks" },
    };

    /// <summary>The planner prompt (Slice 4) — decompose AND tag each subtask research (analysis-only, reads without changing) vs code (edits the codebase, produces a branch); short name + self-contained goal. The responseSchema enum is the hard bound; this prompt is soft guidance.</summary>
    protected override JsonElement PlannerInputs(TaskBuildContext context) => JsonSerializer.SerializeToElement(new
    {
        systemPrompt = "Decompose the task into the fewest independent subtasks that can be worked in parallel. For EACH subtask, choose a mode: 'research' for analysis-only work that reads the codebase without changing it, or 'code' for work that edits the codebase and produces a branch. Give each subtask a short name and a self-contained goal.",
        userPrompt = $"Task: {context.Seed.Goal}",
    });

    /// <summary>The body agent's goal — bound from the map's per-branch <c>{{item.goal}}</c> (this branch's authored subtask goal), so each branch works its OWN element.</summary>
    protected override string BranchGoal => "{{item.goal}}";

    /// <summary>The body agent's mode — bound from the map's per-branch <c>{{item.mode}}</c> (the model's chosen intent), which the node maps to permissions + push under the autonomy-tier + override precedence.</summary>
    protected override string? BranchMode => "{{item.mode}}";
}
