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
/// returns the original decision, so review is never worse than no review. v1 does NOT block a decision (Gate falls
/// through to the original) — a supervisor decision is re-derivable, so IMPROVE (re-decide) is the natural fit and a
/// hard block→re-decide→force-stop arc is a later slice. A plain class — wired via Autofac <c>RegisterDecorator</c>.</para>
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

        // Doubly-off ⇒ byte-identical: no per-run review baked, OR the operator killed the critic globally.
        if (context.DecisionReviewMode == ReviewMode.None || !CriticToggle.Enabled) return decision;

        var verdict = await _critic.ReviewAsync(
            new CriticRequest { Mode = context.DecisionReviewMode, ArtifactKind = "supervisor decision", Artifact = Render(decision), Goal = ComposeYardstick(context) },
            context.TeamId, context.ReviewerModelId, cancellationToken).ConfigureAwait(false);

        if (verdict.Failed) return decision;   // fail-open — a failed review is never worse than no review

        // IMPROVE: fold the critique into ONE bounded re-decide (Review reset to None ⇒ no recursion), so the decider
        // revises against it. Gate (v1) + a blank critique fall through to the original — we never BLOCK a decision
        // mid-loop (a supervisor decision is re-derivable; a hard block→re-decide→force-stop arc is a later slice).
        if (context.DecisionReviewMode == ReviewMode.Improve && !string.IsNullOrWhiteSpace(verdict.Critique))
            return await _inner.DecideAsync(context with { DecisionReviewMode = ReviewMode.None, ReviewerCritique = verdict.Critique }, cancellationToken).ConfigureAwait(false);

        return decision;
    }

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
