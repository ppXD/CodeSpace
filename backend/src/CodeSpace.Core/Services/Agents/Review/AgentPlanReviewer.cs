using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Review;

namespace CodeSpace.Core.Services.Agents.Review;

/// <summary>
/// The real-agent <see cref="IAgentPlanReviewer"/> (D①) — a thin facade over the shared <see cref="AgentReviewRunner"/>:
/// clones the plan's target repository at its BASE state (the tree the plan's first agent would see) and instructs an
/// independent agent to VERIFY the plan against the actual code, returning the shared <c>VERDICT:</c> contract. The
/// plan's producer is a MODEL, so the harness ladder simply takes the first registered harness — the independence is
/// agent-vs-planner, not harness-vs-harness. The reviewer run lands on the plan node's cell under the
/// <c>#plan-review</c> iteration key.
/// </summary>
public sealed class AgentPlanReviewer : IAgentPlanReviewer, IScopedDependency
{
    /// <summary>The plan-review run's iteration key — a fixed, checklist-invisible key on the plan node's cell (one grounded review per plan version in flight; a re-plan's review re-uses the key, which is fine — keys are observability grouping, not identity).</summary>
    internal const string IterationKey = "#plan-review";

    private readonly AgentReviewRunner _runner;

    public AgentPlanReviewer(AgentReviewRunner runner) { _runner = runner; }

    public async Task<CriticVerdict> ReviewAsync(PlanReviewRequest request, CancellationToken cancellationToken) =>
        await _runner.RunAsync(new AgentReviewSpec
        {
            SubjectInstructions = BuildReviewInstructions(request.PlanArtifact, request.Goal),
            RepositoryId = request.RepositoryId,
            BaseRef = null,   // the plan targets the repo's CURRENT state — clone the default branch
            TeamId = request.TeamId,
            WorkflowRunId = request.WorkflowRunId,
            NodeId = request.NodeId,
            IterationKey = IterationKey,
            ProducerHarness = "",   // the plan's producer is a model — any registered harness is independent
            ReviewerModelId = request.ReviewerModelId,
        }, cancellationToken).ConfigureAwait(false);

    /// <summary>The grounded plan-review body: verify the plan against the REAL tree — assumptions, feasibility, completeness, already-done work. The capability-context clause is load-bearing: the reviewer's OWN clone is deliberately read-only, and a real run was once derailed by a reviewer that probed <c>touch</c>/<c>dotnet build</c>, hit its own sandbox wall, and concluded the plan itself was unachievable — the executing agents' environment must never be inferred from the reviewer's. Internal for direct unit pinning.</summary>
    internal static string BuildReviewInstructions(string planArtifact, string goal)
    {
        return
            "You are an INDEPENDENT reviewer. This workspace is the repository the plan below targets, checked out at its base state. " +
            "VERIFY the plan against the ACTUAL code — read the files it presumes, run greps: (1) do its assumptions hold (files, frameworks, test infrastructure it names or implies)? " +
            "(2) is each step feasible and necessary in THIS codebase? (3) is anything the plan schedules already done? (4) does anything essential for the goal go unplanned? " +
            "You did not write the plan; judge it strictly against what you can see in the tree. Do NOT modify anything.\n\n" +
            "IMPORTANT — your sandbox is NOT the executing environment: your clone is deliberately READ-ONLY and command-restricted (write probes and builds WILL fail here by design). " +
            "The agents that will execute this plan run with WRITABLE workspaces and can run builds and tests. " +
            "Never judge the plan's feasibility from your own write/exec failures — judge it from the CODE.\n\n" +
            $"Goal the plan should serve:\n{goal}\n\n" +
            $"The plan under review:\n{planArtifact}";
    }
}
