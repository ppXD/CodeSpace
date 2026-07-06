using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;

namespace CodeSpace.Core.Services.Agents.Review;

/// <summary>
/// The real-agent OUTPUT reviewer (triad S8, now a thin facade over the shared <see cref="AgentReviewRunner"/>):
/// a read-only review agent — distinct-first harness — cloned at the PRODUCED BRANCH, so it inspects the actual
/// repository state the change created, not a diff string. Its verdict feeds the executor's ladder
/// (agent → model critic → fail-open); its run lands on the producer's node cell under an iteration key the
/// plan-map checklist's positional join deliberately cannot parse as a branch index.
/// </summary>
public sealed class AgentOutputReviewer : IAgentOutputReviewer, IScopedDependency
{
    /// <summary>The output-review run's iteration-key suffix — appended to the producer's key.</summary>
    internal const string IterationKeySuffix = "#review";

    private readonly AgentReviewRunner _runner;

    public AgentOutputReviewer(AgentReviewRunner runner) { _runner = runner; }

    public async Task<CriticVerdict> ReviewAsync(AgentTask producerTask, AgentRunResult result, AgentRun run, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(result.ProducedBranch) || producerTask.RepositoryId is not { } repositoryId)
            return CriticVerdict.ReviewFailed(ReviewMode.Gate, "agent-reviewer: no produced branch to clone — nothing for an agent to inspect");

        return await _runner.RunAsync(new AgentReviewSpec
        {
            SubjectInstructions = BuildReviewInstructions(producerTask.Goal, result),
            RepositoryId = repositoryId,
            BaseRef = result.ProducedBranch,
            TeamId = run.TeamId,
            WorkflowRunId = run.WorkflowRunId,
            NodeId = run.NodeId,
            IterationKey = ReviewIterationKey(run.IterationKey),
            ProducerHarness = producerTask.Harness,
            ReviewerModelId = producerTask.ReviewerModelId,
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The review run's iteration key — the producer's key + the review suffix (a bare producer key means a bare "#review").</summary>
    internal static string ReviewIterationKey(string? producerIterationKey) =>
        string.IsNullOrEmpty(producerIterationKey) ? IterationKeySuffix : producerIterationKey + IterationKeySuffix;

    /// <summary>The change-review body: the producer's goal (the yardstick) + the change summary + the inspect-don't-modify framing. Internal for direct unit pinning.</summary>
    internal static string BuildReviewInstructions(string producerGoal, AgentRunResult result)
    {
        var files = result.ChangedFiles.Count > 0 ? string.Join(", ", result.ChangedFiles.Take(30)) : "(none listed)";

        return
            "You are an INDEPENDENT reviewer. This workspace is checked out at the branch another agent produced — inspect the ACTUAL repository state (read the changed files, their neighbours, run greps) and judge whether the change soundly achieves the goal below. You did not write it; judge it strictly on its merits. Do NOT modify anything.\n\n" +
            "IMPORTANT — your sandbox is NOT the producing environment: your clone is deliberately READ-ONLY and command-restricted (write probes and builds WILL fail here by design). Never infer anything about the producer's capabilities, or the change's viability, from your own write/exec failures — judge the CODE.\n\n" +
            $"Goal the change should serve:\n{producerGoal}\n\n" +
            $"Changed files: {files}";
    }
}
