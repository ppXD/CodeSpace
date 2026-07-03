using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Review;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Review;

/// <summary>
/// The real-agent <see cref="IAgentOutputReviewer"/> (triad S8): stages a READ-ONLY review agent — preferring a
/// DIFFERENT harness from the producer's (the distinct-first ladder the owner asked for; same-harness fallback on a
/// one-harness deployment) — cloned at the PRODUCED BRANCH, so the reviewer inspects the actual repository state the
/// change created, not a diff string. The reviewer's contract is its FINAL message: a <c>VERDICT:</c>-prefixed JSON
/// (approved / rationale / evidence-attached issues) parsed fail-closed; the run is a first-class <c>AgentRun</c>
/// (billed, observable, on the producer's node in the Room) with an iteration key the checklist's positional join
/// deliberately cannot parse as a branch index.
///
/// <para>RECURSION-PROOF by construction: the review task pins <c>OutputReviewMode=None</c>, <c>ReviewerAgent=false</c>,
/// <c>MaxReviseRounds=0</c>, no acceptance, no push — a reviewer never gets reviewed, revised, or published. NEVER
/// throws (cancellation aside): any failure — staging, execution, a missing/unparseable verdict — returns
/// <see cref="CriticVerdict.ReviewFailed"/>, and the executor ladders down to the in-process model critic.</para>
/// </summary>
public sealed class AgentOutputReviewer : IAgentOutputReviewer, IScopedDependency
{
    /// <summary>The final-message contract marker — everything after it must parse as the verdict JSON. Pinned (the review goal quotes it; the fakes and real harness prompts program against it).</summary>
    public const string VerdictMarker = "VERDICT:";

    /// <summary>The reviewer's wall-clock cap — a review is a bounded read, never a second engineering project.</summary>
    internal const int ReviewerTimeoutSeconds = 900;

    /// <summary>The review run's iteration-key suffix — appended to the producer's key so the run lands on the same node cell, while the plan-map checklist's <c>map#i</c> positional join (int-parse guarded) can never mistake it for a branch.</summary>
    internal const string IterationKeySuffix = "#review";

    private readonly IAgentRunService _runs;
    private readonly IAgentHarnessRegistry _harnesses;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgentOutputReviewer> _logger;

    public AgentOutputReviewer(IAgentRunService runs, IAgentHarnessRegistry harnesses, IServiceScopeFactory scopeFactory, ILogger<AgentOutputReviewer> logger)
    {
        _runs = runs;
        _harnesses = harnesses;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<CriticVerdict> ReviewAsync(AgentTask producerTask, AgentRunResult result, AgentRun run, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrEmpty(result.ProducedBranch) || producerTask.RepositoryId is null)
                return CriticVerdict.ReviewFailed(ReviewMode.Gate, "agent-reviewer: no produced branch to clone — nothing for an agent to inspect");

            var reviewTask = BuildReviewTask(producerTask, result, PickReviewerHarness(producerTask.Harness, _harnesses.All));

            var reviewRun = await _runs.CreateAsync(reviewTask, run.TeamId, run.WorkflowRunId, run.NodeId, ReviewIterationKey(run.IterationKey), cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(reviewRun.Id, cancellationToken).ConfigureAwait(false);

            var finished = await _runs.GetAsync(reviewRun.Id, cancellationToken).ConfigureAwait(false);

            if (finished.Status != AgentRunStatus.Succeeded)
                return CriticVerdict.ReviewFailed(ReviewMode.Gate, $"agent-reviewer: the review run finished {finished.Status} — no verdict");

            var summary = finished.ResultJson is null ? null : JsonSerializer.Deserialize<AgentRunResult>(finished.ResultJson, AgentJson.Options)?.Summary;

            return ParseVerdict(summary);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Agent run {RunId}: the agent reviewer failed; laddering down to the model critic", run.Id);
            return CriticVerdict.ReviewFailed(ReviewMode.Gate, $"agent-reviewer: {ex.Message}");
        }
    }

