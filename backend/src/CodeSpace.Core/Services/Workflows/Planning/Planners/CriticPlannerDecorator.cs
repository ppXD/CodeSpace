using System.Text;
using CodeSpace.Core.Services.Agents.Review;
using CodeSpace.Core.Services.Review;
using CodeSpace.Messages.Dtos.Workflows.Planning;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;

namespace CodeSpace.Core.Services.Workflows.Planning.Planners;

/// <summary>
/// The first application of the generic <see cref="IStructuredCritic"/> primitive — an adversarial-review decorator over
/// any <see cref="IWorkflowPlanner"/> (the owner's "send my plan to another model and combine the critique" pattern). It
/// is DOUBLY-OFF by default (per-request <see cref="ReviewMode.None"/> + the <see cref="CriticToggle"/> kill-switch), so
/// an unconfigured plan is byte-identical to the bare planner. When on, it reviews the plan with an INDEPENDENT model and
/// either IMPROVES it (re-plans once with the critique) or GATES it (annotates the plan with the reviewer's verdict — it
/// never discards a usable plan at this stage). Fails OPEN: a failed review returns the original plan, so review is never
/// worse than no review. A plain class (not an <c>IScopedDependency</c>) — wired via Autofac <c>RegisterDecorator</c>.
/// </summary>
public sealed class CriticPlannerDecorator : IWorkflowPlanner
{
    private readonly IWorkflowPlanner _inner;
    private readonly IStructuredCritic _critic;
    private readonly IAgentPlanReviewer _agentReviewer;

    public CriticPlannerDecorator(IWorkflowPlanner inner, IStructuredCritic critic, IAgentPlanReviewer agentReviewer)
    {
        _inner = inner;
        _critic = critic;
        _agentReviewer = agentReviewer;
    }

    public async Task<PlannedWorkflow> PlanAsync(WorkflowPlanRequest request, CancellationToken cancellationToken)
    {
        var plan = await _inner.PlanAsync(request, cancellationToken).ConfigureAwait(false);

        // Doubly-off ⇒ byte-identical: no per-request review requested, OR the operator killed the critic globally.
        if (request.Review == ReviewMode.None || !CriticToggle.Enabled) return plan;

        // D① reviewer ladder: an opted-in GROUNDED review first — a real read-only agent clones the plan's target
        // repository and verifies the plan against the actual tree (assumptions, feasibility, already-done work) —
        // laddering DOWN to the in-process model critic (a text review) when the agent can't produce a verdict.
        // A grounded review is never worse than a text review, and a text review is never worse than none.
        var verdict = request.ReviewerAgent && request.RepositoryId is { } repositoryId
            ? await _agentReviewer.ReviewAsync(new PlanReviewRequest
            {
                PlanArtifact = Render(plan),
                Goal = request.TaskText,
                RepositoryId = repositoryId,
                TeamId = request.TeamId,
                WorkflowRunId = request.WorkflowRunId,
                NodeId = request.NodeId,
                ReviewerModelId = request.ReviewerModelId,
                PinnedSha = request.PinnedSha,
            }, cancellationToken).ConfigureAwait(false)
            : CriticVerdict.ReviewFailed(request.Review, "agent-reviewer: not requested");

        if (verdict.Failed)
            verdict = await _critic.ReviewAsync(
                new CriticRequest { Mode = request.Review, ArtifactKind = CriticArtifactKinds.WorkflowPlan, Artifact = Render(plan), Goal = request.TaskText, ProducerModelRowId = request.BrainModelId },
                request.TeamId, request.ReviewerModelId, cancellationToken).ConfigureAwait(false);

        if (verdict.Failed) return plan;   // fail-open — a failed review is never worse than no review

        return request.Review switch
        {
            ReviewMode.Improve => await ImproveAsync(request, plan, verdict, cancellationToken).ConfigureAwait(false),
            ReviewMode.Gate => Annotate(plan, verdict),
            _ => plan,
        };
    }

