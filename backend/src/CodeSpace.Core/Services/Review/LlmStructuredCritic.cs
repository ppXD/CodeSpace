using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Review;

/// <summary>
/// The real model-backed <see cref="IStructuredCritic"/> (Rule 18.3 — an impl in the <c>Review/</c> folder). Mirrors
/// <c>LlmDecisionArbiter</c>'s independent-brain call EXACTLY — resolve the reviewer model row → match the structured
/// client by THAT model's provider → schema-constrained completion — but for two review MODES, and FAILS CLOSED to a
/// <see cref="CriticVerdict.Failed"/> verdict (never throws, cancellation aside), so the caller keeps the producer's
/// original output. The reviewer is the operator-pinned model, else the team's auto-picked brain. A different configured
/// name alone does not establish independence: gateways may alias the same backing model.
///
/// <para>An artifact too large for one call is reviewed in parts sized to the picked row's declared window
/// (<see cref="CriticArtifactParts"/>): every part must approve, and a part that cannot be reviewed fails the whole
/// review. One part — every artifact that fits — is the one call it always was.</para>
/// </summary>
public sealed partial class LlmStructuredCritic : IStructuredCritic, IScopedDependency
{
    private readonly ILLMClientRegistry _clientRegistry;
    private readonly IModelPoolSelector _modelSelector;
    private readonly ILogger<LlmStructuredCritic> _logger;

    public LlmStructuredCritic(ILLMClientRegistry clientRegistry, IModelPoolSelector modelSelector, ILogger<LlmStructuredCritic> logger)
    {
        _clientRegistry = clientRegistry;
        _modelSelector = modelSelector;
        _logger = logger;
    }

    /// <summary>The interaction kind a critic review call records under by default (the journal's intent label) — pinned by a unit test.</summary>
    public const string ReviewCallKind = "critic.review";

    /// <summary>
    /// The kind the OUTPUT review names via <see cref="CriticRequest.CallKind"/> — the one rung that examines a
    /// produced RESULT rather than an intention (a plan, a decision). Distinct from <see cref="ReviewCallKind"/> so a
    /// consumer asking "did anything check what this run produced?" cannot be answered yes by a decision review.
    /// Pinned by a unit test (Rule 8) — the Room's ledger probe is built off this const.
    /// </summary>
    public const string OutputReviewCallKind = "critic.output";

    /// <summary>The payload <c>kind</c> a <see cref="WorkflowRunRecordTypes.ReviewSkipped"/> record carries — the sibling of <see cref="ReviewCallKind"/> for the review that did NOT happen. Pinned by a unit test (Rule 8).</summary>
    public const string SkippedCallKind = "critic.skipped";

    /// <summary>How much of a fault's message rides the machine-readable reason — enough to name the cause (a revoked key, a schema refusal), never a whole provider payload on the ledger.</summary>
    private const int ReasonMessageHeadChars = 200;

