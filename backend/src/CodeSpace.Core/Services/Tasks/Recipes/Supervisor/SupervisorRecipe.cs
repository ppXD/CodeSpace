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
/// <para><b>Always on.</b> The supervisor lane graduated its feature gate and is now unconditionally available, so
/// the supervisor projection's <c>agent.supervisor</c> node always runs and this recipe always projects the durable
/// supervisor lane. It declares no <see cref="RequiresCapability"/> and never degrades — <c>deep</c> always reaches
/// the supervisor shape. Self-registers via <see cref="ISingletonDependency"/>; a new recipe is a
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

    // The supervisor lane is always on (it graduated its feature gate), so this recipe never degrades — it always
    // projects the durable supervisor lane.
    public string? RequiresCapability => null;

    public string? DegradesToRecipe => null;
}
