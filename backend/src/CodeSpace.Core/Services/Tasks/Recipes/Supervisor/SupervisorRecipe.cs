using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;

namespace CodeSpace.Core.Services.Tasks.Recipes.Supervisor;

/// <summary>
/// The <c>supervisor</c> recipe (Rule 18.3 — one impl beside its variant folder): the DEEP effort tier's shape.
/// It routes a task into the bounded durable supervisor lane — a single <c>agent.supervisor</c> node that plans,
/// delegates to sub-agents in bounded rounds, and synthesizes (the <see cref="TaskProjectionKinds.Supervisor"/>
/// projection). It is the default SHAPE for <c>deep</c> (<see cref="ServesEfforts"/>), so an explicit
/// <c>deep</c> request (with no pinned recipe) reaches the supervisor; the no-overlap registry assert is why
/// map-fanout no longer claims <c>deep</c>.
///
/// <para><b>Honest degrade.</b> The supervisor projection's <c>agent.supervisor</c> node fails closed when the
/// lane flag is off, so this recipe DECLARES <see cref="RequiresCapability"/> = the supervisor-lane capability +
/// <see cref="DegradesToRecipe"/> = map-fanout: when the lane is unavailable the router degrades <c>deep</c>
/// back to the multi-agent map-fanout shape (with a non-null DegradedReason) rather than projecting a run that
/// would only fail its own gate. Self-registers via <see cref="ISingletonDependency"/>; a new recipe is a
/// sibling folder, never an edit elsewhere.</para>
/// </summary>
public sealed class SupervisorRecipe : ITaskRecipe, ISingletonDependency
{
    public string RecipeKind => TaskRecipeKinds.Supervisor;

    public IReadOnlyList<string> ServesEfforts => new[] { TaskEffortModes.Deep };

    public string GoalFrame => "A supervisor plans, delegates to sub-agents in bounded rounds, and synthesizes — within the durable supervisor lane.";

    public string BoundsPreset => TaskEffortModes.Deep;

    public string RecommendedAutonomy => "Standard";

    public string DefaultProjectionKind => TaskProjectionKinds.Supervisor;

    public bool RequiresPlanReview => false;

    public IReadOnlyList<string> RecommendedPhaseLabels => new[] { "Plan", "Delegate", "Synthesize" };

    public string? RequiresCapability => TaskCapabilities.SupervisorLane;

    public string? DegradesToRecipe => TaskRecipeKinds.MapFanout;
}
