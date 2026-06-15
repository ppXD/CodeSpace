namespace CodeSpace.Core.Services.Tasks.Recipes;

/// <summary>
/// Resolves an <see cref="ITaskRecipe"/> by its <see cref="ITaskRecipe.RecipeKind"/> — same shape as
/// <c>IAgentHarnessRegistry</c> / <c>ITaskProjectionRegistry</c>, plus a <see cref="Default"/> the router
/// falls OPEN to when a request / classifier names no recipe or names an unknown one (so an unrecognised
/// suggested kind never throws — it routes the safe single-agent recipe). A new recipe becomes resolvable by
/// registering its class — no edit here.
/// </summary>
public interface ITaskRecipeRegistry
{
    /// <summary>Every registered recipe — the "which recipes are available" surface.</summary>
    IReadOnlyList<ITaskRecipe> All { get; }

    /// <summary>Resolve the recipe for <paramref name="recipeKind"/>. Throws when none is registered for that kind.</summary>
    ITaskRecipe Resolve(string recipeKind);

    /// <summary>Try-resolve variant — false (and a null out) when no recipe is registered for <paramref name="recipeKind"/>, for the router to fall OPEN to <see cref="Default"/> rather than throw.</summary>
    bool TryResolve(string recipeKind, out ITaskRecipe recipe);

    /// <summary>The safe fallback recipe (single-agent) the router uses when no recipe is requested / suggested or the named kind is unknown.</summary>
    ITaskRecipe Default { get; }
}
