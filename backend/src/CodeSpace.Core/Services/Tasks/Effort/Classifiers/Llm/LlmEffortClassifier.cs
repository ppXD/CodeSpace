using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Tasks.Effort.Classifiers.Heuristic;
using CodeSpace.Core.Services.Tasks.Recipes;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Tasks.Effort;

namespace CodeSpace.Core.Services.Tasks.Effort.Classifiers.Llm;

/// <summary>
/// The STRUCTURED-LLM effort classifier (Rule 18.3 — one impl beside its variant folder) — the intelligent supersession
/// of the always-confirm <see cref="HeuristicEffortClassifier"/> the "Auto" effort path asks for. It resolves a
/// structured-LLM client + a pool model the SAME way the planner does, sends the goal constrained by
/// <see cref="LlmEffortClassifierSchema"/>, and maps the reply onto the generic <see cref="EffortSignals"/> + a
/// MODEL-DERIVED confidence — so an "Auto" task becomes a real model decision that CAN clear
/// <see cref="EffortPolicy.ConfirmConfidenceFloor"/> and route without a human confirm. The policy still decides the
/// tier from the signals (model emits data, policy decides) — identical routing logic, better data.
///
/// <para><b>兜底 (graceful degradation):</b> when no structured provider is registered, the team's pool has no model, or
/// the model path misses for ANY reason — a missing / keyless credential the client rejects, a transport / gateway fault,
/// a malformed reply — it DELEGATES to the deterministic <see cref="HeuristicEffortClassifier"/> baseline, so the auto
/// path ALWAYS produces a decision (an intelligent one with a model, an always-confirm guess without) and NEVER crashes
/// the launch. <see cref="IScopedDependency"/> (its <see cref="IModelPoolSelector"/> + DbContext are per-request — never
/// captured by the singleton it would otherwise be), and it supersedes the baseline with zero router edit (the registry's
/// <c>Auto</c> selector picks it up); a deployment without a model degrades transparently.</para>
/// </summary>
public sealed class LlmEffortClassifier : IEffortClassifier, IScopedDependency
{
    /// <summary>This classifier's open kind string — the registry's <c>Auto</c> selector resolves the auto path to it when registered.</summary>
    public const string ClassifierKind = "structured_llm";

    private readonly ILLMClientRegistry _clients;
    private readonly IModelPoolSelector _models;
    private readonly ITaskRecipeRegistry _recipes;
    private readonly HeuristicEffortClassifier _heuristic;

    public LlmEffortClassifier(ILLMClientRegistry clients, IModelPoolSelector models, ITaskRecipeRegistry recipes, HeuristicEffortClassifier heuristic)
    {
        _clients = clients;
        _models = models;
        _recipes = recipes;
        _heuristic = heuristic;
    }

    public string Kind => ClassifierKind;

    public async Task<EffortDecision> ClassifyAsync(EffortRouteRequest request, CancellationToken ct)
    {
        var classification = await TryClassifyWithModelAsync(request, ct).ConfigureAwait(false);

        // No structured provider / no pool model / a degraded reply → the deterministic heuristic baseline (兜底): a real
        // model-derived decision when a model is available, an always-confirm guess when it is not.
        if (classification is null) return await _heuristic.ClassifyAsync(request, ct).ConfigureAwait(false);

        var signals = ToSignals(classification);
        var tier = EffortPolicy.Decide(signals, requestedEffort: null);

        return new EffortDecision
        {
            Signals = signals,
            SuggestedEffort = tier,
            SuggestedRecipe = _recipes.RecipeForEffort(tier).RecipeKind,
            Confidence = Math.Clamp(classification.Confidence, 0.0, 1.0),
            Rationale = string.IsNullOrWhiteSpace(classification.Rationale) ? "Model-classified effort." : classification.Rationale.Trim(),
            ClassifierKind = ClassifierKind,
        };
    }

