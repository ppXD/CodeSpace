using System.Text;
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

    public CriticPlannerDecorator(IWorkflowPlanner inner, IStructuredCritic critic)
    {
        _inner = inner;
        _critic = critic;
    }

    public async Task<PlannedWorkflow> PlanAsync(WorkflowPlanRequest request, CancellationToken cancellationToken)
    {
        var plan = await _inner.PlanAsync(request, cancellationToken).ConfigureAwait(false);

        // Doubly-off ⇒ byte-identical: no per-request review requested, OR the operator killed the critic globally.
        if (request.Review == ReviewMode.None || !CriticToggle.Enabled) return plan;

        var verdict = await _critic.ReviewAsync(
            new CriticRequest { Mode = request.Review, ArtifactKind = "workflow plan", Artifact = Render(plan), Goal = request.TaskText },
            request.TeamId, request.ReviewerModelId, cancellationToken).ConfigureAwait(false);

        if (verdict.Failed) return plan;   // fail-open — a failed review is never worse than no review

        return request.Review switch
        {
            ReviewMode.Improve => await ImproveAsync(request, plan, verdict, cancellationToken).ConfigureAwait(false),
            ReviewMode.Gate => Annotate(plan, verdict),
            _ => plan,
        };
    }

    /// <summary>IMPROVE: re-plan ONCE through the BARE inner planner (<see cref="ReviewMode.None"/> ⇒ no recursion) with the critique folded into the request, so the producer revises against it. A blank critique falls back to the original.</summary>
    private async Task<PlannedWorkflow> ImproveAsync(WorkflowPlanRequest request, PlannedWorkflow plan, CriticVerdict verdict, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(verdict.Critique)) return plan;

        return await _inner.PlanAsync(request with { Review = ReviewMode.None, ReviewerCritique = verdict.Critique }, cancellationToken).ConfigureAwait(false);
    }

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
