using System.Text;
using CodeSpace.Core.Services.Agents.Review;
using CodeSpace.Core.Services.Review;
using CodeSpace.Core.Services.Workflows.Planning.Planners;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;

namespace CodeSpace.Core.Services.Supervisor.Deciders;

/// <summary>
/// The second application of the generic <see cref="IStructuredCritic"/> primitive (after the planner) — an adversarial
/// review decorator over any <see cref="ISupervisorDecider"/>. Before a turn's <see cref="SupervisorDecision"/> takes its
/// side effect, an INDEPENDENT model reviews it against the goal + acceptance criteria; in <see cref="ReviewMode.Improve"/>
/// it folds the critique into ONE bounded re-decide so the supervisor revises its own call. The mode + reviewer model are
/// BAKED into the immutable run snapshot (unlike the synchronous planner critic), so every turn + every replay reads the
/// same critic config — durability comes for free from the existing config-bake.
///
/// <para>DOUBLY-OFF by default (per-run <see cref="ReviewMode.None"/> + the shared <see cref="CriticToggle"/> kill-switch),
/// so an unconfigured supervisor run is byte-identical — the decorator is a pure passthrough. FAILS OPEN: a failed review
/// returns the original decision, so review is never worse than no review. GATE is HARD (S8): a disapproved decision
/// does not execute — one bounded re-decide against the evidence-attached critique, a second independent review, and a
/// still-disapproved decision ESCALATES to the human via <see cref="SupervisorGateEscalation"/> (the run parks on the
/// standard ask card; approve = one-shot absolution, anything else = guidance). The full policy ladder in one
/// mechanism: model-critic → self-revision → human. A plain class — wired via Autofac <c>RegisterDecorator</c>.</para>
/// </summary>
public sealed class CriticSupervisorDeciderDecorator : ISupervisorDecider
{
    private readonly ISupervisorDecider _inner;
    private readonly IStructuredCritic _critic;
    private readonly IAgentPlanReviewer _agentPlanReviewer;

    public CriticSupervisorDeciderDecorator(ISupervisorDecider inner, IStructuredCritic critic, IAgentPlanReviewer agentPlanReviewer)
    {
        _inner = inner;
        _critic = critic;
        _agentPlanReviewer = agentPlanReviewer;
    }

    public async Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        var decision = await _inner.DecideAsync(context, cancellationToken).ConfigureAwait(false);

        // The applicable mode: a PLAN decision prefers the plan-scoped critic (the tier-generic "plan critic",
        // S4e) so the operator can critique plans without paying a review on every spawn/merge/stop; everything
        // else — and plans when no plan critic is set — rides the all-step decision critic. Doubly-off ⇒
        // byte-identical (no per-run review baked, OR the operator killed the critic globally).
        var mode = ReviewModeFor(context, decision);

        if (mode == ReviewMode.None || !CriticToggle.Enabled) return decision;

        // P1d checks-before-critics: a structurally-invalid PLAN (a dangling DependsOn or a cycle) is force-stopped by
        // the free Tier-0 SupervisorPlanValidator in the post-decision gate no matter what a critic rules, so paying up
        // to three model calls to critique it first is pure waste. Skip the critic and let the gate reject it. The check
        // is pure + returns null for every non-plan decision and every well-formed plan, so a valid decision is reviewed
        // exactly as before — only a doomed plan short-circuits.
        if (SupervisorPlanValidator.Validate(decision) != null) return decision;

        // S8 one-shot absolution: the LATEST prior decision is an ANSWERED gate-escalation card. Approve ⇒ the
        // human saw the critique and ruled — this turn's decision proceeds unreviewed (positional latest-only, so
        // absolution never leaks past the very next decision). Any other answer is guidance the inner decider
        // already read from its context — the fresh decision earns a fresh review below.
        if (SupervisorGateEscalation.TryReadAnswer(context.PriorDecisions, out var humanApproved) && humanApproved)
            return decision;

        var (verdict, agentReviewed) = await ReviewAsync(mode, decision, context, cancellationToken).ConfigureAwait(false);

        if (verdict.Failed) return decision;   // fail-open — a failed review is never worse than no review

        var scope = decision.Kind == SupervisorDecisionKinds.Plan ? "plan" : "decision";

