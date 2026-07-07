using System.Text;
using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;

namespace CodeSpace.Core.Services.Review;

/// <summary>
/// The real model-backed <see cref="IStructuredCritic"/> (Rule 18.3 — an impl in the <c>Review/</c> folder). Mirrors
/// <c>LlmDecisionArbiter</c>'s independent-brain call EXACTLY — resolve the reviewer model row → match the structured
/// client by THAT model's provider → schema-constrained completion — but for two review MODES, and FAILS CLOSED to a
/// <see cref="CriticVerdict.Failed"/> verdict (never throws, cancellation aside), so the caller keeps the producer's
/// original output. The reviewer is the operator-pinned model, else the team's auto-picked brain (so it is independent
/// of a specific producer when the team has &gt; 1 model).
/// </summary>
public sealed class LlmStructuredCritic : IStructuredCritic, IScopedDependency
{
    private readonly ILLMClientRegistry _clientRegistry;
    private readonly IModelPoolSelector _modelSelector;

    public LlmStructuredCritic(ILLMClientRegistry clientRegistry, IModelPoolSelector modelSelector)
    {
        _clientRegistry = clientRegistry;
        _modelSelector = modelSelector;
    }

    /// <summary>The interaction kind every critic review call records under (the journal's intent label) — pinned by a unit test.</summary>
    public const string ReviewCallKind = "critic.review";

