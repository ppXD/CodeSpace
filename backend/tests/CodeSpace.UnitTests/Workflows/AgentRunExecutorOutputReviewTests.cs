using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Review;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the v1 GATE behaviour of <c>AgentRunExecutor.ReviewOutputIfEnabledAsync</c> — the 3rd critic application. With
/// <see cref="ReviewMode.None"/> (the default) it is a pure passthrough that never calls the critic (byte-identical), and
/// it self-skips a non-success or a no-diff run (a no-op / re-attach). When enabled, a DISAPPROVED change re-grades a
/// would-be Succeeded run to <see cref="AgentRunStatus.NeedsReview"/> with a timeline warning; an approved verdict OR a
/// failed review passes through unchanged (fail-open). Driven with a recording fake critic + the minimal run-service stub.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AgentRunExecutorOutputReviewTests
{
    [Fact]
    public async Task None_never_calls_the_critic_and_passes_through_byte_identical()
    {
        var (runId, executor, runs, critic) = NewExecutor(new CriticVerdict { Mode = ReviewMode.Gate, Approved = false });

        var input = SucceededWithChanges();   // OutputReviewMode defaults to None
        var result = await executor.ReviewOutputIfEnabledAsync(DefaultTask, input, Run(runId), CancellationToken.None);

        critic.Called.ShouldBeFalse("None never reviews — byte-identical to no review");
        result.ShouldBeSameAs(input, "the result is passed through reference-unchanged");
        runs.AppendedEvents.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_non_succeeded_run_skips_the_review()
    {
        var (runId, executor, _, critic) = NewExecutor(new CriticVerdict { Mode = ReviewMode.Gate, Approved = false });

        var failed = SucceededWithChanges() with { Status = AgentRunStatus.Failed };
        var result = await executor.ReviewOutputIfEnabledAsync(GatedTask, failed, Run(runId), CancellationToken.None);

        critic.Called.ShouldBeFalse("a non-success has no produced change to gate");
        result.Status.ShouldBe(AgentRunStatus.Failed);
    }

    [Fact]
    public async Task An_empty_diff_skips_the_review()
    {
        var (runId, executor, _, critic) = NewExecutor(new CriticVerdict { Mode = ReviewMode.Gate, Approved = false });

        var noChanges = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" };   // no ChangedFiles, no Patch
        var result = await executor.ReviewOutputIfEnabledAsync(GatedTask, noChanges, Run(runId), CancellationToken.None);

        critic.Called.ShouldBeFalse("a no-op / re-attach run with no captured diff has nothing to gate");
        result.Status.ShouldBe(AgentRunStatus.Succeeded);
    }

    [Fact]
    public async Task An_approved_change_passes_through_unchanged()
    {
        var (runId, executor, runs, critic) = NewExecutor(new CriticVerdict { Mode = ReviewMode.Gate, Approved = true, Rationale = "looks good" });

        var input = SucceededWithChanges();
        var result = await executor.ReviewOutputIfEnabledAsync(GatedTask, input, Run(runId), CancellationToken.None);

        critic.Called.ShouldBeTrue("a gated run with a diff IS reviewed");
        result.Status.ShouldBe(AgentRunStatus.Succeeded, "an approved change stays a clean success");
        runs.AppendedEvents.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_review_falls_open_to_the_original_success()
    {
        var (runId, executor, runs, _) = NewExecutor(CriticVerdict.ReviewFailed(ReviewMode.Gate, "no reviewer model"));

        var result = await executor.ReviewOutputIfEnabledAsync(GatedTask, SucceededWithChanges(), Run(runId), CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Succeeded, "a failed review is never worse than no review — fail-open");
        runs.AppendedEvents.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_disapproved_change_is_re_graded_to_needs_review_with_a_warning()
    {
        var (runId, executor, runs, _) = NewExecutor(new CriticVerdict { Mode = ReviewMode.Gate, Approved = false, Issues = new[] { "no tests for the new path" }, Rationale = "incomplete" });

        var result = await executor.ReviewOutputIfEnabledAsync(GatedTask, SucceededWithChanges(), Run(runId), CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.NeedsReview, "a disapproved change blocks the clean-success path so a human looks");
        result.CompletionDisposition.ShouldBe(CompletionDisposition.NeedsReview);
        result.ExitReason.ShouldBe("output-flagged");
        result.ReviewFeedback.ShouldBe("incomplete Issues: no tests for the new path", "the critique persists on the result — WHY it was flagged, and the S6 Improve loop's food");

        runs.AppendedEvents.Count.ShouldBe(1, "the operator gets a timeline warning explaining why it's flagged");
        runs.AppendedEvents[0].Kind.ShouldBe(AgentEventKind.Warning);
        runs.AppendedEvents[0].Text.ShouldContain("no tests for the new path", customMessage: "the reviewer's issues are surfaced");
    }

    private static AgentRunResult SucceededWithChanges() =>
        new() { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = "did the thing", ChangedFiles = new[] { "src/foo.cs" }, Patch = "diff --git ..." };

    [Fact]
    public async Task A_pending_decision_defers_to_the_A1_gate_and_skips_the_output_review()
    {
        // A run that BOTH produced a flagged diff AND left a decision.request unanswered must defer to the A1 completion
        // gate (NeedsReview/NeedsDecision with the decision linkage), not pre-empt it with output-flagged. So when a
        // blocking decision is pending, the output review is skipped entirely — the critic is never even called.
        var (runId, executor, runs, critic) = NewExecutor(new CriticVerdict { Mode = ReviewMode.Gate, Approved = false }, pendingDecision: Guid.NewGuid());

        var result = await executor.ReviewOutputIfEnabledAsync(GatedTask, SucceededWithChanges(), Run(runId), CancellationToken.None);

        critic.Called.ShouldBeFalse("a pending decision defers to A1 — the output review never runs");
        result.Status.ShouldBe(AgentRunStatus.Succeeded, "the output review leaves the status for A1 to re-grade at completion");
        runs.AppendedEvents.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_workflow_run_pushes_an_agent_critic_scope_keyed_to_the_cell_so_the_call_records()
    {
        // The critic is the executor's one IN-PROCESS model call, and it runs OUTSIDE the engine's per-node scope. This
        // pins the fix: when the agent run belongs to a workflow run, the critic call is made UNDER an LlmCallScope keyed
        // to the run's (WorkflowRunId, NodeId, IterationKey) cell with kind "agent.critic" — so the recording decorator
        // lands its interaction.* on the SAME workflow_run_record ledger as the rest of the run.
        var (runId, executor, _, critic) = NewExecutor(new CriticVerdict { Mode = ReviewMode.Gate, Approved = true });

        var workflowRunId = Guid.NewGuid();
        var run = Run(runId, workflowRunId: workflowRunId, nodeId: "agent-node", iterationKey: "agent-node#2");

        await executor.ReviewOutputIfEnabledAsync(GatedTask, SucceededWithChanges(), run, CancellationToken.None);

        critic.Called.ShouldBeTrue();
        critic.ObservedScope.ShouldNotBeNull("the critic call is made under a pushed recording scope");
        critic.ObservedScope!.RunId.ShouldBe(workflowRunId, "the interaction is bound to the OWNING workflow run, not the agent run id");
        critic.ObservedScope.NodeId.ShouldBe("agent-node");
        critic.ObservedScope.IterationKey.ShouldBe("agent-node#2", "the full cell key rides so a map-branch agent's critic is distinguishable");
        critic.ObservedScope.Kind.ShouldBe("agent.critic", "the step is attributed as the agent's critic, not the node's own call");
    }

    [Fact]
    public async Task A_standalone_run_pushes_no_scope_so_the_critic_runs_byte_identically()
    {
        // A standalone agent run (no WorkflowRunId) has no workflow_run_record ledger to write to, so NO scope is pushed
        // — the critic still runs (records nothing), fail-open and byte-identical to the pre-recording behaviour.
        var (runId, executor, _, critic) = NewExecutor(new CriticVerdict { Mode = ReviewMode.Gate, Approved = true });

        await executor.ReviewOutputIfEnabledAsync(GatedTask, SucceededWithChanges(), Run(runId), CancellationToken.None);   // WorkflowRunId == null

        critic.Called.ShouldBeTrue("the critic still runs for a standalone run");
        critic.ObservedScope.ShouldBeNull("no workflow run ⇒ no scope pushed ⇒ the call records nothing (fail-open)");
    }

    private static AgentRun Run(Guid id, Guid? workflowRunId = null, string? nodeId = null, string iterationKey = "") =>
        new() { Id = id, TeamId = Guid.NewGuid(), WorkflowRunId = workflowRunId, NodeId = nodeId, IterationKey = iterationKey };

    private static AgentTask DefaultTask => new() { Goal = "g", Harness = "codex-cli" };   // OutputReviewMode = None
    private static AgentTask GatedTask => new() { Goal = "g", Harness = "codex-cli", OutputReviewMode = ReviewMode.Gate };

    private static (Guid RunId, AgentRunExecutor Executor, StubRuns Runs, RecordingCritic Critic) NewExecutor(CriticVerdict verdict, Guid? pendingDecision = null)
    {
        var runId = Guid.NewGuid();
        var runs = new StubRuns(runId);
        var critic = new RecordingCritic { Verdict = verdict };
        var scopeFactory = new FakeScopeFactory(new FakeLedger(pendingDecision));
        var executor = new AgentRunExecutor(runs, null!, null!, null!, null!, null!, null!, null!, scopeFactory, null!, critic, null!, NullLogger<AgentRunExecutor>.Instance);
        return (runId, executor, runs, critic);
    }

    /// <summary>A minimal IServiceScopeFactory whose fresh scope resolves the ledger (the A1-defer guard) plus the record logger + offloader the recording scope pulls from a fresh scope.</summary>
    private sealed class FakeScopeFactory : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        private readonly IToolCallLedgerService _ledger;
        public FakeScopeFactory(IToolCallLedgerService ledger) { _ledger = ledger; }

        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IToolCallLedgerService) ? _ledger
            : serviceType == typeof(IRunRecordLogger) ? new NoopRecordLogger()
            : serviceType == typeof(IArtifactOffloader) ? new NoopOffloader()
            : null;
        public void Dispose() { }
    }

    /// <summary>The offloader carried on the pushed scope. Never exercised here (the fake critic short-circuits before any decorator) — it only needs to be resolvable and non-null so the scope can be constructed.</summary>
    private sealed class NoopOffloader : IArtifactOffloader
    {
        public Task<OffloadedText> OffloadIfLargeAsync(Guid teamId, string? text, string contentType, CancellationToken ct) => Task.FromResult(new OffloadedText(text ?? "", null));
        public Task<string> ResolveAsync(Guid teamId, string? inline, Guid? artifactId, CancellationToken ct) => Task.FromResult(inline ?? "");
    }

    /// <summary>The ledger writer carried on the pushed scope. Never exercised here (the fake critic never reaches the recording decorator) — every member is an inert no-op; it only needs to be resolvable and non-null.</summary>
    private sealed class NoopRecordLogger : IRunRecordLogger
    {
        public Task<Guid> RecordInteractionAsync(Guid runId, string recordType, string? nodeId, string iterationKey, Guid correlationId, Guid? parentRecordId, JsonElement payload, CancellationToken ct) => Task.FromResult(Guid.NewGuid());
        public Task RunQueuedAsync(Guid runId, string sourceType, Guid? actorId, CancellationToken ct) => Task.CompletedTask;
        public Task RunStartedAsync(Guid runId, CancellationToken ct) => Task.CompletedTask;
        public Task ReleaseLoadedAsync(Guid runId, int version, string definitionHash, int nodeCount, int edgeCount, CancellationToken ct) => Task.CompletedTask;
        public Task ScopeResolvedAsync(Guid runId, int wfCount, int teamCount, int sysCount, int secretPathCount, CancellationToken ct) => Task.CompletedTask;
        public Task VariablesSnapshottedAsync(Guid runId, int wfCount, int teamCount, string releaseHash, CancellationToken ct) => Task.CompletedTask;
        public Task RunCompletedAsync(Guid runId, TimeSpan duration, bool outputsPresent, CancellationToken ct) => Task.CompletedTask;
        public Task RunFailedAsync(Guid runId, string error, TimeSpan duration, CancellationToken ct) => Task.CompletedTask;
        public Task RunCancelledAsync(Guid runId, TimeSpan duration, CancellationToken ct) => Task.CompletedTask;
        public Task RunReplayedAsync(Guid runId, Guid? parentRunId, int snapshotCount, CancellationToken ct) => Task.CompletedTask;
        public Task SupervisorRunRecoveredAsync(Guid runId, int attempt, CancellationToken ct) => Task.CompletedTask;
        public Task<Guid> NodeStartedAsync(Guid runId, string nodeId, string iterationKey, IReadOnlyDictionary<string, JsonElement> resolvedInputs, IReadOnlyDictionary<string, JsonElement> resolvedConfig, CancellationToken ct) => Task.FromResult(Guid.NewGuid());
        public Task NodeCompletedAsync(Guid runId, string nodeId, string iterationKey, IReadOnlyDictionary<string, JsonElement> outputs, IReadOnlyList<string>? routingHints, TimeSpan duration, CancellationToken ct) => Task.CompletedTask;
        public Task NodeFailedAsync(Guid runId, string nodeId, string iterationKey, string error, TimeSpan duration, CancellationToken ct) => Task.CompletedTask;
        public Task AttemptFailedAsync(Guid runId, string nodeId, string iterationKey, int attempt, int maxAttempts, string error, TimeSpan duration, double retryInSeconds, Guid? parentRecordId, CancellationToken ct) => Task.CompletedTask;
        public Task NodeSkippedAsync(Guid runId, string nodeId, string iterationKey, string reason, CancellationToken ct) => Task.CompletedTask;
        public Task NodeSuspendedAsync(Guid runId, string nodeId, string iterationKey, string waitKind, DateTimeOffset? wakeAt, CancellationToken ct) => Task.CompletedTask;
        public Task IterationStartedAsync(Guid runId, string nodeId, int itemCount, CancellationToken ct) => Task.CompletedTask;
        public Task IterationCompletedAsync(Guid runId, string nodeId, int itemCount, TimeSpan duration, CancellationToken ct) => Task.CompletedTask;
        public Task<(Guid RecordId, Guid CorrelationId)> ExternalCallStartedAsync(Guid runId, string? nodeId, string target, string method, JsonElement? requestPayload, Guid? parentRecordId, CancellationToken ct) => Task.FromResult((Guid.NewGuid(), Guid.NewGuid()));
        public Task ExternalCallCompletedAsync(Guid runId, string? nodeId, Guid correlationId, int? statusCode, JsonElement? responsePayload, TimeSpan duration, CancellationToken ct) => Task.CompletedTask;
        public Task ExternalCallFailedAsync(Guid runId, string? nodeId, Guid correlationId, string target, string error, TimeSpan duration, CancellationToken ct) => Task.CompletedTask;
        public Task LogAsync(Guid runId, string? nodeId, LogLevel level, string message, CancellationToken ct) => Task.CompletedTask;
        public Task WaitReissuedAsync(Guid runId, string nodeId, string iterationKey, string waitKind, Guid waitId, Guid byUserId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>A minimal ledger: serves only FindBlockingDecisionIdAsync (a configurable blocking id, null = none). Every other member throws — the review step calls none of them.</summary>
    private sealed class FakeLedger : IToolCallLedgerService
    {
        private readonly Guid? _blocking;
        public FakeLedger(Guid? blocking) { _blocking = blocking; }

        public Task<Guid?> FindBlockingDecisionIdAsync(Guid agentRunId, CancellationToken cancellationToken) => Task.FromResult(_blocking);

        public Task<ToolCallClaim> TryClaimAsync(Guid agentRunId, Guid teamId, string toolKind, string idempotencyKey, string inputHash, long fenceEpoch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RecordTerminalAsync(Guid ledgerId, Guid teamId, ToolCallLedgerStatus status, string? resultJson, string? error, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryBeginApprovalAsync(Guid ledgerId, Guid teamId, string approvalToken, DateTimeOffset deadlineAt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetApprovalMessageAsync(Guid ledgerId, Guid teamId, Guid messageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryBeginExecutionAsync(Guid ledgerId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ToolCallApprovalState?> ReadApprovalStateAsync(Guid ledgerId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryAnswerDecisionAsync(Guid ledgerId, Guid teamId, string answerJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetDecisionEnvelopeAsync(Guid ledgerId, Guid teamId, string envelopeJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExpiredToolApproval>> ExpireStaleApprovalsAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<TimedOutDecision>> ExpireStaleDecisionsAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> ExpireStaleToolCallsAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CountPendingDecisionsAsync(Guid agentRunId, Guid teamId, string excludeIdempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ToolCallLedger>> GetForRunAsync(Guid agentRunId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingCritic : IStructuredCritic
    {
        public CriticVerdict Verdict { get; set; } = new() { Mode = ReviewMode.Gate };
        public bool Called { get; private set; }

        /// <summary>The ambient LlmCallScope in flight when the critic was invoked — the executor's recording push, or null when none.</summary>
        public LlmCallScope? ObservedScope { get; private set; }

        public Task<CriticVerdict> ReviewAsync(CriticRequest request, Guid teamId, Guid? reviewerModelId, CancellationToken cancellationToken)
        {
            Called = true;
            ObservedScope = LlmCallContext.Current;
            return Task.FromResult(Verdict);
        }
    }

    /// <summary>Minimal IAgentRunService: records appended warning events. Every other member throws — the review step calls none of them.</summary>
    private sealed class StubRuns : IAgentRunService
    {
        private readonly Guid _runId;
        public List<AgentEvent> AppendedEvents { get; } = new();

        public StubRuns(Guid runId) { _runId = runId; }

        public Task<AgentRunEvent> AppendEventAsync(Guid runId, AgentEvent @event, CancellationToken cancellationToken)
        {
            AppendedEvents.Add(@event);
            return Task.FromResult(new AgentRunEvent { Id = Guid.NewGuid(), AgentRunId = runId, Kind = @event.Kind, Text = @event.Text });
        }

        public Task<AgentRun> GetAsync(Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ResumableSession?> FindResumableSessionAsync(Guid teamId, Guid? parentRunId, string nodeId, string iterationKey, CancellationToken cancellationToken) => Task.FromResult<ResumableSession?>(null);

        public Task<ResumableSession?> FindResumableSubtaskAttemptAsync(Guid teamId, Guid supervisorRunId, string subtaskId, CancellationToken cancellationToken) => Task.FromResult<ResumableSession?>(null);
        public Task AppendEventsAsync(Guid runId, IReadOnlyList<AgentEvent> events, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AgentRun> CreateAsync(AgentTask task, Guid teamId, Guid? workflowRunId, string? nodeId, string iterationKey = "", CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> MarkRunningAsync(Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task HeartbeatAsync(Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ReclaimForReattachAsync(Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetRunnerHandleAsync(Guid runId, string handleJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteAsync(Guid runId, AgentRunResult result, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteAsync(Guid runId, AgentRunResult result, long expectedEpoch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CancelQueuedAsync(Guid runId, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CancelRunningAsync(Guid runId, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CodeSpace.Messages.Dtos.Agents.AgentRunSummary?> GetSummaryForTeamAsync(Guid runId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentRunEvent>> GetEventsAsync(Guid runId, Guid teamId, long afterSequence, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
