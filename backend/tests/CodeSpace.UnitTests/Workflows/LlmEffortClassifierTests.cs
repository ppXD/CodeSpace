using System.Text.Json;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Tasks.Bounds;
using CodeSpace.Core.Services.Tasks.Bounds.Presets.Deep;
using CodeSpace.Core.Services.Tasks.Bounds.Presets.Quick;
using CodeSpace.Core.Services.Tasks.Bounds.Presets.Standard;
using CodeSpace.Core.Services.Tasks.Capabilities;
using CodeSpace.Core.Services.Tasks.Effort;
using CodeSpace.Core.Services.Tasks.Effort.Classifiers.Heuristic;
using CodeSpace.Core.Services.Tasks.Effort.Classifiers.Llm;
using CodeSpace.Core.Services.Tasks.Recipes;
using CodeSpace.Core.Services.Tasks.Recipes.MapFanout;
using CodeSpace.Core.Services.Tasks.Recipes.SingleAgent;
using CodeSpace.Core.Services.Tasks.Recipes.Supervisor;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit: the structured-LLM effort classifier (P4) at the honest <see cref="IStructuredLLMClient"/> seam (only the
/// network is faked). Pins: with a model it maps the reply onto the generic signals + a MODEL-DERIVED confidence that
/// CAN clear <see cref="EffortPolicy.ConfirmConfidenceFloor"/> (so "Auto" becomes a real decision, not an always-confirm
/// guess), the policy still decides the tier from the signals, and a clamped confidence; AND the 兜底 — no structured
/// provider / no pool model / a transport miss / a malformed reply each DELEGATE to the deterministic heuristic baseline
/// (an always-confirm guess) so the auto path always produces a decision.
/// </summary>
[Trait("Category", "Unit")]
public class LlmEffortClassifierTests
{
    private static EffortRouteRequest Request(string goal = "Add a feature with tests across modules") =>
        new() { Seed = new TaskLaunchSeed { Goal = goal, SurfaceKind = "chat", TeamId = Guid.NewGuid() } };

    private static LlmEffortClassifier Classifier(IStructuredLLMClient? client, ModelPoolPick? pick) =>
        new(new FakeClients(client), new FakeSelector(pick), new FakeRecipes(), new HeuristicEffortClassifier());

    private static ModelPoolPick Pick() => new() { ModelId = "m", Credential = new ResolvedModelCredential { Provider = "TestEffort", ApiKey = "sk" } };

    // ── With a model: real signals + a real confidence that can clear the floor ──

    [Fact]
    public async Task With_a_model_it_maps_the_signals_a_clearing_confidence_and_the_policy_tier()
    {
        var reply = Reply(riskySideEffects: true, estimatedCostTier: "high", confidence: 0.92, rationale: "Prod migration.");

        var decision = await Classifier(new CannedClient(reply), Pick()).ClassifyAsync(Request(), CancellationToken.None);

        decision.ClassifierKind.ShouldBe(LlmEffortClassifier.ClassifierKind, "the decision is the model's, not the heuristic fallback");
        decision.Confidence.ShouldBe(0.92, "the model-derived confidence is carried through");
        decision.Confidence.ShouldBeGreaterThanOrEqualTo(EffortPolicy.ConfirmConfidenceFloor, "a confident model produces a clearing confidence (the classifier's output; the router still vetoes the skip for a RISKY task)");
        decision.Signals.RiskySideEffects.ShouldBeTrue("the model's signals map verbatim");
        decision.SuggestedEffort.ShouldBe(TaskEffortModes.Deep, "the POLICY decides the tier from the signals (risky/high → deep), not the model directly");
        decision.SuggestedRecipe.ShouldBe($"recipe-for-{TaskEffortModes.Deep}", "the suggested recipe is the registry's recipe for the policy tier");
        decision.Rationale.ShouldBe("Prod migration.");
    }

    [Fact]
    public async Task The_policy_decides_the_tier_from_the_model_signals_not_the_model()
    {
        // needsCodeChange + crossFile (no risk) → the policy's MIDDLE row → standard.
        var standard = await Classifier(new CannedClient(Reply(needsCodeChange: true, crossFile: true, confidence: 0.8)), Pick())
            .ClassifyAsync(Request(), CancellationToken.None);
        standard.SuggestedEffort.ShouldBe(TaskEffortModes.Standard);

        // no strong signals → the catch-all → quick.
        var quick = await Classifier(new CannedClient(Reply(confidence: 0.8)), Pick()).ClassifyAsync(Request(), CancellationToken.None);
        quick.SuggestedEffort.ShouldBe(TaskEffortModes.Quick);
    }

    [Theory]
    [InlineData(1.7, 1.0)]    // over-claimed → clamped to 1
    [InlineData(-0.3, 0.0)]   // negative → clamped to 0
    public async Task It_clamps_an_out_of_range_model_confidence(double reported, double expected)
    {
        var decision = await Classifier(new CannedClient(Reply(confidence: reported)), Pick()).ClassifyAsync(Request(), CancellationToken.None);

        decision.Confidence.ShouldBe(expected, "a model confidence outside 0..1 is clamped — the router's floor gate can't be gamed past the bounds");
    }

    // ── 兜底: every model-unavailable / miss path delegates to the heuristic baseline ──

    [Fact]
    public async Task No_structured_provider_falls_back_to_the_heuristic_baseline()
    {
        var decision = await Classifier(client: null, pick: Pick()).ClassifyAsync(Request(), CancellationToken.None);

        decision.ClassifierKind.ShouldBe(HeuristicEffortClassifier.ClassifierKind, "no structured client → the heuristic floor");
        decision.Confidence.ShouldBeLessThan(EffortPolicy.ConfirmConfidenceFloor, "the heuristic always asks the operator to confirm");
    }

    [Fact]
    public async Task No_pool_model_falls_back_to_the_heuristic_baseline()
    {
        var decision = await Classifier(new CannedClient(Reply(confidence: 0.9)), pick: null).ClassifyAsync(Request(), CancellationToken.None);

        decision.ClassifierKind.ShouldBe(HeuristicEffortClassifier.ClassifierKind, "no credentialed pool model → the heuristic floor (never a guessed model)");
    }

    [Fact]
    public async Task A_transport_miss_falls_back_to_the_heuristic_baseline()
    {
        var decision = await Classifier(new ThrowingClient(), Pick()).ClassifyAsync(Request(), CancellationToken.None);

        decision.ClassifierKind.ShouldBe(HeuristicEffortClassifier.ClassifierKind, "an LLM transport/model error never crashes the launch — it degrades to the heuristic");
    }

    [Fact]
    public async Task A_malformed_reply_falls_back_to_the_heuristic_baseline()
    {
        var notAnObject = JsonDocument.Parse("[1,2,3]").RootElement;

        var decision = await Classifier(new RawClient(notAnObject), Pick()).ClassifyAsync(Request(), CancellationToken.None);

        decision.ClassifierKind.ShouldBe(HeuristicEffortClassifier.ClassifierKind, "a reply that doesn't bind to the schema → the heuristic floor");
    }

    [Fact]
    public async Task A_raw_client_exception_NOT_an_LlmApiException_still_falls_back_never_crashes_the_launch()
    {
        // A keyless / mis-configured credential makes the real Anthropic/OpenAI client throw a RAW InvalidOperationException
        // (not LlmApiException) BEFORE the transport — and the OpenAI tool-arg parse can throw one on a 200. The classifier
        // must DEGRADE on ANY non-cancellation client fault, never let it escape and crash the launch.
        var decision = await Classifier(new RawThrowingClient(), Pick()).ClassifyAsync(Request(), CancellationToken.None);

        decision.ClassifierKind.ShouldBe(HeuristicEffortClassifier.ClassifierKind, "a raw client exception degrades to the heuristic — the launch never crashes (the 兜底 contract)");
    }

    // ── End-to-end through the router: a confident LLM routes WITHOUT a confirm card ──

    [Fact]
    public async Task The_router_auto_path_uses_the_llm_and_skips_the_confirm_card_when_the_model_is_confident()
    {
        // The P4 win: with the structured-LLM classifier registered, the router's Auto path uses it, and a CONFIDENT
        // model decision (>= the floor) routes WITHOUT a human confirm — "Auto" is a real decision, not the heuristic's
        // always-confirm guess. (needsCodeChange + crossFile → the policy's standard tier; map-fanout needs no capability.)
        var recipes = new TaskRecipeRegistry(new ITaskRecipe[] { new SingleAgentRecipe(), new MapFanoutRecipe(), new SupervisorRecipe() });
        var llm = new LlmEffortClassifier(new FakeClients(new CannedClient(Reply(needsCodeChange: true, crossFile: true, confidence: 0.9))), new FakeSelector(Pick()), recipes, new HeuristicEffortClassifier());

        var router = new EffortRouter(
            new EffortClassifierRegistry(new IEffortClassifier[] { new HeuristicEffortClassifier(), llm }),
            recipes,
            new BoundsPresetRegistry(new IBoundsPreset[] { new QuickBoundsPreset(), new StandardBoundsPreset(), new DeepBoundsPreset() }),
            new CapabilityProbeRegistry(Array.Empty<ICapabilityProbe>()));

        var plan = await router.RouteAsync(Request("Add validation to the signup endpoint across the service with tests"), CancellationToken.None);

        plan.WasAutoClassified.ShouldBeTrue("an auto request runs the classifier");
        plan.ClassifierConfidence.ShouldBe(0.9);
        plan.NeedsConfirmCard.ShouldBeFalse("a confident LLM decision routes WITHOUT a human confirm — the P4 win over the always-confirm heuristic");
        plan.Confirm.ShouldBeNull();
        plan.EffortMode.ShouldBe(TaskEffortModes.Standard, "the policy routed the code+cross-file task to standard");
    }

    [Fact]
    public async Task The_router_auto_path_STILL_confirms_when_the_llm_is_unsure()
    {
        // The floor still bites: an UNCERTAIN model (below the floor) routes WITH a confirm card — the LLM doesn't
        // remove the human gate, it earns the right to skip it only when confident.
        var recipes = new TaskRecipeRegistry(new ITaskRecipe[] { new SingleAgentRecipe(), new MapFanoutRecipe(), new SupervisorRecipe() });
        var llm = new LlmEffortClassifier(new FakeClients(new CannedClient(Reply(confidence: 0.3))), new FakeSelector(Pick()), recipes, new HeuristicEffortClassifier());

        var router = new EffortRouter(
            new EffortClassifierRegistry(new IEffortClassifier[] { new HeuristicEffortClassifier(), llm }),
            recipes,
            new BoundsPresetRegistry(new IBoundsPreset[] { new QuickBoundsPreset(), new StandardBoundsPreset(), new DeepBoundsPreset() }),
            new CapabilityProbeRegistry(Array.Empty<ICapabilityProbe>()));

        var plan = await router.RouteAsync(Request("do the thing"), CancellationToken.None);

        plan.NeedsConfirmCard.ShouldBeTrue("a low-confidence model decision still asks the operator to confirm");
        plan.Confirm.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_router_STILL_confirms_a_RISKY_task_even_when_the_model_is_confident()
    {
        // The risk veto: an over-confident model must NOT suppress the operator's escalation affordance on destructive
        // work. A risky task (riskySideEffects) confirms regardless of confidence — the pre-LLM always-confirm floor for
        // risk — while non-risky confident tasks still route without a confirm.
        var recipes = new TaskRecipeRegistry(new ITaskRecipe[] { new SingleAgentRecipe(), new MapFanoutRecipe(), new SupervisorRecipe() });
        var llm = new LlmEffortClassifier(new FakeClients(new CannedClient(Reply(riskySideEffects: true, estimatedCostTier: "high", confidence: 0.95))), new FakeSelector(Pick()), recipes, new HeuristicEffortClassifier());

        var router = new EffortRouter(
            new EffortClassifierRegistry(new IEffortClassifier[] { new HeuristicEffortClassifier(), llm }),
            recipes,
            new BoundsPresetRegistry(new IBoundsPreset[] { new QuickBoundsPreset(), new StandardBoundsPreset(), new DeepBoundsPreset() }),
            new CapabilityProbeRegistry(Array.Empty<ICapabilityProbe>()));

        var plan = await router.RouteAsync(Request("Drop the production users table and deploy"), CancellationToken.None);

        plan.ClassifierConfidence.ShouldBe(0.95, "the model was confident");
        plan.NeedsConfirmCard.ShouldBeTrue("a RISKY task confirms regardless of model confidence — the human gate the model can't suppress");
        plan.Confirm.ShouldNotBeNull();
        plan.EffortMode.ShouldBe(TaskEffortModes.Deep, "the risky task still routes to deep");
    }

    // ── Schema commit-contract pin ──

    [Fact]
    public void Schema_shape_is_pinned()
    {
        var props = LlmEffortClassifierSchema.ResponseSchema.GetProperty("properties");

        foreach (var field in new[] { "needsCodeChange", "crossFile", "needsTestsOrCi", "ambiguous", "riskySideEffects", "estimatedCostTier", "confidence", "rationale" })
            props.TryGetProperty(field, out _).ShouldBeTrue($"the schema must carry '{field}'");

        props.GetProperty("estimatedCostTier").GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(new[] { "low", "medium", "high" }, "the cost tier is a closed set the classifier normalizes against");

        LlmEffortClassifierSchema.ResponseSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .ShouldNotContain("rationale", "rationale is optional — a model may omit the why without failing the schema");
    }

    // ── Helpers + fakes ──

    private static JsonElement Reply(bool needsCodeChange = false, bool crossFile = false, bool needsTestsOrCi = false, bool ambiguous = false, bool riskySideEffects = false, string estimatedCostTier = "low", double confidence = 0.5, string rationale = "ok") =>
        JsonSerializer.SerializeToElement(new { needsCodeChange, crossFile, needsTestsOrCi, ambiguous, riskySideEffects, estimatedCostTier, confidence, rationale });

    private sealed class FakeClients : ILLMClientRegistry
    {
        public FakeClients(IStructuredLLMClient? structured) => All = structured == null ? Array.Empty<ILLMClient>() : new ILLMClient[] { (ILLMClient)structured };
        public IReadOnlyList<ILLMClient> All { get; }
        public ILLMClient Resolve(string provider) => All.First();
    }

    private abstract class BaseClient : ILLMClient, IStructuredLLMClient
    {
        public string Provider => "TestEffort";
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) => Task.FromResult(new LLMCompletion { Text = "", Model = request.Model });
        public abstract Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken ct);
    }

    private sealed class CannedClient : BaseClient
    {
        private readonly JsonElement _json;
        public CannedClient(JsonElement json) => _json = json;
        public override Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken ct) =>
            Task.FromResult(new StructuredLLMCompletion { Json = _json, Model = request.Model });
    }

    private sealed class RawClient : BaseClient
    {
        private readonly JsonElement _json;
        public RawClient(JsonElement json) => _json = json;
        public override Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken ct) =>
            Task.FromResult(new StructuredLLMCompletion { Json = _json, Model = request.Model });
    }

    private sealed class ThrowingClient : BaseClient
    {
        public override Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken ct) =>
            throw new LlmApiException("TestEffort", null, LlmErrorCategory.Malformed, "boom");
    }

    /// <summary>Throws a RAW InvalidOperationException — what the real Anthropic/OpenAI client throws on a keyless credential or an unparseable 200 tool-arg, BEFORE/around the transport's LlmApiException wrapping.</summary>
    private sealed class RawThrowingClient : BaseClient
    {
        public override Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("API key not configured");
    }

    private sealed class FakeSelector : IModelPoolSelector
    {
        private readonly ModelPoolPick? _pick;
        public FakeSelector(ModelPoolPick? pick) => _pick = pick;
        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken ct) => Task.FromResult(_pick);
        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken ct) => Task.FromResult(_pick);
        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken ct) => Task.FromResult<ModelDispatchRef?>(null);
        public Task<IReadOnlyList<PoolModelInfo>> ListPoolAsync(Guid teamId, IReadOnlyList<Guid>? allowedRowIds, CancellationToken ct) => Task.FromResult<IReadOnlyList<PoolModelInfo>>(Array.Empty<PoolModelInfo>());
        public Task<Guid?> SelectBrainRowIdAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, CancellationToken ct) => Task.FromResult<Guid?>(null);
    }

    /// <summary>A recipe registry whose RecipeForEffort maps a tier to a recipe whose kind echoes the tier — so a test can prove the classifier suggests the recipe for the POLICY tier.</summary>
    private sealed class FakeRecipes : ITaskRecipeRegistry
    {
        public IReadOnlyList<ITaskRecipe> All => Array.Empty<ITaskRecipe>();
        public ITaskRecipe Default => new FakeRecipe("default");
        public ITaskRecipe Resolve(string recipeKind) => new FakeRecipe(recipeKind);
        public bool TryResolve(string recipeKind, out ITaskRecipe recipe) { recipe = new FakeRecipe(recipeKind); return true; }
        public ITaskRecipe RecipeForEffort(string effortMode) => new FakeRecipe($"recipe-for-{effortMode}");
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
}
