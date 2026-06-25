using CodeSpace.Core.Services.Tasks.Bounds;
using CodeSpace.Core.Services.Tasks.Bounds.Presets.Quick;
using CodeSpace.Core.Services.Tasks.Bounds.Presets.Standard;
using CodeSpace.Core.Services.Tasks.Effort;
using CodeSpace.Core.Services.Tasks.Effort.Classifiers.Heuristic;
using CodeSpace.Core.Services.Tasks.Effort.Classifiers.Llm;
using CodeSpace.Core.Services.Tasks.Recipes;
using CodeSpace.Core.Services.Tasks.Recipes.SingleAgent;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the three NEW open-string registries (classifier / recipe / bounds) — the SAME IEnumerable&lt;T&gt; +
/// dedup + resolve shape <see cref="TaskProjectionRegistryTests"/> / <see cref="AgentHarnessRegistryTests"/>
/// pin. Each: resolves by kind, rejects a duplicate kind in the ctor, throws on an unknown Resolve, returns
/// false from TryResolve, and (recipe / classifier) exposes a Default. Dispatch is <c>Resolve(openString)</c>
/// with no per-kind switch — half the zero-core-edit proof.
/// </summary>
[Trait("Category", "Unit")]
public class EffortRegistriesTests
{
    // ─── Classifier registry ────────────────────────────────────────────────

    [Fact]
    public void Classifier_registry_resolves_by_kind_and_exposes_the_heuristic_default()
    {
        var heuristic = new HeuristicEffortClassifier();
        var registry = new EffortClassifierRegistry(new IEffortClassifier[] { heuristic });

        registry.Resolve(HeuristicEffortClassifier.ClassifierKind).ShouldBeSameAs(heuristic);
        registry.Default.ShouldBeSameAs(heuristic);
        registry.All.Select(c => c.Kind).ShouldContain(HeuristicEffortClassifier.ClassifierKind);
    }

    [Fact]
    public void Classifier_registry_Auto_prefers_structured_llm_when_registered_else_the_heuristic_default()
    {
        var heuristic = new HeuristicEffortClassifier();

        // Heuristic only → Auto IS the heuristic (the always-confirm baseline).
        new EffortClassifierRegistry(new IEffortClassifier[] { heuristic }).Auto.ShouldBeSameAs(heuristic);

        // structured_llm registered → Auto prefers it (a real model decision supersedes the baseline), but Default
        // stays the guaranteed heuristic floor (the LLM classifier's own run-time fallback).
        var llm = new FakeClassifier(LlmEffortClassifier.ClassifierKind);
        var registry = new EffortClassifierRegistry(new IEffortClassifier[] { heuristic, llm });

        registry.Auto.ShouldBeSameAs(llm, "the structured-LLM classifier supersedes the heuristic on the auto path");
        registry.Default.ShouldBeSameAs(heuristic, "Default stays the guaranteed heuristic baseline");
    }

    [Fact]
    public void Classifier_registry_rejects_duplicate_kinds()
    {
        Should.Throw<InvalidOperationException>(() =>
            new EffortClassifierRegistry(new IEffortClassifier[] { new FakeClassifier("dup"), new FakeClassifier("dup") }));
    }

    [Fact]
    public void Classifier_registry_throws_without_the_heuristic_default()
    {
        // The Default fallback must exist; a registry of only-fakes is a misconfiguration.
        Should.Throw<InvalidOperationException>(() =>
            new EffortClassifierRegistry(new IEffortClassifier[] { new FakeClassifier("fake") }));
    }

    [Fact]
    public void Classifier_registry_resolve_throws_and_tryresolve_is_false_for_unknown()
    {
        var registry = new EffortClassifierRegistry(new IEffortClassifier[] { new HeuristicEffortClassifier() });

        Should.Throw<InvalidOperationException>(() => registry.Resolve("never-registered"));
        registry.TryResolve("never-registered", out var c).ShouldBeFalse();
        c.ShouldBeNull();
    }

    // ─── Recipe registry ────────────────────────────────────────────────────

