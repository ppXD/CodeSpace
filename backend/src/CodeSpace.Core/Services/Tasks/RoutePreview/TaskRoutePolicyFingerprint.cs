using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Tasks.Bounds;
using CodeSpace.Core.Services.Tasks.Capabilities;
using CodeSpace.Core.Services.Tasks.Effort;
using CodeSpace.Core.Services.Tasks.Recipes;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Services.Tasks.RoutePreview;

/// <summary>A routing-consistency fingerprint, not a grant version. Build identity invalidates rolling-deployment drift; runtime registry and capability values invalidate configuration drift.</summary>
public sealed class TaskRoutePolicyFingerprint : IScopedDependency
{
    private readonly ITaskRecipeRegistry _recipes;
    private readonly IBoundsPresetRegistry _bounds;
    private readonly ICapabilityProbeRegistry _capabilities;
    private readonly IEffortClassifierRegistry _classifiers;

    public TaskRoutePolicyFingerprint(ITaskRecipeRegistry recipes, IBoundsPresetRegistry bounds, ICapabilityProbeRegistry capabilities, IEffortClassifierRegistry classifiers)
    {
        _recipes = recipes;
        _bounds = bounds;
        _capabilities = capabilities;
        _classifiers = classifiers;
    }

    public string Capture() => ContractHashing.Hash(new
    {
        Version = "task-route-policy-v1",
        Build = typeof(EffortRouter).Module.ModuleVersionId,
        DeploymentCeiling = AgentAutonomyPolicy.DeploymentCeiling,
        DefaultRecipe = _recipes.Default.RecipeKind,
        Recipes = _recipes.All.OrderBy(r => r.RecipeKind, StringComparer.Ordinal).Select(r => new { Implementation = r.GetType().Assembly.ManifestModule.ModuleVersionId, r.RecipeKind, r.ServesEfforts, r.BoundsPreset, r.RecommendedAutonomy, r.DefaultProjectionKind, r.RequiresPlanReview, r.RequiresCapability, r.DegradesToRecipe }),
        Bounds = _bounds.All.OrderBy(b => b.PresetKind, StringComparer.Ordinal).Select(b => new { Implementation = b.GetType().Assembly.ManifestModule.ModuleVersionId, b.PresetKind, Caps = b.ToCaps() }),
        Capabilities = _capabilities.Capabilities.Concat(_recipes.All.Select(r => r.RequiresCapability).OfType<string>()).Distinct().Order(StringComparer.Ordinal).Select(c => new { Kind = c, Available = _capabilities.IsAvailable(c) }),
        Classifier = _classifiers.Auto.Kind,
    }, WorkflowJson.Options);
}