    /// <summary>The reviewer runs the producing run's EXECUTOR path in a fresh scope (its own claim, heartbeat, spool, billing) — a first-class run, synchronously awaited.</summary>
    private async Task ExecuteAsync(Guid reviewRunId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAgentRunExecutor>().ExecuteAsync(reviewRunId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The distinct-first harness ladder (the owner's Q5): prefer a registered harness DIFFERENT from the producer's
    /// — a genuinely independent second toolchain — falling back to the producer's own on a one-harness deployment
    /// (an independent AGENT + model is still a real second opinion). Registry order keeps the pick deterministic.
    /// Internal + static so the ladder is unit-pinned.
    /// </summary>
    internal static string PickReviewerHarness(string producerHarness, IReadOnlyList<IAgentHarness> registered) =>
        registered.FirstOrDefault(h => !string.Equals(h.Kind, producerHarness, StringComparison.OrdinalIgnoreCase))?.Kind ?? producerHarness;

    /// <summary>The review run's iteration key — the producer's key + the review suffix (a bare producer key means a bare "#review").</summary>
    internal static string ReviewIterationKey(string? producerIterationKey) =>
        string.IsNullOrEmpty(producerIterationKey) ? IterationKeySuffix : producerIterationKey + IterationKeySuffix;

    /// <summary>
    /// The reviewer's task: a READ-ONLY (Confined) clone of the PRODUCED branch with the review contract as the goal.
    /// The reviewer model rides <c>ReviewerModelId</c> as the task's pinned pool row when the operator set one; the
    /// producer's model is deliberately NOT inherited (independence). Recursion-proof pins per the class doc.
    /// </summary>
    internal static AgentTask BuildReviewTask(AgentTask producerTask, AgentRunResult result, string reviewerHarness) => new()
    {
        Goal = BuildReviewGoal(producerTask.Goal, result),
        Harness = reviewerHarness,
        ModelCredentialModelId = producerTask.ReviewerModelId,
        RepositoryId = producerTask.RepositoryId,
        Workspace = AgentWorkspaceAuthoring.ResolveAuthoredWorkspace(producerTask.RepositoryId, Array.Empty<WorkspaceRepositorySpec>(), primaryRef: result.ProducedBranch),
        Autonomy = AgentAutonomyLevel.Confined,
        Permissions = AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Confined),
        TimeoutSeconds = ReviewerTimeoutSeconds,
        PushProducedBranch = null,
        OutputReviewMode = ReviewMode.None,
        ReviewerAgent = false,
        MaxReviseRounds = 0,
        Acceptance = null,
    };

    /// <summary>The review contract the agent works to: the producer's goal (the yardstick), the change summary, and the exact final-message verdict format — evidence REQUIRED per issue.</summary>
    internal static string BuildReviewGoal(string producerGoal, AgentRunResult result)
    {
        var files = result.ChangedFiles.Count > 0 ? string.Join(", ", result.ChangedFiles.Take(30)) : "(none listed)";

        return
            "You are an INDEPENDENT reviewer. This workspace is checked out at the branch another agent produced — inspect the ACTUAL repository state (read the changed files, their neighbours, run greps) and judge whether the change soundly achieves the goal below. You did not write it; judge it strictly on its merits. Do NOT modify anything.\n\n" +
            $"Goal the change should serve:\n{producerGoal}\n\n" +
            $"Changed files: {files}\n\n" +
            "Your FINAL message must be exactly one line starting with the marker, no prose after it:\n" +
            VerdictMarker + " {\"approved\": true|false, \"rationale\": \"why\", \"issues\": [{\"issue\": \"one concrete problem\", \"evidence\": \"quote or precise location in the repository\"}]}\n\n" +
            "Approve ONLY when the change soundly achieves the goal with no material flaw; otherwise approved=false with every issue grounded in evidence you actually saw in this workspace.";
    }

    /// <summary>
    /// Parse the reviewer's final message into a verdict, FAIL-CLOSED to a failed review (→ the model-critic ladder)
    /// on a missing marker or unparseable JSON — a review that can't state its verdict in-contract is not a verdict.
    /// Internal + static so the contract is unit-pinned.
    /// </summary>
    internal static CriticVerdict ParseVerdict(string? finalMessage)
    {
        if (string.IsNullOrWhiteSpace(finalMessage)) return CriticVerdict.ReviewFailed(ReviewMode.Gate, "agent-reviewer: the review run produced no final message");

        var index = finalMessage.LastIndexOf(VerdictMarker, StringComparison.Ordinal);

        if (index < 0) return CriticVerdict.ReviewFailed(ReviewMode.Gate, "agent-reviewer: the final message carries no VERDICT marker");

        try
        {
            var model = JsonSerializer.Deserialize<GateModelReview>(finalMessage[(index + VerdictMarker.Length)..].Trim(), CriticSchema.Options);

            if (model is null || string.IsNullOrWhiteSpace(model.Rationale))
                return CriticVerdict.ReviewFailed(ReviewMode.Gate, "agent-reviewer: the verdict JSON carries no rationale");

            return new CriticVerdict
            {
                Mode = ReviewMode.Gate,
                Approved = model.Approved,
                Score = model.Score,
                Issues = ModelIssueProjection.Project(model.Issues),
                Rationale = model.Rationale,
            };
        }
        catch (JsonException ex)
        {
            return CriticVerdict.ReviewFailed(ReviewMode.Gate, $"agent-reviewer: unparseable verdict JSON — {ex.Message}");
        }
    }
}