    [Fact]
    public void Recipe_registry_resolves_by_kind_and_exposes_the_single_agent_default()
    {
        var single = new SingleAgentRecipe();
        var registry = new TaskRecipeRegistry(new ITaskRecipe[] { single });

        registry.Resolve(TaskRecipeKinds.SingleAgent).ShouldBeSameAs(single);
        registry.Default.ShouldBeSameAs(single);
    }

    [Fact]
    public void Recipe_registry_rejects_duplicate_kinds()
    {
        Should.Throw<InvalidOperationException>(() =>
            new TaskRecipeRegistry(new ITaskRecipe[] { new FakeRecipe("dup"), new FakeRecipe("dup") }));
    }

    [Fact]
    public void Recipe_registry_throws_without_the_single_agent_default()
    {
        Should.Throw<InvalidOperationException>(() =>
            new TaskRecipeRegistry(new ITaskRecipe[] { new FakeRecipe("fake") }));
    }

    [Fact]
    public void Recipe_registry_resolve_throws_and_tryresolve_is_false_for_unknown()
    {
        var registry = new TaskRecipeRegistry(new ITaskRecipe[] { new SingleAgentRecipe() });

        Should.Throw<InvalidOperationException>(() => registry.Resolve("never-registered"));
        registry.TryResolve("never-registered", out var r).ShouldBeFalse();
        r.ShouldBeNull();
    }

    // ─── Bounds registry ────────────────────────────────────────────────────

    [Fact]
    public void Bounds_registry_resolves_by_kind_and_lists_all()
    {
        var quick = new QuickBoundsPreset();
        var standard = new StandardBoundsPreset();
        var registry = new BoundsPresetRegistry(new IBoundsPreset[] { quick, standard });

        registry.Resolve(TaskEffortModes.Quick).ShouldBeSameAs(quick);
        registry.All.Select(p => p.PresetKind).ShouldBe(new[] { TaskEffortModes.Quick, TaskEffortModes.Standard }, ignoreOrder: true);
    }

    [Fact]
    public void Bounds_registry_rejects_duplicate_kinds()
    {
        Should.Throw<InvalidOperationException>(() =>
            new BoundsPresetRegistry(new IBoundsPreset[] { new FakeBounds("dup"), new FakeBounds("dup") }));
    }

    [Fact]
    public void Bounds_registry_resolve_throws_and_tryresolve_is_false_for_unknown()
    {
        var registry = new BoundsPresetRegistry(new IBoundsPreset[] { new QuickBoundsPreset() });

        Should.Throw<InvalidOperationException>(() => registry.Resolve("never-registered"));
        registry.TryResolve("never-registered", out var p).ShouldBeFalse();
        p.ShouldBeNull();
    }

    // ─── Test doubles ────────────────────────────────────────────────────────

    private sealed class FakeClassifier : IEffortClassifier
    {
        public FakeClassifier(string kind) => Kind = kind;
        public string Kind { get; }
        public Task<EffortDecision> ClassifyAsync(EffortRouteRequest request, CancellationToken ct) =>
            Task.FromResult(new EffortDecision { Signals = new EffortSignals(), SuggestedEffort = TaskEffortModes.Quick, SuggestedRecipe = TaskRecipeKinds.SingleAgent });
    }

    private sealed class FakeRecipe : ITaskRecipe
    {
        public FakeRecipe(string kind) => RecipeKind = kind;
        public string RecipeKind { get; }
        public IReadOnlyList<string> ServesEfforts => Array.Empty<string>();
        public string GoalFrame => "fake";
        public string BoundsPreset => TaskEffortModes.Standard;
        public string RecommendedAutonomy => "Standard";
        public string DefaultProjectionKind => TaskProjectionKinds.SingleAgent;
        public bool RequiresPlanReview => false;
        public IReadOnlyList<string> RecommendedPhaseLabels => Array.Empty<string>();
        public string? RequiresCapability => null;
        public string? DegradesToRecipe => null;
    }

    private sealed class FakeBounds : IBoundsPreset
    {
        public FakeBounds(string kind) => PresetKind = kind;
        public string PresetKind { get; }
        public RouteCaps ToCaps() => new();
    }
}