    /// <summary>IMPROVE: re-plan ONCE through the BARE inner planner (<see cref="ReviewMode.None"/> ⇒ no recursion) with the critique folded into the request, so the producer revises against it. An AGENT verdict is Gate-shaped (no critique field) — its rationale + evidence-attached issues compose the critique. A blank critique falls back to the original.</summary>
    private async Task<PlannedWorkflow> ImproveAsync(WorkflowPlanRequest request, PlannedWorkflow plan, CriticVerdict verdict, CancellationToken cancellationToken)
    {
        var critique = EffectiveCritique(verdict);

        if (string.IsNullOrWhiteSpace(critique)) return plan;

        return await _inner.PlanAsync(request with { Review = ReviewMode.None, ReviewerAgent = false, ReviewerCritique = critique }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The actionable critique in a verdict of EITHER shape: the Improve critique when present; a GATE-shaped DISAPPROVAL (an agent reviewer always answers Gate-shaped) composes rationale + evidence-attached issues; anything else — an approval, or an Improve verdict with a blank critique — yields nothing to revise against. Internal for direct unit pinning.</summary>
    internal static string? EffectiveCritique(CriticVerdict verdict) =>
        !string.IsNullOrWhiteSpace(verdict.Critique) ? verdict.Critique
        : verdict.Mode == ReviewMode.Gate && !verdict.Approved
            ? (verdict.Issues.Count > 0 ? $"{verdict.Rationale} Issues: {string.Join("; ", verdict.Issues)}" : verdict.Rationale)
            : null;

    /// <summary>GATE at the planner stage: NEVER discard a usable plan — surface the reviewer's issues + verdict as RISKS, where the human who reviews the projected definition will see them. (A hard reject belongs at a downstream shipping gate, not here.)</summary>
    private static PlannedWorkflow Annotate(PlannedWorkflow plan, CriticVerdict verdict)
    {
        var header = $"Independent review — {(verdict.Approved ? "approved" : "flagged concerns")}{(verdict.Score is { } s ? $" (score {s}/100)" : "")}: {verdict.Rationale}";

        var risks = plan.Risks.Concat(verdict.Issues.Select(i => $"Reviewer: {i}")).Append(header).ToList();

        return plan with { Risks = risks };
    }

    private static string Render(PlannedWorkflow plan)
    {
        var b = new StringBuilder();

        b.AppendLine($"Goal: {plan.Goal}");
        b.AppendLine($"Recommended execution shape: {plan.RecommendedWorkflowKind}");
        b.AppendLine("Subtasks:");
        foreach (var s in plan.Subtasks) b.AppendLine($"  - {s.Title}: {s.Instruction}");

        if (plan.SuccessCriteria.Count > 0)
        {
            b.AppendLine("Success criteria:");
            foreach (var c in plan.SuccessCriteria) b.AppendLine($"  - {c}");
        }

        if (plan.Risks.Count > 0)
        {
            b.AppendLine("Stated risks:");
            foreach (var r in plan.Risks) b.AppendLine($"  - {r}");
        }

        return b.ToString();
    }
}

/// <summary>The planner-critic kill-switch — a global operator off (default ON, like the planner flag); the per-request <see cref="ReviewMode.None"/> is the real default-off. Set <c>CODESPACE_PLANNER_CRITIC_ENABLED=0</c> to disable the critic entirely.</summary>
public static class CriticToggle
{
    public const string EnabledEnvVar = "CODESPACE_PLANNER_CRITIC_ENABLED";

    public static bool Enabled => IsEnabled(Environment.GetEnvironmentVariable(EnabledEnvVar));

    /// <summary>Default ON; disabled only for an explicit "0" / "false" (parity with the planner's enable flag). Internal for direct unit testing.</summary>
    internal static bool IsEnabled(string? raw)
    {
        var value = raw?.Trim();

        return !string.Equals(value, "0", StringComparison.Ordinal) && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }
}
