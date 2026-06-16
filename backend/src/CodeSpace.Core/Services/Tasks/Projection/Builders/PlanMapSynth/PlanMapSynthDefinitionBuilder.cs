using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Tasks.Projection.Builders.PlanMap;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Projection.Builders.PlanMapSynth;

/// <summary>
/// The <c>plan-map-synth</c> projection (Rule 18.3 — one impl beside its variant folder): the FIRST multi-agent
/// task shape. A planner decomposes the task into a list of subtask STRINGS, a <c>flow.map</c> fans those out over
/// one real <c>agent.code</c> body per subtask (goal = <c>"Work on {{item}}"</c>), and a synthesizer reduces the
/// per-branch results into the run's output. It shares the planner→map→agent→synth→done skeleton with its
/// model-authored sibling <c>plan-map-dynamic</c> via <see cref="PlanMapBuilderBase"/>, specializing only the
/// planner schema (a plain <c>string[]</c>), the planner prompt, and the body goal binding; it authors no
/// per-branch mode, so the body agent inherits the profile's posture (the historical behaviour).
///
/// <para><b>Build stays PURE</b> — the planner is a NODE that runs at EXECUTION, not a build-time LLM call — so the
/// output always passes the real <c>DefinitionValidator</c>. Self-registers via <see cref="ISingletonDependency"/>;
/// a new projection is a sibling builder folder (like plan-map-dynamic), never an edit here.</para>
/// </summary>
public sealed class PlanMapSynthDefinitionBuilder : PlanMapBuilderBase, ISingletonDependency
{
    public override string ProjectionKind => TaskProjectionKinds.PlanMapSynth;

    /// <summary>The structured-output schema — a <c>subtasks</c> string array, the EXACT shape <c>flow.map</c>'s items binding (<c>{{nodes.planner.outputs.json.subtasks}}</c>) reads.</summary>
    protected override object SubtasksResponseSchema() => new
    {
        type = "object",
        properties = new { subtasks = new { type = "array", items = new { type = "string" } } },
        required = new[] { "subtasks" },
    };

    /// <summary>The planner Inputs — the seed goal framed as a "decompose into subtasks" instruction (the userPrompt the structured LLM completes).</summary>
    protected override JsonElement PlannerInputs(TaskBuildContext context) => JsonSerializer.SerializeToElement(new
    {
        userPrompt = $"Decompose this task into a list of independent subtasks that can be worked in parallel: {context.Seed.Goal}",
    });

    /// <summary>The body agent's goal — bound from the map's per-branch <c>{{item}}</c> (this branch's subtask string), so each branch works its OWN element.</summary>
    protected override string BranchGoal => "Work on {{item}}";
}
