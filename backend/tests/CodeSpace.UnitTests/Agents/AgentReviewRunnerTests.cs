using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Review;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 5.6 residual — <see cref="AgentReviewRunner.RunAsync"/>'s catch-all (every fault except a cancellation, an
/// authority denial, or an ownership loss — <c>AgentReviewDelegationFlowTests</c> pins those three propagate)
/// must ladder to the model critic with an HONEST failed verdict. PR #1835 fixed the one known cause of a review
/// never starting (a background reviewer denied authority for lacking a real user); these pin that every OTHER cause
/// — a sub-run that fails to STAGE for an infra reason, and an executor that can never be RESOLVED — still returns a
/// <see cref="CriticVerdict"/> that is unmistakably a failure, never a silent "approved, nothing to report".
/// </summary>
[Trait("Category", "Unit")]
public sealed class AgentReviewRunnerTests
{
    [Fact]
    public async Task A_sub_run_creation_fault_ladders_to_a_reason_bearing_failed_verdict()
    {
        var runner = NewRunner(new ThrowingRuns(createFault: new TimeoutException("gateway timed out")));

        var verdict = await runner.RunAsync(Spec(), CancellationToken.None);

        verdict.Failed.ShouldBeTrue("an infra fault while staging the reviewer is not a verdict — the caller must know none ran");
        verdict.Approved.ShouldBeFalse("a fault must never read as an approval");
        verdict.Rationale.ShouldNotBeNullOrWhiteSpace("the caller threads this onto the result/Room — silence here reproduces the fail-open bug one layer up");
        verdict.Rationale.ShouldContain("gateway timed out", customMessage: "the reason names the actual fault, not a generic placeholder");
        verdict.Issues.ShouldBeEmpty();
        verdict.Critique.ShouldBeNull();
    }

    [Fact]
    public async Task An_unresolvable_executor_ladders_to_a_reason_bearing_failed_verdict()
    {
        // The sub-run stages fine; the fresh scope this worker resolves it from can never produce IAgentRunExecutor
        // (an empty container — the "runner unavailable" shape). RunAsync's own catch-all is the only thing standing
        // between that DI fault and a caller that would otherwise never learn the review never executed.
        var runner = NewRunner(new ThrowingRuns(createResult: FakeQueuedRun()), EmptyScopeFactory());

        var verdict = await runner.RunAsync(Spec(), CancellationToken.None);

        verdict.Failed.ShouldBeTrue("the reviewer run could never execute — there is nothing to report a verdict about");
        verdict.Approved.ShouldBeFalse();
        verdict.Rationale.ShouldNotBeNullOrWhiteSpace();
    }

    private static AgentReviewRunner NewRunner(IAgentRunService runs, IServiceScopeFactory? scopeFactory = null) =>
        new(runs, new AgentHarnessRegistry(Array.Empty<IAgentHarness>()), scopeFactory ?? EmptyScopeFactory(), NullLogger<AgentReviewRunner>.Instance);

    /// <summary>An empty DI container — resolving ANY service (here, <c>IAgentRunExecutor</c> from a fresh scope) throws the standard "no service registered" <see cref="InvalidOperationException"/>, the cheapest honest stand-in for "the runner is unavailable".</summary>
    private static IServiceScopeFactory EmptyScopeFactory() => new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    private static AgentReviewSpec Spec() => new()
    {
        SubjectInstructions = "inspect the produced branch", RepositoryId = Guid.NewGuid(), TeamId = Guid.NewGuid(), BaseRef = "codespace/agent/x", IterationKey = "#review",
    };

    private static AgentRun FakeQueuedRun() => new() { Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), Status = AgentRunStatus.Queued, IterationKey = "#review", Harness = "codex-cli" };

    /// <summary>Minimal IAgentRunService: CreateAsync/CreateReviewAsync either throw a configured fault or hand back a configured run. Every other member throws — RunAsync's staging path never reaches them (an unresolvable executor faults before GetAsync, and a staging fault never gets that far at all).</summary>
    private sealed class ThrowingRuns : IAgentRunService
    {
        private readonly Exception? _createFault;
        private readonly AgentRun? _createResult;

        public ThrowingRuns(Exception createFault) { _createFault = createFault; }
        public ThrowingRuns(AgentRun createResult) { _createResult = createResult; }

        public Task<AgentRun> CreateAsync(AgentTask task, Guid teamId, Guid? workflowRunId, string? nodeId, string iterationKey = "", CancellationToken cancellationToken = default) =>
            _createFault is { } fault ? throw fault : Task.FromResult(_createResult!);

        public Task<AgentRun> CreateReviewAsync(AgentReviewCreation request, CancellationToken cancellationToken) =>
            _createFault is { } fault ? throw fault : Task.FromResult(_createResult!);

        public Task<AgentRun> GetAsync(Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResumableSession?> FindResumableSessionAsync(Guid teamId, Guid? parentRunId, string nodeId, string iterationKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResumableSession?> FindResumableSubtaskAttemptAsync(Guid teamId, Guid supervisorRunId, string subtaskId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AgentRunEvent> AppendEventAsync(Guid runId, AgentEvent @event, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AppendEventsAsync(Guid runId, IReadOnlyList<AgentEvent> events, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AgentRunEvent> AppendSystemEventAsync(Guid runId, AgentEvent @event, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RejectQueuedAsync(Guid runId, AgentRunResult result, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AgentRunReattachReservation?> ReserveReattachAsync(AgentRunReconciliationCandidate candidate, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AgentRunOwnerToken?> ClaimOwnershipAsync(Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AgentRunReattachReservation?> ReserveReattachAsync(Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AgentRunOwnerToken?> ActivateReattachAsync(AgentRunReattachReservation reservation, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AssertOwnershipAsync(AgentRunOwnerToken owner, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task HeartbeatAsync(AgentRunOwnerToken owner, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetRunnerHandleAsync(AgentRunOwnerToken owner, string handleJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetSandboxConfinementAsync(AgentRunOwnerToken owner, string confinementJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AgentRunEvent> AppendEventAsync(AgentRunOwnerToken owner, AgentEvent @event, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AppendEventsAsync(AgentRunOwnerToken owner, IReadOnlyList<AgentEvent> events, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteAsync(AgentRunOwnerToken owner, AgentRunResult result, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<long> MarkRunningAsync(Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task HeartbeatAsync(Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ReclaimForReattachAsync(Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetRunnerHandleAsync(Guid runId, string handleJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetSandboxConfinementAsync(Guid runId, string confinementJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteAsync(Guid runId, AgentRunResult result, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteAsync(Guid runId, AgentRunResult result, long expectedEpoch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CancelQueuedAsync(Guid runId, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CancelRunningAsync(Guid runId, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CodeSpace.Messages.Dtos.Agents.AgentRunSummary?> GetSummaryForTeamAsync(Guid runId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentRunEvent>> GetEventsAsync(Guid runId, Guid teamId, long afterSequence, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