    /// <summary>The model's classification, or null when no structured provider has a team pool model, or the model path missed for ANY reason (a keyless credential the client rejects, a transport / gateway fault, a malformed reply) — the caller then falls to the heuristic baseline. Resolves a structured client + model that MATCH (so an all-Custom pool classifies on the Custom client), the same as the planner.</summary>
    private async Task<LlmEffortClassification?> TryClassifyWithModelAsync(EffortRouteRequest request, CancellationToken ct)
    {
        try
        {
            if (await InProcessStructuredModel.ResolveAsync(_clients, _models, request.Seed.TeamId, ct, InProcessStructuredModel.CheapBrainCeiling).ConfigureAwait(false) is not { } resolved)
                return null;

            var (structured, pick) = resolved;

            var completion = await structured.CompleteStructuredAsync(BuildRequest(request, pick), ct).ConfigureAwait(false);

            return completion.Json.Deserialize<LlmEffortClassification>(LlmEffortClassifierSchema.Options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ANY miss on the model path — no/keyless credential the client rejects with a raw exception, a transport or
            // gateway fault (LlmApiException), a malformed reply (JsonException / a parse fault) — degrades to the
            // heuristic baseline. The effort classifier is a launch-time BEST-EFFORT enhancement; it must NEVER crash the
            // launch (the documented 兜底). A genuine cancellation (the caller's token) propagates — never swallowed.
            return null;
        }
    }

    private static StructuredLLMCompletionRequest BuildRequest(EffortRouteRequest request, ModelPoolPick pick) => new()
    {
        Model = pick.ModelId,
        Credential = pick.Credential,
        SystemPrompt = SystemPrompt,
        UserPrompt = BuildUserPrompt(request),
        JsonSchema = LlmEffortClassifierSchema.ResponseSchema,
        MaxOutputTokens = 512,
        Temperature = 0.0,
    };

    /// <summary>
    /// The classifier's user prompt: the goal PLUS the launch context a router needs and a bare goal cannot carry —
    /// whether a repository is bound (an unbound task cannot be a code change), whether this is a follow-up turn in an
    /// existing thread, and a bounded excerpt of the grounding the surface gathered. Without it the model judged
    /// "explain how the retry loop works" identically whether it arrived with a repo or with nothing at all.
    /// </summary>
    internal static string BuildUserPrompt(EffortRouteRequest request)
    {
        var seed = request.Seed;
        var continuing = !string.IsNullOrWhiteSpace(seed.GroundingContext);

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("Task to route:").AppendLine(seed.Goal).AppendLine();
        builder.AppendLine("Context:");
        builder.AppendLine($"- A repository is bound to this task: {(seed.RepositoryId is not null ? "yes" : "no — there is no code to change")}");
        builder.AppendLine($"- This is a follow-up turn continuing earlier work: {(continuing ? "yes" : "no — a fresh task")}");

        if (continuing) builder.AppendLine().AppendLine("Earlier context (excerpt):").AppendLine(Excerpt(seed.GroundingContext!));

        return builder.ToString().TrimEnd();
    }

    /// <summary>The leading <see cref="GroundingExcerptChars"/> of the grounding — enough to tell a continuation apart from a fresh ask, bounded so a long thread digest never dominates a 512-token classification call.</summary>
    private static string Excerpt(string grounding)
    {
        var trimmed = grounding.Trim();

        return trimmed.Length <= GroundingExcerptChars ? trimmed : trimmed[..GroundingExcerptChars] + "…";
    }

    /// <summary>Map the model's reply onto the generic signals — every bool verbatim, the cost tier normalized to the closed low/medium/high set (an out-of-set value degrades to the cheap reading), the deliverable shape normalized to the open shape vocabulary (an out-of-set value degrades to <c>code</c>, the status quo).</summary>
    private static EffortSignals ToSignals(LlmEffortClassification c) => new()
    {
        NeedsCodeChange = c.NeedsCodeChange,
        CrossFile = c.CrossFile,
        NeedsTestsOrCi = c.NeedsTestsOrCi,
        Ambiguous = c.Ambiguous,
        RiskySideEffects = c.RiskySideEffects,
        EstimatedCostTier = NormalizeCostTier(c.EstimatedCostTier),
        DeliverableShape = DeliverableShapes.Normalize(c.DeliverableShape),
    };

    private static string NormalizeCostTier(string? tier)
    {
        var normalized = tier?.Trim().ToLowerInvariant();

        return normalized is "high" or "medium" or "low" ? normalized : "low";
    }

    /// <summary>Internal test accessor (InternalsVisibleTo) — pins the classifier's framing as a tested contract without a real LLM round-trip.</summary>
    internal static string SystemPromptForTest => SystemPrompt;

    /// <summary>How much of the seed's grounding context rides in the classification prompt — enough to tell a continuation from a fresh ask, bounded so a long digest never crowds out the goal.</summary>
    internal const int GroundingExcerptChars = 600;

    private const string SystemPrompt =
        "You are a task router. A task may ask for anything — an explanation, a written document, a code change, an " +
        "investigation — so do NOT assume it is code. Read the task and its context, then extract OBSERVABLE properties " +
        "of the work (NOT a task type): does it change code, span multiple files, need tests/CI, is it ambiguous/under-specified, " +
        "does it have risky/irreversible side effects (delete/drop/migrate/deploy/production/secrets), and a rough cost " +
        "tier (low/medium/high). Also report the DELIVERABLE SHAPE — what the user is asking you to PRODUCE: an answer " +
        "in chat, a written document file, a code change, or read-only research findings. Effort and shape are " +
        "INDEPENDENT: a deep investigation and a one-line fix can cost the same, and a task with no repository bound to " +
        "it cannot be a code change. Also report your CONFIDENCE 0..1: >= 0.6 means you are confident enough to route " +
        "automatically without asking the human; below 0.6 means the task is ambiguous and the operator should confirm " +
        "the effort. Be calibrated — a clear, well-scoped task earns high confidence; a vague one-liner earns low. " +
        "Return ONLY the schema-constrained JSON.";
}
