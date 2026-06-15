using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;

namespace CodeSpace.Core.Services.Tasks.Recipes.MapFanout;

/// <summary>
/// The <c>map-fanout</c> recipe (Rule 18.3 — one impl beside its variant folder): the FIRST multi-agent recipe.
/// It shapes a task as a planner → parallel fan-out → synthesizer run, defaulting to the
/// <see cref="TaskProjectionKinds.PlanMapSynth"/> projection. It is the default SHAPE for the <c>standard</c>
/// effort tier (<see cref="ServesEfforts"/>), so an explicit <c>standard</c> request (with no pinned recipe)
/// routes a real multi-agent run — while <c>quick</c> stays single-agent and <c>deep</c> now routes the
/// supervisor recipe (which DEGRADES back to this map-fanout shape when the supervisor lane is off).
///
/// <para>Self-registers via <see cref="ISingletonDependency"/>; a new recipe is a sibling folder, never an edit
/// here. This recipe needs no execution-time capability, so <see cref="RequiresCapability"/> /
/// <see cref="DegradesToRecipe"/> are null. <b>Deferred (not built this PR):</b> a plan-review wait_approval
/// gate variant (<see cref="RequiresPlanReview"/> is false — the plan-map-synth graph has no approval node yet).</para>
/// </summary>
public sealed class MapFanoutRecipe : ITaskRecipe, ISingletonDependency
{
    public string RecipeKind => TaskRecipeKinds.MapFanout;

    public IReadOnlyList<string> ServesEfforts => new[] { TaskEffortModes.Standard };

    public string GoalFrame => "Decompose the task, work the subtasks in parallel, synthesize the results.";

    public string BoundsPreset => TaskEffortModes.Standard;

    public string RecommendedAutonomy => "Standard";

    public string DefaultProjectionKind => TaskProjectionKinds.PlanMapSynth;

    public bool RequiresPlanReview => false;

    public IReadOnlyList<string> RecommendedPhaseLabels => new[] { "Plan", "Fan-out", "Synthesize" };

    public string? RequiresCapability => null;

    public string? DegradesToRecipe => null;
}
