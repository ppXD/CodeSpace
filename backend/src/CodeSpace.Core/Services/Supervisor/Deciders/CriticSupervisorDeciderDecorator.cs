using System.Text;
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

    public CriticSupervisorDeciderDecorator(ISupervisorDecider inner, IStructuredCritic critic)
    {
        _inner = inner;
        _critic = critic;
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

        // S8 one-shot absolution: the LATEST prior decision is an ANSWERED gate-escalation card. Approve ⇒ the
        // human saw the critique and ruled — this turn's decision proceeds unreviewed (positional latest-only, so
        // absolution never leaks past the very next decision). Any other answer is guidance the inner decider
        // already read from its context — the fresh decision earns a fresh review below.
        if (SupervisorGateEscalation.TryReadAnswer(context.PriorDecisions, out var humanApproved) && humanApproved)
            return decision;

        var verdict = await ReviewAsync(mode, decision, context, cancellationToken).ConfigureAwait(false);

        if (verdict.Failed) return decision;   // fail-open — a failed review is never worse than no review

        // IMPROVE: fold the critique into ONE bounded re-decide (BOTH review modes reset to None ⇒ no recursion),
        // so the decider revises against it. A blank critique falls through to the original.
        if (mode == ReviewMode.Improve && !string.IsNullOrWhiteSpace(verdict.Critique))
            return await RedecideAsync(context, verdict.Critique, cancellationToken).ConfigureAwait(false);

        // GATE (S8 hard semantics): a disapproved decision does NOT execute. The ladder — one bounded re-decide
        // against the critique (the same self-heal Improve gets), a SECOND independent review of the revision, and
        // only a still-disapproved decision escalates: the run parks on the standard ask card carrying the blocked
        // verb + the evidence-attached critique, and the HUMAN rules (approve = one-shot absolution next turn;
        // anything else = guidance the next decide reads). Fail-open on a failed second review.
        if (mode == ReviewMode.Gate && !verdict.Approved)
        {
            var revised = await RedecideAsync(context, ComposeGateCritique(verdict), cancellationToken).ConfigureAwait(false);

            var second = await ReviewAsync(ReviewMode.Gate, revised, context, cancellationToken).ConfigureAwait(false);

            if (second.Failed || second.Approved) return revised;

            return SupervisorGateEscalation.IntoAskHuman(revised, second);
        }

        return decision;
    }

    /// <summary>One independent review of a decision under the given mode — the shared call the first pass and the hard-Gate's second pass both use.</summary>
    private async Task<CriticVerdict> ReviewAsync(ReviewMode mode, SupervisorDecision decision, SupervisorTurnContext context, CancellationToken cancellationToken) =>
        await _critic.ReviewAsync(
            new CriticRequest { Mode = mode, ArtifactKind = decision.Kind == SupervisorDecisionKinds.Plan ? "workflow plan" : "supervisor decision", Artifact = Render(decision), Goal = ComposeYardstick(context), ProducerModelRowId = context.SupervisorModelId },
            context.TeamId, context.ReviewerModelId, cancellationToken).ConfigureAwait(false);

    /// <summary>ONE bounded re-decide against a critique — both review modes reset to None so the re-decide can never recurse into another review.</summary>
    private async Task<SupervisorDecision> RedecideAsync(SupervisorTurnContext context, string critique, CancellationToken cancellationToken) =>
        await _inner.DecideAsync(context with { DecisionReviewMode = ReviewMode.None, PlanReviewMode = ReviewMode.None, ReviewerCritique = critique }, cancellationToken).ConfigureAwait(false);

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
