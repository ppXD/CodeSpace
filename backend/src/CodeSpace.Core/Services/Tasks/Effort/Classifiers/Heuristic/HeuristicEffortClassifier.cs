using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Tasks.Effort;

namespace CodeSpace.Core.Services.Tasks.Effort.Classifiers.Heuristic;

/// <summary>
/// The DETERMINISTIC BASELINE effort classifier (Rule 18.3 — one impl beside its variant folder): derives
/// generic <see cref="EffortSignals"/> from the seed goal via TRANSPARENT keyword heuristics, then lets
/// <see cref="EffortPolicy"/> decide the tier. It suggests the only shipped recipe (single-agent).
///
/// <para><b>HONESTY CONSTRAINT — the key contract:</b> its <see cref="EffortDecision.Confidence"/> is ALWAYS
/// strictly below <see cref="EffortPolicy.ConfirmConfidenceFloor"/> (capped at <see cref="ConfidenceCap"/>), so
/// the router's auto path ALWAYS produces a confirm card. The heuristic NEVER silently decides effort — it makes
/// a cheap, rule-based guess and ALWAYS asks the operator to confirm. This is NOT fake intelligence: it is
/// transparently rule-based, low-confidence, and always-confirms. The (deferred) <c>structured_llm</c> classifier
/// SUPERSEDES it with real model-derived confidence that can clear the floor and route without a confirm — but
/// the routing logic, the policy table, and the registry seam are unchanged (the classifier only emits better
/// data). The fake-classifier contract test proves a new strategy plugs in with zero core edit.</para>
/// </summary>
public sealed class HeuristicEffortClassifier : IEffortClassifier, ISingletonDependency
{
    /// <summary>This classifier's open kind string — also the registry's <c>Default</c> selector.</summary>
    public const string ClassifierKind = "heuristic";

    /// <summary>The confidence ceiling — strictly below <see cref="EffortPolicy.ConfirmConfidenceFloor"/> so the heuristic ALWAYS triggers a confirm card. The honesty invariant, pinned by a unit test.</summary>
    public const double ConfidenceCap = 0.5;

    public string Kind => ClassifierKind;

    public Task<EffortDecision> ClassifyAsync(EffortRouteRequest request, CancellationToken ct)
    {
        var goal = request.Seed.Goal ?? "";

        var signals = DeriveSignals(goal);

        var decision = new EffortDecision
        {
            Signals = signals,
            SuggestedEffort = EffortPolicy.Decide(signals, requestedEffort: null),
            SuggestedRecipe = TaskRecipeKinds.SingleAgent,
            Confidence = ComputeConfidence(goal),
            Rationale = BuildRationale(signals),
            ClassifierKind = ClassifierKind,
        };

        return Task.FromResult(decision);
    }

    /// <summary>Derive the generic, task-type-agnostic signals from transparent keyword groups in the goal — no "is this a refactor?" branch, just observable properties of the work.</summary>
    private static EffortSignals DeriveSignals(string goal) => new()
    {
        NeedsCodeChange = ContainsAny(goal, CodeChangeVerbs),
        CrossFile = ContainsAny(goal, CrossFileWords),
        NeedsTestsOrCi = ContainsAny(goal, TestWords),
        RiskySideEffects = ContainsAny(goal, RiskyWords),
        Ambiguous = goal.Trim().Length < AmbiguousLengthThreshold,
        EstimatedCostTier = EstimateCostTier(goal),
    };

    /// <summary>A rough cost tier from goal length — a longer goal implies more surface, so high/medium/low by two length thresholds.</summary>
    private static string EstimateCostTier(string goal) =>
        goal.Length >= HighCostLength ? "high" : goal.Length >= MediumCostLength ? "medium" : "low";

    /// <summary>The confidence — a cheap function of goal length, ALWAYS capped strictly below the confirm floor so the heuristic always asks the operator.</summary>
    private static double ComputeConfidence(string goal)
    {
        var raw = Math.Clamp(goal.Trim().Length / 120.0, 0.1, 1.0);

        return Math.Min(raw, ConfidenceCap);
    }

    /// <summary>A short why-this-tier line for the confirm card, listing which generic signals fired.</summary>
    private static string BuildRationale(EffortSignals signals)
    {
        var fired = new List<string>();

        if (signals.RiskySideEffects) fired.Add("risky side effects");
        if (signals.NeedsCodeChange) fired.Add("code change");
        if (signals.CrossFile) fired.Add("cross-file");
        if (signals.NeedsTestsOrCi) fired.Add("tests/CI");

        var basis = fired.Count == 0 ? "no strong signals" : string.Join(", ", fired);

        return $"Heuristic guess (cost tier {signals.EstimatedCostTier}) from: {basis}. Please confirm the effort.";
    }

    private static bool ContainsAny(string goal, IReadOnlyList<string> words) =>
        words.Any(w => goal.Contains(w, StringComparison.OrdinalIgnoreCase));

    private const int AmbiguousLengthThreshold = 24;
    private const int MediumCostLength = 80;
    private const int HighCostLength = 200;

    private static readonly IReadOnlyList<string> CodeChangeVerbs = new[] { "fix", "add", "implement", "refactor", "update", "build", "change", "write", "create", "remove" };
    private static readonly IReadOnlyList<string> CrossFileWords = new[] { "across", "multiple", "all ", "several", "every", "throughout", "codebase" };
    private static readonly IReadOnlyList<string> TestWords = new[] { "test", "ci", "coverage", "spec", "lint" };
    private static readonly IReadOnlyList<string> RiskyWords = new[] { "delete", "drop", "migrate", "migration", "deploy", "production", "prod", "secret", "credential", "rotate" };
}
