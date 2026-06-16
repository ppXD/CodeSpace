using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;

namespace CodeSpace.Core.Services.Tasks.Recipes.MapFanoutDynamic;

/// <summary>
/// The <c>map-fanout-dynamic</c> recipe (Rule 18.3 — one impl beside its variant folder): the MODEL-AUTHORED
/// sibling of <c>map-fanout</c>. It shapes a task as a planner → parallel fan-out → synthesizer run, defaulting to
/// the <see cref="TaskProjectionKinds.PlanMapDynamic"/> projection — where the planner authors a per-subtask
/// <c>mode</c> (research/code) the fan-out body maps to permissions, so the MODEL decides each agent's intent.
///
/// <para>It is OPT-IN: <see cref="ServesEfforts"/> is empty, so it claims NO effort tier (the registry's
/// no-overlap assert still holds — standard stays mapped to <c>map-fanout</c>) and is reached only by an explicit
/// <c>RequestedRecipe="map-fanout-dynamic"</c>. Self-registers via <see cref="ISingletonDependency"/>; a new
/// recipe is a sibling folder, never an edit here. This recipe needs no execution-time capability, so
/// <see cref="RequiresCapability"/> / <see cref="DegradesToRecipe"/> are null. Like <c>map-fanout</c> it emits no
/// PR-open node (merge stays human-gated), so <see cref="RequiresPlanReview"/> is false.</para>
/// </summary>
public sealed class MapFanoutDynamicRecipe : ITaskRecipe, ISingletonDependency
{
    public string RecipeKind => TaskRecipeKinds.MapFanoutDynamic;

    public IReadOnlyList<string> ServesEfforts => Array.Empty<string>();

    public string GoalFrame => "Decompose the task, let the model tag each subtask research or code, work them in parallel, synthesize the results.";

    public string BoundsPreset => TaskEffortModes.Standard;

    public string RecommendedAutonomy => "Standard";

    public string DefaultProjectionKind => TaskProjectionKinds.PlanMapDynamic;

    public bool RequiresPlanReview => false;

    public IReadOnlyList<string> RecommendedPhaseLabels => new[] { "Plan", "Fan-out", "Synthesize" };

    public string? RequiresCapability => null;

    public string? DegradesToRecipe => null;
}