        // IMPROVE: fold the critique into ONE bounded re-decide (BOTH review modes reset to None ⇒ no recursion),
        // so the decider revises against it. An AGENT verdict is Gate-shaped (no critique field) — its disapproval
        // composes the critique from rationale + evidence-attached issues; an agent APPROVAL (or a blank critique)
        // falls through to the original. EVERY verdict rides the surviving decision (draft attributed) so the
        // journal shows the exchange — an AGENT verdict rides flagged ViaAgent (its reviewer run is its own beat).
        if (mode == ReviewMode.Improve)
        {
            var critique = !string.IsNullOrWhiteSpace(verdict.Critique) ? verdict.Critique
                : verdict.Mode == ReviewMode.Gate && !verdict.Approved ? ComposeGateCritique(verdict)
                : null;

            if (!string.IsNullOrWhiteSpace(critique))
            {
                var revision = await RedecideAsync(context, critique, cancellationToken).ConfigureAwait(false);

                return CarryReview(revision, verdict, agentReviewed, scope, draft: decision);
            }

            return CarryReview(decision, verdict, agentReviewed, scope, draft: null);
        }

        // GATE (S8 hard semantics): a disapproved decision does NOT execute. The ladder — one bounded re-decide
        // against the critique (the same self-heal Improve gets), a SECOND independent review of the revision, and
        // only a still-disapproved decision escalates: the run parks on the standard ask card carrying the blocked
        // verb + the evidence-attached critique, and the HUMAN rules (approve = one-shot absolution next turn;
        // anything else = guidance the next decide reads). Fail-open on a failed second review. Every rung's MODEL
        // verdict rides the surviving decision so the journal shows the whole ladder.
        if (mode == ReviewMode.Gate && !verdict.Approved)
        {
            var revised = await RedecideAsync(context, ComposeGateCritique(verdict), cancellationToken).ConfigureAwait(false);

            revised = CarryReview(revised, verdict, agentReviewed, scope, draft: decision);

            var (second, secondByAgent) = await ReviewAsync(ReviewMode.Gate, revised, context, cancellationToken).ConfigureAwait(false);

            if (second.Failed || second.Approved) return CarryReview(revised, second, secondByAgent, scope, draft: null);

            // The escalation ask INHERITS the ladder's whole chain (first flagged verdict + draft attribution + the
            // still-disapproving second verdict) — the parked card is the ladder's product, so its tape row tells it.
            // The FIRST verdict rides too, so the card names which issues the revision could NOT resolve (convergence).
            return SupervisorGateEscalation.IntoAskHuman(revised, second, priorVerdict: verdict) with { Reviews = revised.Reviews.Concat(ToReviews(second, secondByAgent, scope, null)).ToList() };
        }