    public async Task<CriticVerdict> ReviewAsync(CriticRequest request, Guid teamId, Guid? reviewerModelId, CancellationToken cancellationToken)
    {
        // Re-label the ambient recording scope for the duration of the review — the critic's model call records as
        // "critic.review" instead of inheriting its caller's kind ("supervisor.decision", a planner node's type key),
        // so the run journal can say WHAT the call was doing. One nesting here covers EVERY critic caller. A request
        // that names its OWN kind keeps it (the output review's "critic.output" — the one rung judging a RESULT). No
        // ambient scope (a call outside any run) ⇒ nothing to re-label.
        using var relabel = LlmCallContext.Current is { } ambient ? LlmCallContext.Push(ambient with { Kind = request.CallKind ?? ReviewCallKind }) : null;

        // NEVER throws (cancellation aside) — the caller relies on always getting a verdict (a failed review = fall back
        // to the original output). Any failure of resolution / the brain call / the parse returns a Failed verdict.
        try
        {
            var producerModel = request.ProducerModel ?? new ReviewModelIdentity { ModelCredentialModelId = request.ProducerModelRowId };
            var rowId = reviewerModelId ?? await ResolveAutoReviewerAsync(teamId, producerModel, cancellationToken).ConfigureAwait(false);

            if (rowId is not { } id) return await SkippedAsync(request, "No reviewer model is available in the team's pool.").ConfigureAwait(false);

            var pick = await _modelSelector.ResolveByRowIdAsync(teamId, id, cancellationToken).ConfigureAwait(false);

            if (pick == null) return await SkippedAsync(request, "The reviewer model is not available in the team's pool.").ConfigureAwait(false);

            var structured = _clientRegistry.All.OfType<IStructuredLLMClient>().FirstOrDefault(c => string.Equals(c.Provider, pick.Credential.Provider, StringComparison.OrdinalIgnoreCase));

            if (structured == null) return await SkippedAsync(request, "No structured-output provider for the reviewer model.").ConfigureAwait(false);

            // The artifact in parts the picked row's window holds (CriticArtifactParts) — one part, and so one call with
            // the byte-identical prompt, whenever it fits.
            if (PartsOf(request, pick) is not { Count: <= CriticArtifactParts.MaxParts } parts)
                return await SkippedAsync(request, $"The {request.ArtifactKind} does not fit {CriticArtifactParts.MaxParts} review calls on the reviewer model's {(pick.ContextWindowTokens is { } window ? $"declared {window}-token context window" : "default per-call size")}, so it was not reviewed.").ConfigureAwait(false);

            var verdicts = new List<CriticVerdict>();
            string? observedModel = null;

            for (var part = 0; part < parts.Count; part++)
            {
                var completion = await structured.CompleteStructuredAsync(BuildRequest(request, pick, parts, part), cancellationToken).ConfigureAwait(false);
                var verdict = Project(request.Mode, completion.Json);

                if (verdict.Failed) return await SkippedAsync(request, parts.Count == 1 ? verdict.Rationale : $"part {part + 1} of {parts.Count}: {verdict.Rationale}").ConfigureAwait(false);

                observedModel ??= completion.ObservedModel;
                verdicts.Add(verdict);
            }

            // Only the provider-reported identity can accompany a completed review. A missing observation stays unknown;
            // the selected alias and compatibility Model fallback cannot establish reviewer diversity.
            var reviewerModel = ObservedLlmModel.FromWire(observedModel, pick.Credential);
            return CriticArtifactParts.Merge(request.Mode, verdicts) with { ReviewerModel = reviewerModel, Independence = IndependenceOf(request.ProducerModel?.ObservedModel, reviewerModel) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await SkippedAsync(request, Reason(ex)).ConfigureAwait(false);
        }
    }

    internal static ReviewModelIndependence IndependenceOf(string? producerObservedModel, string? reviewerObservedModel) =>
        string.IsNullOrWhiteSpace(producerObservedModel) || string.IsNullOrWhiteSpace(reviewerObservedModel)
            ? ReviewModelIndependence.Unknown
            : string.Equals(producerObservedModel, reviewerObservedModel, StringComparison.OrdinalIgnoreCase)
                ? ReviewModelIndependence.SameBackingModel
                : ReviewModelIndependence.DistinctBackingModel;

    /// <summary>
    /// The one exit for a review that did NOT happen: say so at Warning, leave a DURABLE user-visible beat on the run's
    /// ledger, and hand the caller its fail-open verdict carrying the same reason. Every silent-review path funnels
    /// through here, so a revoked reviewer credential can no longer turn a configured review off with zero trace.
    /// </summary>
    private async Task<CriticVerdict> SkippedAsync(CriticRequest request, string reason)
    {
        // MASK BEFORE THE REASON ESCAPES. A fault's message is not a curated string: an LlmApiException's message IS
        // the gateway's raw error body, and a malformed-response fault can carry a content preview. This reason reaches
        // FOUR durable or human-visible places — the log line below, the ledger beat, the verdict rationale the planner
        // folds into the plan's risks, and outcome_json — so it passes the run's persistence redactor exactly once,
        // here, the same masking the model-call capture applies. No configured redactor ⇒ verbatim (today's behavior).
        reason = Redacted(reason);

        _logger.LogWarning("The independent {ReviewMode} review of a {ArtifactKind} did not run, so the producer's original output stands unreviewed: {Reason}", request.Mode, request.ArtifactKind, reason);

        await RecordSkippedAsync(request, reason).ConfigureAwait(false);

        return CriticVerdict.ReviewFailed(request.Mode, reason);
    }

    /// <summary>The reason with the run's exact-value secrets masked, off the ambient scope's persistence redactor. A call outside any run, or a run with no redactor configured, reads through verbatim. Internal for direct unit pinning.</summary>
    internal static string Redacted(string reason) =>
        LlmCallContext.Current?.CaptureRedactor is { } redactor ? redactor.Redact(reason).Value ?? reason : reason;

    /// <summary>
    /// Append the <see cref="WorkflowRunRecordTypes.ReviewSkipped"/> beat onto the ambient run's ledger. FAIL-OPEN in
    /// both directions: a call outside any run (no ambient scope) records nothing, and a ledger write that faults is
    /// swallowed — saying a review was skipped may never itself break the run. Written on <see cref="CancellationToken.None"/>
    /// because the caller's token is commonly cancelled by the very failure being recorded.
    ///
    /// <para><c>agentRunId</c> rides the key regardless (null for a plan/decision review, valued for an OUTPUT
    /// review) — the same "key always present" shape <c>reviewerModel</c> already uses, so a reader tells "no unit
    /// named" from "the key was never written" off one stable payload instead of two.</para>
    /// </summary>
    private async Task RecordSkippedAsync(CriticRequest request, string reason)
    {
        if (LlmCallContext.Current is not { } scope) return;

        try
        {
            var payload = JsonSerializer.SerializeToElement(new { kind = SkippedCallKind, mode = request.Mode.ToString(), artifact_kind = request.ArtifactKind, reason, agentRunId = request.AgentRunId });

            await scope.Logger.RecordInteractionAsync(scope.RunId, WorkflowRunRecordTypes.ReviewSkipped, scope.NodeId, scope.IterationKey, Guid.NewGuid(), parentRecordId: null, payload, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The review-skipped beat of workflow run {WorkflowRunId} could not be recorded; the skipped review is reported by this log line alone", scope.RunId);
        }
    }

    /// <summary>The machine-readable reason a fault gives a skipped review — the exception TYPE (the stable half a consumer can group on) plus the head of its message (the human half). Internal for direct unit pinning.</summary>
    internal static string Reason(Exception ex)
    {
        var message = ex.Message.ReplaceLineEndings(" ").Trim();

        return message.Length <= ReasonMessageHeadChars ? $"{ex.GetType().Name}: {message}" : $"{ex.GetType().Name}: {message[..ReasonMessageHeadChars]}…";
    }

    /// <summary>Auto-pick the reviewer via the distinct-first ladder: prefer a model DIFFERENT from the producer (a real second opinion), fall back to the producer's own model on a one-model pool — an independent call either way, never a silent no-review. Null only when NOTHING structured-eligible exists.</summary>
    private async Task<Guid?> ResolveAutoReviewerAsync(Guid teamId, ReviewModelIdentity producerModel, CancellationToken cancellationToken)
    {
        var providers = _clientRegistry.All.OfType<IStructuredLLMClient>().Select(c => c.Provider).ToList();

        return providers.Count == 0 ? null : await _modelSelector.SelectReviewerRowIdAsync(teamId, providers, producerModel, cancellationToken).ConfigureAwait(false);
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
}