    public async Task<CriticVerdict> ReviewAsync(CriticRequest request, Guid teamId, Guid? reviewerModelId, CancellationToken cancellationToken)
    {
        // Re-label the ambient recording scope for the duration of the review — the critic's model call records as
        // "critic.review" instead of inheriting its caller's kind ("supervisor.decision", a planner node's type key),
        // so the run journal can say WHAT the call was doing. One nesting here covers EVERY critic caller. No ambient
        // scope (a call outside any run) ⇒ nothing to re-label.
        using var relabel = LlmCallContext.Current is { } ambient ? LlmCallContext.Push(ambient with { Kind = ReviewCallKind }) : null;

        // NEVER throws (cancellation aside) — the caller relies on always getting a verdict (a failed review = fall back
        // to the original output). Any failure of resolution / the brain call / the parse returns a Failed verdict.
        try
        {
            var rowId = reviewerModelId ?? await ResolveAutoReviewerAsync(teamId, request.ProducerModelRowId, cancellationToken).ConfigureAwait(false);

            if (rowId is not { } id) return CriticVerdict.ReviewFailed(request.Mode, "No reviewer model is available in the team's pool.");

            var pick = await _modelSelector.ResolveByRowIdAsync(teamId, id, cancellationToken).ConfigureAwait(false);

            if (pick == null) return CriticVerdict.ReviewFailed(request.Mode, "The reviewer model is not available in the team's pool.");

            var structured = _clientRegistry.All.OfType<IStructuredLLMClient>().FirstOrDefault(c => string.Equals(c.Provider, pick.Credential.Provider, StringComparison.OrdinalIgnoreCase));

            if (structured == null) return CriticVerdict.ReviewFailed(request.Mode, "No structured-output provider for the reviewer model.");

            var completion = await structured.CompleteStructuredAsync(BuildRequest(request, pick), cancellationToken).ConfigureAwait(false);

            return Project(request.Mode, completion.Json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CriticVerdict.ReviewFailed(request.Mode, "The reviewer could not produce a valid review.");
        }
    }

    /// <summary>Auto-pick the reviewer via the distinct-first ladder: prefer a model DIFFERENT from the producer (a real second opinion), fall back to the producer's own model on a one-model pool — an independent call either way, never a silent no-review. Null only when NOTHING structured-eligible exists.</summary>
    private async Task<Guid?> ResolveAutoReviewerAsync(Guid teamId, Guid? producerRowId, CancellationToken cancellationToken)
    {
        var providers = _clientRegistry.All.OfType<IStructuredLLMClient>().Select(c => c.Provider).ToList();

        return providers.Count == 0 ? null : await _modelSelector.SelectReviewerRowIdAsync(teamId, providers, producerRowId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Project the schema-valid model review into the canonical <see cref="CriticVerdict"/>, FAIL-CLOSED per mode. Internal for direct unit testing.</summary>
    internal static CriticVerdict Project(ReviewMode mode, JsonElement json) => mode switch
    {
        ReviewMode.Improve => ProjectImprove(json),
        _ => ProjectGate(json),
    };

    private static CriticVerdict ProjectGate(JsonElement json)
    {
        var model = json.Deserialize<GateModelReview>(CriticSchema.Options);

        if (model is null) return CriticVerdict.ReviewFailed(ReviewMode.Gate, "The reviewer returned no verdict.");

        var issues = ModelIssueProjection.Project(model.Issues);

        return new CriticVerdict
        {
            Mode = ReviewMode.Gate,
            // SEVERITY-AUTHORITATIVE approval (P1): a gate halts iff at least one issue is a Blocker — the model's raw
            // approved bit is advisory. A Minor/Major-only disapproval no longer halts (the calibration fix), and a
            // Blocker the model under-called with approved:true still halts (the safety catch). The oracle/rubric layer
            // is the deterministic gate for correctness; the critic is advisory calibration over what a human weighs.
            Approved = CriticGatePolicy.Approves(issues),
            Score = model.Score,
            Issues = issues,
            Rationale = Rationale(model.Rationale),
        };
    }

    private static CriticVerdict ProjectImprove(JsonElement json)
    {
        var model = json.Deserialize<ImproveModelReview>(CriticSchema.Options);

        // A critique is the whole point of IMPROVE — a blank one is a failed review (fall back to the original).
        if (model is null || string.IsNullOrWhiteSpace(model.Critique))
            return CriticVerdict.ReviewFailed(ReviewMode.Improve, "The reviewer returned no critique.");

        var issues = ModelIssueProjection.Project(model.Issues);

        return new CriticVerdict
        {
            Mode = ReviewMode.Improve,
            // A MINOR-ONLY critique (all issues are nitpicks) does not warrant a revision round — suppress the critique
            // so the producer keeps its output, while still surfacing the verdict (the review ran, the minors are
            // noted). A critique with no structured issues keeps its revision — an unknown-severity free-text critique
            // must not be silently dropped (fail toward doing the review, the safe direction).
            Critique = CriticGatePolicy.WarrantsRevision(issues) ? model.Critique : null,
            Issues = issues,
            Rationale = Rationale(model.Rationale),
        };
    }

    private static string Rationale(string? raw) => string.IsNullOrWhiteSpace(raw) ? "(the reviewer gave no rationale)" : raw;

    private static StructuredLLMCompletionRequest BuildRequest(CriticRequest request, ModelPoolPick pick) => new()
    {
        Model = pick.ModelId,
        SystemPrompt = request.Mode == ReviewMode.Improve ? ImproveSystemPrompt : GateSystemPrompt,
        UserPrompt = BuildUserPrompt(request),
        JsonSchema = request.Mode == ReviewMode.Improve ? CriticSchema.ImproveSchema : CriticSchema.GateSchema,
        MaxOutputTokens = 2048,
        Temperature = 0.2,
        Credential = pick.Credential,
    };

    /// <summary>Internal test accessor (InternalsVisibleTo) — pins the prompt framing without a real LLM round-trip.</summary>
    internal static string BuildUserPromptForTest(CriticRequest request) => BuildUserPrompt(request);

    private static string BuildUserPrompt(CriticRequest request)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(request.Goal))
        {
            builder.AppendLine($"Goal the {request.ArtifactKind} should serve:");
            builder.AppendLine(request.Goal);
            builder.AppendLine();
        }

        builder.AppendLine($"The {request.ArtifactKind} to review:");
        builder.AppendLine(request.Artifact);
        builder.AppendLine();

        // ⑧ plan-review satisfiability: when the artifact is a PLAN, add the acceptance-verifiability check — the error
        // class (an acceptance that can NEVER pass as written) that dooms a subtask to endless retry. Scoped by the
        // SHARED CriticArtifactKinds.WorkflowPlan constant (an EXACT match, not a "plan" substring that a future kind
        // like "explanation" would trip), so the generic critic is byte-identical for every other kind. The model judges
        // STRUCTURAL satisfiability from the plan text (a rubric/schema check with no rubric/schema, an artifact-dependent
        // check the plan never produces); the grounded reviewer — which has the real code — catches the code-dependent cases.
        if (string.Equals(request.ArtifactKind, CriticArtifactKinds.WorkflowPlan, StringComparison.OrdinalIgnoreCase))
            builder.AppendLine("Also check ACCEPTANCE SATISFIABILITY: for each subtask, can the way the plan declares it 'done' be verified AS WRITTEN? Treat as a BLOCKER any acceptance that can never pass — a rubric / citation / schema check with no rubric or schema supplied, or one requiring an artifact (a repo binding, a built binary, a produced branch) the plan never creates. An unsatisfiable acceptance dooms its subtask to endless retry.");

        builder.AppendLine(request.Mode == ReviewMode.Improve
            ? "Critique it: what is weak, missing, or wrong, and specifically how to improve it to better serve the goal. Return ONLY the schema-constrained JSON."
            : "Judge it: does it soundly achieve the goal? Score it, approve only if there is no material flaw, and list concrete issues. Return ONLY the schema-constrained JSON.");

        return builder.ToString();
    }

    private const string GateSystemPrompt =
        "You are an INDEPENDENT reviewer. You did not write the artifact under review; judge it strictly and fairly on " +
        "its own merits against the stated goal. Ground EVERY issue in evidence (quote the offending part or name its " +
        "precise location — an unevidenced issue is an opinion, not a finding) AND classify its SEVERITY: 'blocker' = " +
        "the artifact is UNFIT for its goal (it would produce wrong, broken, unsafe, or incomplete results, or fails a " +
        "hard requirement); 'major' = a real problem worth fixing that does NOT make it unfit; 'minor' = a nitpick or " +
        "style preference. Set approved=false if and ONLY if you list at least one BLOCKER — a major or minor issue is " +
        "worth surfacing but is not, on its own, grounds to halt. Do NOT inflate severity: reserve 'blocker' for genuine " +
        "unfitness, so a sound artifact with a cosmetic flaw is not blocked. Always give a rationale. Return ONLY the " +
        "schema-constrained JSON.";

    private const string ImproveSystemPrompt =
        "You are an INDEPENDENT reviewer helping improve an artifact you did not write. Critique it against the stated " +
        "goal: identify what is weak, missing, or wrong, and give SPECIFIC, ACTIONABLE guidance the author can apply to " +
        "produce a better revision. Ground every itemised issue in evidence — quote the artifact or name the precise " +
        "location — AND classify its severity ('blocker' = makes it unfit; 'major' = a real problem to fix; 'minor' = a " +
        "nitpick). If the only problems are minor nitpicks, say so plainly — do not manufacture a substantive revision " +
        "for style preferences. Be concrete, not vague. Return ONLY the schema-constrained JSON.";
}
