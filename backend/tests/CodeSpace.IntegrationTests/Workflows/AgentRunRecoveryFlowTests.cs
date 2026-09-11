using System.Diagnostics;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Capture;
using CodeSpace.Core.Services.Agents.Recovery;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Recovery;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Crash-recovery for agents — the analogue of AbandonedRunRecoveryFlowTests. Simulates the post-crash
/// state (a Running run whose worker vanished — a killed pod / rolling update) and proves the reconciler:
///   1. flips a genuinely abandoned run (stale heartbeat AND no recent events) to Failed + logs it;
///   2. leaves a run with a FRESH heartbeat alone (worker alive);
///   3. leaves a run with a STALE heartbeat but RECENT events alone (streaming agent still emitting);
///   4. never touches a terminal run (the CAS WHERE status=Running is a no-op).
/// Assertions are scoped to each test's own run id, so they're robust against the shared test DB.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentRunRecoveryFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly List<string> _spoolDirs = new();
    private readonly List<int> _launchedPids = new();

    public AgentRunRecoveryFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Abandoned_running_run_is_failed_with_a_recovery_event()
    {
        var teamId = await SeedTeamAsync();
        var runId = await SeedRunAsync(teamId, AgentRunStatus.Running, livenessAgo: TimeSpan.FromMinutes(20), withRecentEvent: false);

        int marked;
        using (var scope = _fixture.BeginScope())
            marked = (await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None)).MarkedAbandonedFromRunning;

        marked.ShouldBeGreaterThanOrEqualTo(1);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var run = await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(AgentRunStatus.Failed);
        run.Error.ShouldNotBeNull();
        run.Error!.ShouldContain("abandoned");
        run.CompletedAt.ShouldNotBeNull();

        var hasErrorEvent = await db.AgentRunEvent.AsNoTracking().AnyAsync(e => e.AgentRunId == runId && e.Kind == AgentEventKind.Error);
        hasErrorEvent.ShouldBeTrue("the reconciler appends an Error event so the timeline shows the abandonment");
    }

    [Fact]
    public async Task Running_run_with_a_fresh_heartbeat_is_left_alone()
    {
        var teamId = await SeedTeamAsync();
        var runId = await SeedRunAsync(teamId, AgentRunStatus.Running, livenessAgo: TimeSpan.FromSeconds(10), withRecentEvent: false);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

        using var verify = _fixture.BeginScope();
        var run = await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(AgentRunStatus.Running);
    }

    [Fact]
    public async Task Running_run_with_stale_heartbeat_but_recent_events_is_left_alone()
    {
        var teamId = await SeedTeamAsync();
        var runId = await SeedRunAsync(teamId, AgentRunStatus.Running, livenessAgo: TimeSpan.FromMinutes(20), withRecentEvent: true);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

        using var verify = _fixture.BeginScope();
        var run = await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(AgentRunStatus.Running, "recent event activity proves liveness even when the heartbeat is stale");
    }

    [Fact]
    public async Task Terminal_run_is_never_touched()
    {
        var teamId = await SeedTeamAsync();
        var runId = await SeedRunAsync(teamId, AgentRunStatus.Succeeded, livenessAgo: TimeSpan.FromMinutes(30), withRecentEvent: false);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

        using var verify = _fixture.BeginScope();
        var run = await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(AgentRunStatus.Succeeded, "the CAS WHERE status=Running is a no-op on an already-terminal run");
    }

    [Fact]
    public async Task Durable_run_that_finished_unobserved_is_recovered_as_succeeded()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await SeedDurableRunAsync(teamId, processId: DeadPid(), exitCode: 0);

        AgentRunReconcileSummary summary;
        using (var scope = _fixture.BeginScope())
            summary = await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

        summary.RecoveredFromSpool.ShouldBeGreaterThanOrEqualTo(1);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var run = await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.Status.ShouldBe(AgentRunStatus.Succeeded, "the exit marker proves it finished cleanly while unobserved — recover, don't abandon");
        run.CompletedAt.ShouldNotBeNull();
        run.ResultJson.ShouldNotBeNull();
        (await db.AgentRunEvent.AsNoTracking().AnyAsync(e => e.AgentRunId == runId && e.Kind == AgentEventKind.Completed))
            .ShouldBeTrue("a recovery event records the salvage on the timeline");
    }

    [Fact]
    public async Task Durable_run_finished_unobserved_with_an_unanswered_decision_is_recovered_as_needs_review()
    {
        // Slice A1 completion contract on the CRASH-RECOVERY path: even when the reconciler salvages a run from its
        // exit-0 spool marker (it finished cleanly while unobserved), a decision it raised that's still unanswered
        // means it can't be called Succeeded — it's recovered as NeedsReview, mirroring AgentRunService.CompleteAsync,
        // so the invariant holds on BOTH terminal write paths.
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await SeedDurableRunAsync(teamId, processId: DeadPid(), exitCode: 0);
        var decisionId = await SeedPendingDecisionAsync(teamId, runId);

        AgentRunReconcileSummary summary;
        using (var scope = _fixture.BeginScope())
            summary = await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

        summary.RecoveredFromSpool.ShouldBeGreaterThanOrEqualTo(1, "the exit-0 marker still drives a spool recovery");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var run = await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.Status.ShouldBe(AgentRunStatus.NeedsReview, "a clean exit can't bury an unanswered decision even on the recovery path");
        var stored = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
        stored.CompletionDisposition.ShouldBe(CompletionDisposition.NeedsDecision);
        stored.PendingDecisionId.ShouldBe(decisionId);
        (await db.AgentRunEvent.AsNoTracking().AnyAsync(e => e.AgentRunId == runId && e.Kind == AgentEventKind.Warning))
            .ShouldBeTrue("the recovered NeedsReview is recorded as a Warning, not a Completed/Error event");
    }

    [Fact]
    public async Task Durable_run_that_failed_unobserved_is_recovered_as_failed_with_the_exit_code()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await SeedDurableRunAsync(teamId, processId: DeadPid(), exitCode: 5);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

        using var verify = _fixture.BeginScope();
        var run = await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.Status.ShouldBe(AgentRunStatus.Failed);
        run.Error.ShouldNotBeNull();
        run.Error!.ShouldContain("5", customMessage: "the recovered failure names the exit code");
    }

    [Fact]
    public async Task Durable_run_gone_without_a_marker_is_abandoned()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await SeedDurableRunAsync(teamId, processId: DeadPid(), exitCode: null);   // no marker → killed before finishing

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

        using var verify = _fixture.BeginScope();
        var run = await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.Status.ShouldBe(AgentRunStatus.Failed);
        run.Error!.ShouldContain("abandoned", customMessage: "a gone-without-a-marker durable run is abandoned, like a non-durable one");
    }

    [Fact]
    public async Task Durable_run_whose_handle_names_another_host_is_not_swept_as_dead()
    {
        if (OperatingSystem.IsWindows()) return;

        // The multi-worker live-run-loss case. Worker A launched the run and died; its setsid-detached supervisor is
        // still working on host A. Worker B runs this sweep, and A's pid resolves to NOTHING in B's namespace — the
        // pre-fix reconciler read that as Gone and abandoned a live run. B has no evidence of death here, so the run
        // must survive the sweep and wait for one that lands on the host where its pid means something.
        var teamId = await SeedTeamAsync();
        var runId = await SeedDurableRunAsync(teamId, processId: DeadPid(), exitCode: null, launchHost: "some-other-worker-host");

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

        using var verify = _fixture.BeginScope();
        var run = await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.Status.ShouldBe(AgentRunStatus.Running,
            "a pid this worker cannot resolve is not a dead run — abandoning on it destroys a live run on the host that minted it");
        run.CompletedAt.ShouldBeNull("nothing terminal happened, so no completion stamp");
    }

    [Fact]
    public async Task Foreign_host_run_past_its_wall_clock_deadline_is_abandoned()
    {
        if (OperatingSystem.IsWindows()) return;

        // The anti-stuck half of the same decision: deferring forever would leave a run Running for good whenever the
        // minting host never comes back (a replaced pod). Past the handle's own Deadline no observer can still be
        // completing it — a ground that needs no pid — so the deferral ends there.
        var teamId = await SeedTeamAsync();
        var runId = await SeedDurableRunAsync(teamId, processId: DeadPid(), exitCode: null,
            launchHost: "a-host-that-never-came-back", deadline: DateTimeOffset.UtcNow.AddMinutes(-1));

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

        using var verify = _fixture.BeginScope();
        var run = await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.Status.ShouldBe(AgentRunStatus.Failed, "a foreign handle past its deadline must still reach a terminal state");
        run.Error!.ShouldContain("abandoned");

        // Terminal is not the whole story: the run's spool is still on a host this worker cannot reach, and before the
        // cleanup ledger existed nothing anywhere named it. Every receipt must be a CLAIM, never a reclaim.
        var receipts = await verify.Resolve<IRunCleanupLedger>().ForRunsAsync(teamId, [runId], CancellationToken.None);
        receipts.ShouldContain(receipt => receipt.Kind == RunResourceKind.Spool && receipt.Outcome == RunResourceOutcome.Orphaned
            && receipt.OwnerHost == "a-host-that-never-came-back");
        receipts.ShouldAllBe(receipt => !receipt.IsSettled, "this worker freed nothing, so nothing here may read as freed");
    }

    [Fact]
    public async Task Same_host_abandon_settles_its_own_isolation_instead_of_orphaning_it()
    {
        if (OperatingSystem.IsWindows()) return;

        // The mirror of the foreign case, and the reason the branch cannot simply always record orphans: a sweep
        // standing on the run's OWN host really can tear its netns down, so the receipt must say what happened here —
        // Completed where the tools exist, Unknown where they cannot even be attempted — and never "orphaned", which
        // would send a reader looking for a host to reap that is the one they are already on.
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();
        var spoolDir = Path.Combine(Path.GetTempPath(), "cs-recover-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(spoolDir);
        _spoolDirs.Add(spoolDir);

        var handle = new SandboxHandle
        {
            Kind = "local", ProcessId = DeadPid(), LaunchHost = LocalProcessRunner.CurrentHost, SpoolDirectory = spoolDir,
            Deadline = DateTimeOffset.UtcNow.AddMinutes(-1), EgressNetnsKey = runId.ToString("N"),
        };
        var stamp = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.AgentRun.Add(new AgentRun
            {
                Id = runId, TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.Running, FenceEpoch = 1,
                StartedAt = stamp, HeartbeatAt = stamp, LeaseExpiresAt = stamp + AgentRunLiveness.Window,
                RunnerHandleJson = JsonSerializer.Serialize(handle, AgentJson.Options),
            });
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

        using var verify = _fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(AgentRunStatus.Failed);

        var netns = (await verify.Resolve<IRunCleanupLedger>().ForRunsAsync(teamId, [runId], CancellationToken.None))
            .Single(receipt => receipt.Kind == RunResourceKind.EgressSubnet);
        netns.Outcome.ShouldBe(FilteredEgressNetns.IsSupported ? RunResourceOutcome.Completed : RunResourceOutcome.Unknown);
        netns.ErrorCode.ShouldBe(FilteredEgressNetns.IsSupported ? null : RunCleanupReceipts.UnsupportedCode);
        netns.OwnerHost.ShouldBe(LocalProcessRunner.CurrentHost);
    }

    [Fact]
    public async Task Durable_run_whose_process_is_still_alive_is_left_for_reattach()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        // Point the handle at THIS test process (definitely alive) with no marker → probe Running → leave it.
        var runId = await SeedDurableRunAsync(teamId, processId: Environment.ProcessId, exitCode: null);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

        using var verify = _fixture.BeginScope();
        var run = await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.Status.ShouldBe(AgentRunStatus.Running, "a durable run whose supervised process is still alive must be left for re-attach, not abandoned");
    }

    [Fact]
    public async Task Alive_durable_run_past_the_reattach_ceiling_is_killed_then_abandoned()
    {
        if (OperatingSystem.IsWindows()) return;

        // The orphan-key-burner scenario: a real durable agent process is still ALIVE, but its run has exhausted
        // the re-attach budget. The reconciler must KILL it before abandoning — otherwise it runs on to its
        // wall-clock deadline holding the workspace and burning the injected model credential after the DB says Failed.
        var teamId = await SeedTeamAsync();
        var (runId, pid) = await SeedAliveDurableRunAsync(teamId, reattachAttempts: AgentRunReconcilerService.MaxReattachAttempts);

        ProcessAlive(pid).ShouldBeTrue("precondition: the launched durable agent process is running before the sweep");

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None))
                .MarkedAbandonedFromRunning.ShouldBeGreaterThanOrEqualTo(1);

        using var verify = _fixture.BeginScope();
        var run = await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(AgentRunStatus.Failed);
        run.Error!.ShouldContain("abandoned", customMessage: "a permanently-unattachable alive run is abandoned past the ceiling");

        // The orphan must be DEAD — not orphaned to its deadline. The kill is a signal + reap, so poll briefly.
        (await WaitForProcessGoneAsync(pid)).ShouldBeTrue(
            "the reconciler must KILL a still-alive run it abandons past the re-attach ceiling, not leave it running");
    }

    [Fact]
    public async Task A_fresh_lease_with_a_stale_heartbeat_is_left_alone()
    {
        // The reconciler gates on the LEASE (ground truth — a live worker keeps renewing it), NOT on
        // heartbeat-silence: an old HeartbeatAt but a still-valid lease must NOT be reclaimed.
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.AgentRun.Add(new AgentRun
            {
                Id = runId, TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.Running,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-20),
                HeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-20),   // stale heartbeat…
                LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2),  // …but the lease is still valid
            });
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

        using var verify = _fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(AgentRunStatus.Running, "a valid lease (a live worker) protects the run despite a stale heartbeat");
    }

    // The defect the six tests below pin: the reconciler's terminal paths flip the Agent Run but never touch its own
    // open WorkflowRunHarnessProcessAttempt / WorkflowRunHarnessExecution rows, so a phantom "live" process survives
    // forever in every reader of the native record plane even after the run itself is Failed. Each case mirrors an
    // existing scenario above, adding a real native-record attempt to the seeded run and asserting BOTH facts hold:
    // the run's own terminal state is unchanged from today, and the native rows the reconciler leaves behind are
    // closed with a cause an operator can tell apart from every other one.

    [Fact]
    public async Task Abandoned_running_run_with_no_handle_closes_its_open_native_attempt()
    {
        var teamId = await SeedTeamAsync();
        var runId = await SeedRunAsync(teamId, AgentRunStatus.Running, livenessAgo: TimeSpan.FromMinutes(20), withRecentEvent: false, fenceEpoch: 1);
        var handle = await SeedOpenAttemptAsync(teamId, runId, fenceEpoch: 1);

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None)).MarkedAbandonedFromRunning.ShouldBeGreaterThanOrEqualTo(1);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(AgentRunStatus.Failed, "the reconciler's own terminal decision must not change");

        var attempt = await db.WorkflowRunHarnessProcessAttempt.AsNoTracking().SingleAsync(a => a.Id == handle.AttemptId);
        attempt.State.ShouldBe(HarnessProcessAttemptState.Lost, "an abandon with no handle must close the open attempt row it leaves behind, not leave it Running forever");
        attempt.ExitCode.ShouldBeNull();
        attempt.ErrorCode.ShouldBe(NativeRecordPlane.ReconcilerAbandonedNoHandleErrorCode);
        attempt.ClaimOwnerId.ShouldBeNull();

        var execution = await db.WorkflowRunHarnessExecution.AsNoTracking().SingleAsync(e => e.Id == handle.ExecutionId);
        execution.State.ShouldBe(HarnessExecutionState.Exited, "a launched process (attempt_count > 0) closes Exited, never Abandoned");
        execution.TerminalAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Durable_run_gone_without_a_marker_closes_its_open_native_attempt()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await SeedDurableRunAsync(teamId, processId: DeadPid(), exitCode: null, fenceEpoch: 1);
        var handle = await SeedOpenAttemptAsync(teamId, runId, fenceEpoch: 1);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(AgentRunStatus.Failed);

        var attempt = await db.WorkflowRunHarnessProcessAttempt.AsNoTracking().SingleAsync(a => a.Id == handle.AttemptId);
        attempt.State.ShouldBe(HarnessProcessAttemptState.Lost, "a probe-confirmed-dead abandon must close the open attempt row");
        attempt.ErrorCode.ShouldBe(NativeRecordPlane.ReconcilerAbandonedProcessDeadErrorCode);

        (await db.WorkflowRunHarnessExecution.AsNoTracking().SingleAsync(e => e.Id == handle.ExecutionId)).State.ShouldBe(HarnessExecutionState.Exited);
    }

    [Fact]
    public async Task Foreign_host_run_past_its_wall_clock_deadline_closes_its_open_native_attempt()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await SeedDurableRunAsync(teamId, processId: DeadPid(), exitCode: null,
            launchHost: "a-host-that-never-came-back", deadline: DateTimeOffset.UtcNow.AddMinutes(-1), fenceEpoch: 1);
        var handle = await SeedOpenAttemptAsync(teamId, runId, fenceEpoch: 1);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(AgentRunStatus.Failed);

        var attempt = await db.WorkflowRunHarnessProcessAttempt.AsNoTracking().SingleAsync(a => a.Id == handle.AttemptId);
        attempt.State.ShouldBe(HarnessProcessAttemptState.Lost, "a lease-lapsed give-up past a foreign host's deadline must close the open attempt row");
        attempt.ErrorCode.ShouldBe(NativeRecordPlane.ReconcilerAbandonedLeaseLapsedErrorCode);

        (await db.WorkflowRunHarnessExecution.AsNoTracking().SingleAsync(e => e.Id == handle.ExecutionId)).State.ShouldBe(HarnessExecutionState.Exited);
    }

    [Fact]
    public async Task Alive_durable_run_past_the_reattach_ceiling_closes_its_open_native_attempt()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var (runId, pid) = await SeedAliveDurableRunAsync(teamId, reattachAttempts: AgentRunReconcilerService.MaxReattachAttempts, fenceEpoch: 1);
        var handle = await SeedOpenAttemptAsync(teamId, runId, fenceEpoch: 1);

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None)).MarkedAbandonedFromRunning.ShouldBeGreaterThanOrEqualTo(1);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(AgentRunStatus.Failed);

        var attempt = await db.WorkflowRunHarnessProcessAttempt.AsNoTracking().SingleAsync(a => a.Id == handle.AttemptId);
        attempt.State.ShouldBe(HarnessProcessAttemptState.Lost, "a reattach-ceiling abandon of a still-alive-but-unattachable run must close the open attempt row");
        attempt.ErrorCode.ShouldBe(NativeRecordPlane.ReconcilerAbandonedLeaseLapsedErrorCode);

        (await db.WorkflowRunHarnessExecution.AsNoTracking().SingleAsync(e => e.Id == handle.ExecutionId)).State.ShouldBe(HarnessExecutionState.Exited);
        (await WaitForProcessGoneAsync(pid)).ShouldBeTrue("the kill is unrelated to the native-record close, and must still happen");
    }

    [Fact]
    public async Task Durable_run_that_finished_unobserved_closes_its_open_native_attempt()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await SeedDurableRunAsync(teamId, processId: DeadPid(), exitCode: 0, fenceEpoch: 1);
        var handle = await SeedOpenAttemptAsync(teamId, runId, fenceEpoch: 1);

        AgentRunReconcileSummary summary;
        using (var scope = _fixture.BeginScope())
            summary = await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

        summary.RecoveredFromSpool.ShouldBeGreaterThanOrEqualTo(1);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(AgentRunStatus.Succeeded, "the exit marker recovery outcome must not change");

        // The native plane's own observer genuinely never recorded this exit — the spool marker is a different, out-
        // of-band signal — so the SAME generic reason the executor's own forced terminals use is the honest one here.
        var attempt = await db.WorkflowRunHarnessProcessAttempt.AsNoTracking().SingleAsync(a => a.Id == handle.AttemptId);
        attempt.State.ShouldBe(HarnessProcessAttemptState.Lost, "a spool recovery must close the native plane's own open attempt row too");
        attempt.ErrorCode.ShouldBe(NativeRecordPlane.ProcessOutcomeUnrecordedErrorCode);

        (await db.WorkflowRunHarnessExecution.AsNoTracking().SingleAsync(e => e.Id == handle.ExecutionId)).State.ShouldBe(HarnessExecutionState.Exited);
    }

    [Fact]
    public async Task A_native_attempt_the_reconciler_abandoned_cannot_be_reopened_by_a_late_close()
    {
        var teamId = await SeedTeamAsync();
        var runId = await SeedRunAsync(teamId, AgentRunStatus.Running, livenessAgo: TimeSpan.FromMinutes(20), withRecentEvent: false, fenceEpoch: 1);
        var handle = await SeedOpenAttemptAsync(teamId, runId, fenceEpoch: 1);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

        // A late writer: the ORIGINAL worker's own executor, unaware its run was already reconciled, still reaches
        // its ordinary happy-path close for the same attempt — under the epoch it originally launched at.
        using (var lateScope = _fixture.BeginScope())
            await lateScope.Resolve<INativeRecordPlane>().CloseAsync(handle, exitCode: 0, handle.WorkerFenceEpoch, CancellationToken.None);

        using var verify = _fixture.BeginScope();
        var attempt = await verify.Resolve<CodeSpaceDbContext>().WorkflowRunHarnessProcessAttempt.AsNoTracking().SingleAsync(a => a.Id == handle.AttemptId);

        attempt.State.ShouldBe(HarnessProcessAttemptState.Lost, "the reconciler's close already landed; a late writer's happy-path close must not reopen or overwrite it");
        attempt.ExitCode.ShouldBeNull("a late writer's exit code must never overwrite an already-terminal row");
        attempt.ErrorCode.ShouldBe(NativeRecordPlane.ReconcilerAbandonedNoHandleErrorCode);
    }

    // P07/P09 3a: the sibling of the two "late close" tests above, for the race those cannot reach — a RE-ATTACH
    // rather than an abandon/cancel. A re-attach deliberately leaves the attempt row Running (ReopenAsync hands the
    // SAME AttemptId to the fresh observer, per INativeRecordExecutionPlane's own docs), so the status-guarded CAS
    // alone cannot refuse a stale worker's close here — only the run's own fence can. Counterexample for
    // NativeRecordPlane.CloseAsync before it accepted an expectedEpoch: a superseded worker's happy-path close would
    // have landed Exited over an attempt the reattached worker is still observing.
    [Fact]
    public async Task A_stale_worker_after_a_reattach_cannot_close_the_attempt_its_reattacher_still_observes()
    {
        var teamId = await SeedTeamAsync();
        var runId = await SeedRunAsync(teamId, AgentRunStatus.Running, livenessAgo: TimeSpan.FromMinutes(20), withRecentEvent: false, fenceEpoch: 1);
        var handle = await SeedOpenAttemptAsync(teamId, runId, fenceEpoch: 1);

        // Reclaim the run for re-attach WITHOUT touching the native record plane at all — exactly what
        // AgentRunReconcilerService.ReattachAsync does before dispatching a fresh observer: the process is alive, so
        // the attempt stays Running while a new worker resumes tailing it.
        long reclaimedEpoch;
        using (var reclaim = _fixture.BeginScope())
            reclaimedEpoch = (await reclaim.Resolve<IAgentRunService>().ReserveReattachAsync(runId, CancellationToken.None)).ShouldNotBeNull().Epoch;

        reclaimedEpoch.ShouldBe(handle.WorkerFenceEpoch + 1, "the premise: the run moved to a fresh generation the stale worker never saw");

        using (var precondition = _fixture.BeginScope())
            (await precondition.Resolve<CodeSpaceDbContext>().WorkflowRunHarnessProcessAttempt.AsNoTracking().SingleAsync(a => a.Id == handle.AttemptId)).State
                .ShouldBe(HarnessProcessAttemptState.Running, "the reclaim itself must not close the attempt — that is exactly the state a re-attach expects to find");

        // The STALE worker: unaware of the reclaim, it reaches its ordinary happy-path close under the epoch it
        // originally launched at — which the CURRENT code must refuse rather than stamp over the live re-attach.
        using (var staleScope = _fixture.BeginScope())
            await staleScope.Resolve<INativeRecordPlane>().CloseAsync(handle, exitCode: 0, handle.WorkerFenceEpoch, CancellationToken.None);

        using (var verify = _fixture.BeginScope())
        {
            var attempt = await verify.Resolve<CodeSpaceDbContext>().WorkflowRunHarnessProcessAttempt.AsNoTracking().SingleAsync(a => a.Id == handle.AttemptId);
            attempt.State.ShouldBe(HarnessProcessAttemptState.Running, "a stale-epoch close must not stamp the attempt the reattached worker is still observing");
            attempt.ExitCode.ShouldBeNull("a superseded worker's exit code must never land on a re-attached attempt");
        }

        // The CURRENT (reattached) owner's own close, at the epoch it actually holds, must still land normally.
        using (var currentScope = _fixture.BeginScope())
            await currentScope.Resolve<INativeRecordPlane>().CloseAsync(handle, exitCode: 0, reclaimedEpoch, CancellationToken.None);

        using (var verify = _fixture.BeginScope())
        {
            var attempt = await verify.Resolve<CodeSpaceDbContext>().WorkflowRunHarnessProcessAttempt.AsNoTracking().SingleAsync(a => a.Id == handle.AttemptId);
            attempt.State.ShouldBe(HarnessProcessAttemptState.Exited, "the CURRENT owner's close, at its own live fence, must still be able to land");
            attempt.ExitCode.ShouldBe(0);
        }
    }

    // The sibling of the six tests above, for the kill-wave's own parent-terminal Running sweep
    // (AgentRunReconcilerService.CancelOrphanRunningAsync): it too flips the Agent Run via CancelRunningAsync's CAS
    // without touching its open native-record rows, so the same phantom-live-process defect applies to a cancelled
    // orphan exactly as it does to an abandoned one — just stamped with a cause naming the parent-terminal sweep
    // rather than a give-up.

    [Fact]
    public async Task Reconciler_kill_wave_cancel_of_a_running_orphan_closes_its_open_native_attempt()
    {
        var teamId = await SeedTeamAsync();
        var parentRunId = await SeedTerminalWorkflowRunAsync(teamId, WorkflowRunStatus.Cancelled);
        var runId = await SeedRunAsync(teamId, AgentRunStatus.Running, livenessAgo: TimeSpan.Zero, withRecentEvent: false, fenceEpoch: 1, workflowRunId: parentRunId);
        var handle = await SeedOpenAttemptAsync(teamId, runId, fenceEpoch: 1);

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None)).CancelledRunningUnderTerminalParent.ShouldBeGreaterThanOrEqualTo(1);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(AgentRunStatus.Cancelled, "the parent-terminal kill-wave's own terminal decision must not change");

        var attempt = await db.WorkflowRunHarnessProcessAttempt.AsNoTracking().SingleAsync(a => a.Id == handle.AttemptId);
        attempt.State.ShouldBe(HarnessProcessAttemptState.Lost, "the kill-wave backstop must close the open attempt row it leaves behind, not leave it Running forever");
        attempt.ExitCode.ShouldBeNull();
        attempt.ErrorCode.ShouldBe(NativeRecordPlane.CancelParentTerminalErrorCode);

        var execution = await db.WorkflowRunHarnessExecution.AsNoTracking().SingleAsync(e => e.Id == handle.ExecutionId);
        execution.State.ShouldBe(HarnessExecutionState.Exited, "a launched process (attempt_count > 0) closes Exited, never Abandoned");
        execution.TerminalAt.ShouldNotBeNull();
    }

    /// <summary>Seed a parent workflow run (request + run) in the given TERMINAL status — WorkflowId null (no Workflow row needed; the FK is optional). Mirrors AgentRunOrphanSweepFlowTests' own helper of the same shape.</summary>
    private async Task<Guid> SeedTerminalWorkflowRunAsync(Guid teamId, WorkflowRunStatus status)
    {
        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Manual, ActorType = "user",
            ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}", Status = WorkflowRunRequestStatus.Consumed,
            ReceivedAt = DateTimeOffset.UtcNow, VerifiedAt = DateTimeOffset.UtcNow, NormalizedAt = DateTimeOffset.UtcNow,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Manual,
            Status = status, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return runId;
    }

    /// <summary>Plant an unanswered agent-grain decision (an AwaitingApproval <c>decision.request</c> ledger row) for a run — what the completion contract checks at recovery.</summary>
    private async Task<Guid> SeedPendingDecisionAsync(Guid teamId, Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var id = Guid.NewGuid();
        db.ToolCallLedger.Add(new ToolCallLedger
        {
            Id = id, TeamId = teamId, AgentRunId = runId, ToolKind = DecisionToolKinds.DecisionRequest,
            IdempotencyKey = $"decision.request:{id:N}", InputHash = new string('0', 64), Status = ToolCallLedgerStatus.AwaitingApproval,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>Seed a stale (20-min) Running run carrying a durable handle that points at a spool dir with an optional exit marker — the post-crash state the reconciler probes. <paramref name="launchHost"/> stamps the handle as some OTHER host's (null ⇒ unstamped, the pre-stamp shape); <paramref name="deadline"/> overrides the run's wall clock (default: an hour out); <paramref name="fenceEpoch"/> defaults to the unclaimed 0, and must be positive for a caller that also opens a native-record attempt against this run.</summary>
    private async Task<Guid> SeedDurableRunAsync(Guid teamId, int processId, int? exitCode, string? launchHost = null, DateTimeOffset? deadline = null, long fenceEpoch = 0)
    {
        var spoolDir = Path.Combine(Path.GetTempPath(), "cs-recover-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(spoolDir);
        _spoolDirs.Add(spoolDir);

        if (exitCode.HasValue) await File.WriteAllTextAsync(Path.Combine(spoolDir, "exit"), exitCode.Value.ToString());

        var handle = new SandboxHandle { Kind = "local", ProcessId = processId, LaunchHost = launchHost, SpoolDirectory = spoolDir, Deadline = deadline ?? DateTimeOffset.UtcNow.AddHours(1) };

        var runId = Guid.NewGuid();
        var stamp = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.AgentRun.Add(new AgentRun
        {
            Id = runId, TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.Running, FenceEpoch = fenceEpoch,
            StartedAt = stamp, HeartbeatAt = stamp, LeaseExpiresAt = stamp + AgentRunLiveness.Window,   // lease = last heartbeat + window (lapsed, since stamp is 20min old)
            RunnerHandleJson = JsonSerializer.Serialize(handle, AgentJson.Options),
        });
        await db.SaveChangesAsync();
        return runId;
    }

    /// <summary>
    /// Launch a REAL long-lived durable run via the local runner (a sleeping process under the supervisor), then
    /// seed a stale Running row pointing at its handle with the given re-attach attempt count — the post-crash
    /// state of an alive-but-unattachable run. Returns the run id + the supervisor pid so the test can assert the
    /// reconciler kills it. The process is tracked for best-effort teardown.
    /// </summary>
    private async Task<(Guid RunId, int Pid)> SeedAliveDurableRunAsync(Guid teamId, int reattachAttempts, long fenceEpoch = 0)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "cs-kill-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        _spoolDirs.Add(workDir);

        var spec = new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", "sleep 300" }, WorkingDirectory = workDir, TimeoutSeconds = 300 };

        SandboxHandle handle;
        using (var scope = _fixture.BeginScope())
            handle = await ((ISandboxDurableRunner)scope.Resolve<ISandboxRunnerRegistry>().Resolve(LocalProcessRunner.LocalKind))
                .LaunchAsync(spec, Guid.NewGuid().ToString("N"), CancellationToken.None);

        _spoolDirs.Add(handle.SpoolDirectory);
        _launchedPids.Add(handle.ProcessId);

        var runId = Guid.NewGuid();
        var stamp = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.AgentRun.Add(new AgentRun
            {
                Id = runId, TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.Running, FenceEpoch = fenceEpoch,
                StartedAt = stamp, HeartbeatAt = stamp, LeaseExpiresAt = stamp + AgentRunLiveness.Window,
                ReattachAttempts = reattachAttempts,
                RunnerHandleJson = JsonSerializer.Serialize(handle, AgentJson.Options),
            });
            await db.SaveChangesAsync();
        }

        return (runId, handle.ProcessId);
    }

    private static bool ProcessAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    private static async Task<bool> WaitForProcessGoneAsync(int pid)
    {
        for (var i = 0; i < 50 && ProcessAlive(pid); i++) await Task.Delay(100);
        return !ProcessAlive(pid);
    }

    /// <summary>A pid guaranteed dead: start a trivial process, let it exit, return its (now-reaped) pid.</summary>
    private static int DeadPid()
    {
        using var p = Process.Start(new ProcessStartInfo { FileName = "/bin/sh", ArgumentList = { "-c", "exit 0" }, UseShellExecute = false })!;
        p.WaitForExit();
        return p.Id;
    }

    public void Dispose()
    {
        foreach (var pid in _launchedPids)
            try { using var p = Process.GetProcessById(pid); if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best-effort: the test's kill already reaped it */ }

        foreach (var dir in _spoolDirs)
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    private async Task<Guid> SeedRunAsync(Guid teamId, AgentRunStatus status, TimeSpan livenessAgo, bool withRecentEvent, long fenceEpoch = 0, Guid? workflowRunId = null)
    {
        var runId = Guid.NewGuid();
        var stamp = DateTimeOffset.UtcNow - livenessAgo;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        // Lease = last heartbeat + the window, so the reconciler's lease-gate reproduces the heartbeat behaviour:
        // a 20-min-old stamp → lapsed lease (reclaimable); a 10s-old stamp → still-valid lease (left alone).
        db.AgentRun.Add(new AgentRun { Id = runId, TeamId = teamId, Harness = "codex-cli", Status = status, FenceEpoch = fenceEpoch, StartedAt = stamp, HeartbeatAt = stamp, LeaseExpiresAt = stamp + AgentRunLiveness.Window, WorkflowRunId = workflowRunId });

        if (withRecentEvent)
            db.AgentRunEvent.Add(new AgentRunEvent { Id = Guid.NewGuid(), AgentRunId = runId, Kind = AgentEventKind.CommandExecuted, Text = "still working" });

        await db.SaveChangesAsync();
        return runId;
    }

    /// <summary>
    /// Open a REAL native-record capture against a manually-seeded run — mints the Running execution + attempt a
    /// launch would, via the production plane, rather than hand-building rows that would have to satisfy 0137's
    /// triggers by guesswork. <paramref name="fenceEpoch"/> must match the run's own seeded <c>FenceEpoch</c>, which
    /// 0137's attempt-insert guard requires to equal the run's CURRENT fence.
    /// </summary>
    private async Task<NativeRecordCaptureHandle> SeedOpenAttemptAsync(Guid teamId, Guid runId, long fenceEpoch)
    {
        using var scope = _fixture.BeginScope();
        var plane = scope.Resolve<INativeRecordPlane>();

        return (await plane.OpenAsync(new NativeRecordCaptureRequest
        {
            TeamId = teamId, AgentRunId = runId, HarnessTypeKey = "codex-cli/v1", RunnerKind = "local",
            RunnerLocatorJson = "{}", WorkerFenceEpoch = fenceEpoch, Channel = NativeRecordChannel.Stdout,
        }, CancellationToken.None).ConfigureAwait(false)).ShouldNotBeNull("the plane must open a capture against a freshly seeded Running run");
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"agent-{userId:N}@test.local", Name = $"agent-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"agent-{teamId:N}", Name = "Agent Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }
}
