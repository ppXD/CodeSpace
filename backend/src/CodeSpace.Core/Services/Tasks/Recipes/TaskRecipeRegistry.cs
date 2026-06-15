using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Tasks.Effort;

namespace CodeSpace.Core.Services.Tasks.Recipes;

/// <summary>
/// Default <see cref="ITaskRecipeRegistry"/> — indexes every registered <see cref="ITaskRecipe"/> by its
/// <see cref="ITaskRecipe.RecipeKind"/>. Mirrors <c>AgentHarnessRegistry</c> / <c>BoundsPresetRegistry</c>
/// EXACTLY: DI injects all recipes, this dedups (a duplicate kind throws in the ctor) + resolves (an unknown
/// kind throws; <see cref="TryResolve"/> returns false). <see cref="Default"/> is the single-agent recipe,
/// resolved from the indexed dict in the ctor (a clear error if it is absent). Registered via the
/// <see cref="ISingletonDependency"/> marker, so adding a recipe needs no wiring here — the dispatch is
/// <c>Resolve(openString)</c> with zero per-kind switch.
/// </summary>
public sealed class TaskRecipeRegistry : ITaskRecipeRegistry, ISingletonDependency
{
    private readonly IReadOnlyDictionary<string, ITaskRecipe> _byKind;

    public TaskRecipeRegistry(IEnumerable<ITaskRecipe> recipes)
    {
        var list = recipes.ToList();

        var duplicates = list.GroupBy(r => r.RecipeKind).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Duplicate ITaskRecipe kinds: {string.Join(", ", duplicates)}");

        var contestedEfforts = list.SelectMany(r => r.ServesEfforts).GroupBy(e => e).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        if (contestedEfforts.Count > 0)
            throw new InvalidOperationException($"Effort tiers claimed by more than one ITaskRecipe (each tier must map to at most one recipe): {string.Join(", ", contestedEfforts)}");

        _byKind = list.ToDictionary(r => r.RecipeKind);
        All = list;

        if (!_byKind.TryGetValue(TaskRecipeKinds.SingleAgent, out var fallback))
            throw new InvalidOperationException($"The default recipe '{TaskRecipeKinds.SingleAgent}' is not registered — at least the single-agent recipe must exist for the router's fail-open fallback.");

        Default = fallback;
    }

    public IReadOnlyList<ITaskRecipe> All { get; }

    public ITaskRecipe Default { get; }

    public ITaskRecipe Resolve(string recipeKind)
    {
        if (!_byKind.TryGetValue(recipeKind, out var recipe))
            throw new InvalidOperationException($"No ITaskRecipe registered for kind '{recipeKind}'. Drop a Recipes/<Kind>/ impl that self-registers.");

        return recipe;
    }

    public bool TryResolve(string recipeKind, out ITaskRecipe recipe) =>
        _byKind.TryGetValue(recipeKind, out recipe!);

    public ITaskRecipe RecipeForEffort(string effortMode) =>
        All.FirstOrDefault(r => r.ServesEfforts.Contains(effortMode)) ?? Default;
}
