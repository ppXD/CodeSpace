using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;

namespace CodeSpace.Core.Services.Tasks.Recipes.SingleAgent;

/// <summary>
/// The <c>single-agent</c> recipe (Rule 18.3 — one impl beside its variant folder) — the only recipe shipped
/// this PR and the registry's <c>Default</c> fail-open fallback. It shapes a task as one agent working the whole
/// thing: it defaults to the <see cref="TaskProjectionKinds.SingleAgent"/> projection (whose builder ships in
/// PR2), falls back to the <c>standard</c> bounds preset, recommends Standard autonomy, and needs no plan
/// review. Self-registers via <see cref="ISingletonDependency"/>; a new recipe is a sibling folder, never an
/// edit here.
/// </summary>
public sealed class SingleAgentRecipe : ITaskRecipe, ISingletonDependency
{
    public string RecipeKind => TaskRecipeKinds.SingleAgent;

    public IReadOnlyList<string> ServesEfforts => new[] { TaskEffortModes.Quick };

    public string GoalFrame => "One agent works the whole task end to end in a single run.";

    public string BoundsPreset => TaskEffortModes.Standard;

    public string RecommendedAutonomy => "Standard";

    public string DefaultProjectionKind => TaskProjectionKinds.SingleAgent;

    public bool RequiresPlanReview => false;

    public IReadOnlyList<string> RecommendedPhaseLabels => new[] { "Run the task" };

    public string? RequiresCapability => null;

    public string? DegradesToRecipe => null;
}