        return CarryReview(decision, verdict, agentReviewed, scope, draft: null);
    }

    /// <summary>Append a verdict to the decision's carried review chain — a FAILED verdict never reaches here. An AGENT verdict rides FLAGGED <see cref="SupervisorDecisionReview.ViaAgent"/> (its reviewer run is already a first-class journal beat, so the projection skips beating it twice — the entry exists for the DRAFT attribution). The draft, when a revision followed, is attributed by verb + the model call that authored it.</summary>
    private static SupervisorDecision CarryReview(SupervisorDecision survivor, CriticVerdict verdict, bool agentReviewed, string scope, SupervisorDecision? draft) =>
        survivor with { Reviews = survivor.Reviews.Concat(ToReviews(verdict, agentReviewed, scope, draft)).ToList() };

    private static IReadOnlyList<SupervisorDecisionReview> ToReviews(CriticVerdict verdict, bool agentReviewed, string scope, SupervisorDecision? draft)
    {
        if (verdict.Failed) return Array.Empty<SupervisorDecisionReview>();

        return new[]
        {
            new SupervisorDecisionReview
            {
                Approved = verdict.Approved,
                Rationale = !string.IsNullOrWhiteSpace(verdict.Critique) ? verdict.Critique! : verdict.Rationale,
                Issues = verdict.Issues.Select(i => i.ToString()).ToList(),
                Scope = scope,
                DraftAttribution = draft is null ? null : DescribeDraft(draft),
                ViaAgent = agentReviewed,
            },
        };
    }

    /// <summary>The discarded draft's attribution line — its verb + the model call that authored it, so the once-anonymous "model call · N tokens" reads as "the flagged draft". Internal for direct unit pinning.</summary>
    internal static string DescribeDraft(SupervisorDecision draft)
    {
        if (draft.Usage is not { } usage) return $"{draft.Kind} draft";

        var tokens = (usage.InputTokens ?? 0) + (usage.OutputTokens ?? 0);

        return tokens > 0 ? $"{draft.Kind} draft · authored via {usage.Model} · {tokens:N0} tokens" : $"{draft.Kind} draft · authored via {usage.Model}";
    }

    /// <summary>
    /// One independent review of a decision under the given mode — the shared call the first pass and the hard-Gate's
    /// second pass both use. A PLAN decision with the D① opt-in reviews GROUNDED first: a real read-only agent clones
    /// the run's repository and verifies the plan against the actual tree, laddering down to the in-process model
    /// critic when the agent can't produce a verdict (a grounded review is never worse than a text review).
    /// </summary>
    private async Task<(CriticVerdict Verdict, bool AgentReviewed)> ReviewAsync(ReviewMode mode, SupervisorDecision decision, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        var verdict = decision.Kind == SupervisorDecisionKinds.Plan && context.ReviewerAgent && context.AgentProfile?.RepositoryId is { } repositoryId
            ? await _agentPlanReviewer.ReviewAsync(new PlanReviewRequest
            {
                PlanArtifact = Render(decision),
                Goal = ComposeYardstick(context),
                RepositoryId = repositoryId,
                TeamId = context.TeamId,
                WorkflowRunId = context.SupervisorRunId,
                NodeId = context.NodeId,
                ReviewerModelId = context.ReviewerModelId,
                // S1: the reviewer verifies the plan against the SAME immutable base every spawned agent materializes.
                PinnedSha = context.AgentProfile?.PinnedSha,
            }, cancellationToken).ConfigureAwait(false)
            : CriticVerdict.ReviewFailed(mode, "agent-reviewer: not requested");

        if (!verdict.Failed) return (verdict, true);

        return (await _critic.ReviewAsync(
            new CriticRequest { Mode = mode, ArtifactKind = decision.Kind == SupervisorDecisionKinds.Plan ? CriticArtifactKinds.WorkflowPlan : CriticArtifactKinds.SupervisorDecision, Artifact = Render(decision), Goal = ComposeYardstick(context), ProducerModelRowId = context.SupervisorModelId },
            context.TeamId, context.ReviewerModelId, cancellationToken).ConfigureAwait(false), false);
    }

    /// <summary>The interaction kind the bounded re-decide's model call records under (vs the ambient turn's "supervisor.decision") — the journal reads "revision", not a second anonymous decide. Pinned by a unit test.</summary>
    public const string ReviseCallKind = "supervisor.revise";

    /// <summary>ONE bounded re-decide against a critique — both review modes reset to None so the re-decide can never recurse into another review. The ambient recording scope is re-labeled <see cref="ReviseCallKind"/> for the call's duration, so the tape distinguishes the draft call from the revision.</summary>
    private async Task<SupervisorDecision> RedecideAsync(SupervisorTurnContext context, string critique, CancellationToken cancellationToken)
    {
        using var relabel = Workflows.Llm.LlmCallContext.Current is { } ambient ? Workflows.Llm.LlmCallContext.Push(ambient with { Kind = ReviseCallKind }) : null;

        return await _inner.DecideAsync(context with { DecisionReviewMode = ReviewMode.None, PlanReviewMode = ReviewMode.None, ReviewerCritique = critique }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The Gate verdict as a critique the re-decide can act on — rationale + the evidence-attached issues (Gate's schema has no critique field; the issues ARE the actionable content). Internal for direct unit pinning.</summary>
    internal static string ComposeGateCritique(CriticVerdict verdict) =>
        verdict.Issues.Count > 0 ? $"{verdict.Rationale} Issues: {string.Join("; ", verdict.Issues)}" : verdict.Rationale;

    /// <summary>A plan decision reviews under the PLAN critic when set, else the all-step decision critic; every other decision only under the decision critic. Internal for direct unit pinning.</summary>
    internal static ReviewMode ReviewModeFor(SupervisorTurnContext context, SupervisorDecision decision) =>
        decision.Kind == SupervisorDecisionKinds.Plan && context.PlanReviewMode != ReviewMode.None
            ? context.PlanReviewMode
            : context.DecisionReviewMode;

    /// <summary>The decision rendered for the reviewer — the verb + its canonical payload (a structured object, directly judgeable).</summary>
    private static string Render(SupervisorDecision decision) => $"{decision.Kind}: {decision.PayloadJson}";

    /// <summary>The yardstick the reviewer judges against: the run goal, plus the acceptance criteria when the operator set them.</summary>
    private static string ComposeYardstick(SupervisorTurnContext context)
    {
        if (context.AcceptanceCriteria is not { Count: > 0 } criteria) return context.Goal;

        var builder = new StringBuilder(context.Goal);
        builder.AppendLine().AppendLine().AppendLine("Acceptance criteria (the definition of done):");
        foreach (var c in criteria) builder.AppendLine($"- {c}");

        return builder.ToString();
    }
}
