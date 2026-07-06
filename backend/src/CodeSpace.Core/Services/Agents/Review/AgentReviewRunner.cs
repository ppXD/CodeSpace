using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Review;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Review;

/// <summary>
/// The SHARED core every real-agent reviewer stands on (D① — one home, two facades: the output reviewer and the plan
/// reviewer): stage a READ-ONLY first-class <c>AgentRun</c> on a DISTINCT-first harness, cloned at the requested ref,
/// with the caller's review instructions + the pinned <c>VERDICT:</c> final-message contract; execute it through the
/// production executor (its own claim, heartbeat, spool, billing); parse the verdict FAIL-CLOSED. RECURSION-PROOF by
/// construction — the review task pins <c>OutputReviewMode=None</c>, <c>ReviewerAgent=false</c>, <c>MaxReviseRounds=0</c>,
/// no acceptance, no push. NEVER throws (cancellation aside): every failure returns
/// <see cref="CriticVerdict.ReviewFailed"/> so the caller can ladder down to the in-process model critic.
/// </summary>
public sealed class AgentReviewRunner : IScopedDependency
{
    /// <summary>The final-message contract marker — everything after it must parse as the verdict JSON. Pinned (the review goals quote it; the fakes and real harness prompts program against it).</summary>
    public const string VerdictMarker = "VERDICT:";

    /// <summary>The reviewer's wall-clock cap — a review is a bounded read, never a second engineering project.</summary>
    internal const int ReviewerTimeoutSeconds = 900;

    private readonly IAgentRunService _runs;
    private readonly IAgentHarnessRegistry _harnesses;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgentReviewRunner> _logger;

