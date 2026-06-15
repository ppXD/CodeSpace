using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;

namespace CodeSpace.Core.Services.Tasks.Recipes.MapFanout;

/// <summary>
/// The <c>map-fanout</c> recipe (Rule 18.3 — one impl beside its variant folder): the FIRST multi-agent recipe.
/// It shapes a task as a planner → parallel fan-out → synthesizer run, defaulting to the
/// <see cref="TaskProjectionKinds.PlanMapSynth"/> projection (whose builder ships in this PR). It is the default
/// SHAPE for the <c>standard</c> and <c>deep</c> effort tiers (<see cref="ServesEfforts"/>), so an explicit
/// <c>standard</c> / <c>deep</c> request (with no pinned recipe) routes a real multi-agent run — while
/// <c>quick</c> stays single-agent.
///
/// <para>Self-registers via <see cref="ISingletonDependency"/>; a new recipe is a sibling folder, never an edit
/// here. <b>Deferred (not built this PR):</b> a dedicated supervisor recipe for <c>deep</c> (PR6 — until then
/// <c>deep</c> reuses map-fanout via <see cref="ServesEfforts"/>), and a plan-review wait_approval gate variant
/// (<see cref="RequiresPlanReview"/> is false — the plan-map-synth graph has no approval node yet).</para>
/// </summary>
public sealed class MapFanoutRecipe : ITaskRecipe, ISingletonDependency
{
    public string RecipeKind => TaskRecipeKinds.MapFanout;

    public IReadOnlyList<string> ServesEfforts => new[] { TaskEffortModes.Standard, TaskEffortModes.Deep };

    public string GoalFrame => "Decompose the task, work the subtasks in parallel, synthesize the results.";

    public string BoundsPreset => TaskEffortModes.Standard;

    public string RecommendedAutonomy => "Standard";

    public string DefaultProjectionKind => TaskProjectionKinds.PlanMapSynth;

    public bool RequiresPlanReview => false;

    public IReadOnlyList<string> RecommendedPhaseLabels => new[] { "Plan", "Fan-out", "Synthesize" };
}