    public AgentReviewRunner(IAgentRunService runs, IAgentHarnessRegistry harnesses, IServiceScopeFactory scopeFactory, ILogger<AgentReviewRunner> logger)
    {
        _runs = runs;
        _harnesses = harnesses;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Run one independent review agent per <paramref name="spec"/> and return its verdict — fail-closed to a failed review on any staging / execution / contract breakage.</summary>
    public async Task<CriticVerdict> RunAsync(AgentReviewSpec spec, CancellationToken cancellationToken)
    {
        try
        {
            var task = BuildReviewTask(spec, PickReviewerHarness(spec.ProducerHarness, _harnesses.All));

            var reviewRun = await _runs.CreateAsync(task, spec.TeamId, spec.WorkflowRunId, spec.NodeId, spec.IterationKey, cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(reviewRun.Id, cancellationToken).ConfigureAwait(false);

            var finished = await _runs.GetAsync(reviewRun.Id, cancellationToken).ConfigureAwait(false);

            if (finished.Status != AgentRunStatus.Succeeded)
                return CriticVerdict.ReviewFailed(ReviewMode.Gate, $"agent-reviewer: the review run finished {finished.Status} — no verdict");

            var summary = finished.ResultJson is null ? null : JsonSerializer.Deserialize<AgentRunResult>(finished.ResultJson, AgentJson.Options)?.Summary;

            return ParseVerdict(summary);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The agent reviewer ({Key}) failed; laddering down to the model critic", spec.IterationKey);
            return CriticVerdict.ReviewFailed(ReviewMode.Gate, $"agent-reviewer: {ex.Message}");
        }
    }

    /// <summary>The reviewer runs the production EXECUTOR path in a fresh scope (its own claim, heartbeat, spool, billing) — a first-class run, synchronously awaited.</summary>
    private async Task ExecuteAsync(Guid reviewRunId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAgentRunExecutor>().ExecuteAsync(reviewRunId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The distinct-first harness ladder (the owner's Q5): prefer a registered harness DIFFERENT from the producer's
    /// — a genuinely independent second toolchain — falling back to the producer's own on a one-harness deployment
    /// (an independent AGENT + model is still a real second opinion). A blank producer (a MODEL-produced artifact,
    /// e.g. a plan) takes the first registered harness. Registry order keeps the pick deterministic. Internal +
    /// static so the ladder is unit-pinned.
    /// </summary>
    internal static string PickReviewerHarness(string producerHarness, IReadOnlyList<IAgentHarness> registered) =>
        registered.FirstOrDefault(h => !string.Equals(h.Kind, producerHarness, StringComparison.OrdinalIgnoreCase))?.Kind ?? producerHarness;

    /// <summary>
    /// The reviewer's task: a READ-ONLY (Confined) clone of the spec's repository@ref with the review instructions +
    /// the verdict contract as the goal. The reviewer model rides the spec's pinned pool row when set; the producer's
    /// model is deliberately NOT inherited (independence). Recursion-proof pins per the class doc. Internal + static
    /// so the pins are unit-pinned directly.
    /// </summary>
    internal static AgentTask BuildReviewTask(AgentReviewSpec spec, string reviewerHarness) => new()
    {
        Goal = $"{spec.SubjectInstructions}\n\n{VerdictContract}",
        Harness = reviewerHarness,
        ModelCredentialModelId = spec.ReviewerModelId,
        RepositoryId = spec.RepositoryId,
        Workspace = AgentWorkspaceAuthoring.ResolveAuthoredWorkspace(spec.RepositoryId, Array.Empty<WorkspaceRepositorySpec>(), primaryRef: spec.BaseRef),
        Autonomy = AgentAutonomyLevel.Confined,
        Permissions = AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Confined),
        TimeoutSeconds = ReviewerTimeoutSeconds,
        PushProducedBranch = null,
        OutputReviewMode = ReviewMode.None,
        ReviewerAgent = false,
        MaxReviseRounds = 0,
        Acceptance = null,
    };

    /// <summary>The shared final-message contract footer every review goal ends with — evidence REQUIRED per issue.</summary>
    internal const string VerdictContract =
        "Your FINAL message must be exactly one line starting with the marker, no prose after it:\n" +
        VerdictMarker + " {\"approved\": true|false, \"rationale\": \"why\", \"issues\": [{\"issue\": \"one concrete problem\", \"evidence\": \"quote or precise location in the repository\"}]}\n\n" +
        "Approve ONLY when the artifact soundly achieves its goal with no material flaw; otherwise approved=false with every issue grounded in evidence you actually saw in this workspace.";

    /// <summary>
    /// Parse the reviewer's final message into a verdict, FAIL-CLOSED to a failed review (→ the model-critic ladder)
    /// on a missing marker or unparseable JSON — a review that can't state its verdict in-contract is not a verdict.
    /// The LAST marker wins, so contract text quoted in prose never shadows the real verdict. Internal + static so
    /// the contract is unit-pinned.
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

/// <summary>One agent-review request — everything the runner needs to stage, link, and judge an independent review run.</summary>
public sealed record AgentReviewSpec
{
    /// <summary>The review body — WHAT to inspect and judge (the runner appends the shared verdict contract).</summary>
    public required string SubjectInstructions { get; init; }

    /// <summary>The repository the reviewer clones (read-only).</summary>
    public required Guid RepositoryId { get; init; }

    /// <summary>The ref to clone at — a produced branch for an output review; null (the default branch) for a plan review.</summary>
    public string? BaseRef { get; init; }

    public required Guid TeamId { get; init; }

    /// <summary>Observability linkage: the run/node cell the reviewer AgentRun lands on. Null on run-less paths.</summary>
    public Guid? WorkflowRunId { get; init; }

    public string? NodeId { get; init; }

    /// <summary>The reviewer run's iteration key — suffixed (<c>#review</c> / <c>#plan-review</c>) so the checklist's positional join can never adopt it.</summary>
    public required string IterationKey { get; init; }

    /// <summary>The producer's harness the distinct-first ladder avoids; "" for a MODEL-produced artifact (any harness qualifies).</summary>
    public string ProducerHarness { get; init; } = "";

    /// <summary>The operator's reviewer model pin (a credentialed-model ROW id); null ⇒ the harness default resolve.</summary>
    public Guid? ReviewerModelId { get; init; }
}
