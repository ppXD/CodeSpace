using System.Diagnostics;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Capture;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The running-agent kill half of PR-D4b, driving the REAL AgentRunService against real Postgres. Pins
/// CancelRunningAsync's CAS + terminate shape directly (the WorkflowService kill-wave composes this): a
/// Running run → Cancelled with the orphan process TERMINATED; a non-Running run is a no-op no-kill; an
/// epoch-mismatch (the run was reclaimed since the cancel observed it) loses the CAS → no-op no-kill.
/// Mirrors AgentRunRecoveryFlowTests' real-durable-process setup (🟢 high fidelity for the kill).
///
/// <para>Also pins the sibling defect to AgentRunRecoveryFlowTests' reconciler closer tests: a cancel that wins
/// the CAS must ALSO close its own open <c>WorkflowRunHarnessProcessAttempt</c>/<c>WorkflowRunHarnessExecution</c>
/// rows (stamped <see cref="AgentRunAbandonCause.OperatorCancelled"/>), not just flip the Agent Run — otherwise a
/// cancelled run still reads as a live process to every reader of the native record plane.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentRunCancelRunningTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly List<string> _spoolDirs = new();
    private readonly List<int> _launchedPids = new();

    public AgentRunCancelRunningTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Running_run_with_a_durable_handle_is_cancelled_and_its_process_is_terminated()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var (runId, pid) = await SeedRunningDurableRunAsync(teamId);

        ProcessAlive(pid).ShouldBeTrue("precondition: the durable agent process is running before the cancel");

        bool won;
        using (var scope = _fixture.BeginScope())
            won = await scope.Resolve<IAgentRunService>().CancelRunningAsync(runId, "operator cancel", AgentRunAbandonCause.OperatorCancelled, CancellationToken.None);

        won.ShouldBeTrue("CancelRunningAsync won the Running → Cancelled CAS");

        using (var verify = _fixture.BeginScope())
        {
            var run = await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);
            run.Status.ShouldBe(AgentRunStatus.Cancelled, "a deliberate cancel lands Cancelled, NOT Failed");
            run.Error.ShouldBe("operator cancel");
            run.CompletedAt.ShouldNotBeNull();
        }

        (await WaitForProcessGoneAsync(pid)).ShouldBeTrue("the won CAS must TerminateAsync the orphan process tree");
    }

    [Theory]
    [InlineData(AgentRunStatus.Queued)]
    [InlineData(AgentRunStatus.Succeeded)]
    [InlineData(AgentRunStatus.Failed)]
    [InlineData(AgentRunStatus.Cancelled)]
    public async Task A_non_running_run_is_a_no_op(AgentRunStatus status)
    {
        var teamId = await SeedTeamAsync();
        var runId = await SeedRunAsync(teamId, status);

        bool won;
        using (var scope = _fixture.BeginScope())
            won = await scope.Resolve<IAgentRunService>().CancelRunningAsync(runId, "operator cancel", AgentRunAbandonCause.OperatorCancelled, CancellationToken.None);

        won.ShouldBeFalse("CancelRunningAsync only flips a Running run; everything else loses the CAS");

        using (var verify = _fixture.BeginScope())
            (await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(status, "a non-Running run is left exactly as it was");
    }

    [Fact]
    public async Task A_run_that_completed_in_the_same_instant_loses_the_cas_and_is_not_killed()
    {
        if (OperatingSystem.IsWindows()) return;

        // THE FENCE the epoch + status guard protects: the run was Running with a live process, but a worker
        // legitimately landed it terminal (Running → Succeeded) just before the cancel's flip. The cancel must LOSE
        // the status-guarded, epoch-fenced CAS (the run is no longer Running) → no flip, and crucially NO kill of a
        // process that belongs to a run that finished cleanly. A lost CAS = no kill is the safety invariant.
        var teamId = await SeedTeamAsync();
        var (runId, pid) = await SeedRunningDurableRunAsync(teamId);
        _launchedPids.Add(pid);   // the cancel must NOT kill it; we reap it ourselves in Dispose

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().CompleteAsync(runId, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" }, CancellationToken.None);

        bool won;
        using (var scope = _fixture.BeginScope())
            won = await scope.Resolve<IAgentRunService>().CancelRunningAsync(runId, "operator cancel", AgentRunAbandonCause.OperatorCancelled, CancellationToken.None);

        won.ShouldBeFalse("a run that completed in the same instant (no longer Running) loses the CAS — no kill");

        using (var verify = _fixture.BeginScope())
            (await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(AgentRunStatus.Succeeded, "the legitimately-completed run is NOT trampled by the cancel");
    }

    // The defect these two tests pin — the sibling of AgentRunRecoveryFlowTests' reconciler closer tests: a won
    // cancel CAS flips the Agent Run but never touched its own open WorkflowRunHarnessProcessAttempt /
    // WorkflowRunHarnessExecution rows, so a phantom "live" process survived forever in every reader of the native
    // record plane even after the run itself was Cancelled.

    [Fact]
    public async Task Running_run_with_an_open_native_attempt_has_it_closed_as_operator_cancelled()
    {
        var teamId = await SeedTeamAsync();
        var runId = await SeedRunAsync(teamId, AgentRunStatus.Running, fenceEpoch: 1);
        var handle = await SeedOpenAttemptAsync(teamId, runId, fenceEpoch: 1);

        bool won;
        using (var scope = _fixture.BeginScope())
            won = await scope.Resolve<IAgentRunService>().CancelRunningAsync(runId, "operator cancel", AgentRunAbandonCause.OperatorCancelled, CancellationToken.None);

        won.ShouldBeTrue();

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(AgentRunStatus.Cancelled, "the cancel's own terminal decision must not change");

        var attempt = await db.WorkflowRunHarnessProcessAttempt.AsNoTracking().SingleAsync(a => a.Id == handle.AttemptId);
        attempt.State.ShouldBe(HarnessProcessAttemptState.Lost, "a won cancel must close the open attempt row it leaves behind, not leave it Running forever");
        attempt.ExitCode.ShouldBeNull();
        attempt.ErrorCode.ShouldBe(NativeRecordPlane.CancelOperatorCancelledErrorCode);

        var execution = await db.WorkflowRunHarnessExecution.AsNoTracking().SingleAsync(e => e.Id == handle.ExecutionId);
        execution.State.ShouldBe(HarnessExecutionState.Exited, "a launched process (attempt_count > 0) closes Exited, never Abandoned");
        execution.TerminalAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_native_attempt_an_operator_cancel_closed_cannot_be_reopened_by_a_late_close()
    {
        var teamId = await SeedTeamAsync();
        var runId = await SeedRunAsync(teamId, AgentRunStatus.Running, fenceEpoch: 1);
        var handle = await SeedOpenAttemptAsync(teamId, runId, fenceEpoch: 1);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().CancelRunningAsync(runId, "operator cancel", AgentRunAbandonCause.OperatorCancelled, CancellationToken.None);

        // A late writer: the ORIGINAL worker's own executor, unaware its run was already cancelled, still reaches
        // its ordinary happy-path close for the same attempt.
        using (var lateScope = _fixture.BeginScope())
            await lateScope.Resolve<INativeRecordPlane>().CloseAsync(handle, exitCode: 0, CancellationToken.None);

        using var verify = _fixture.BeginScope();
        var attempt = await verify.Resolve<CodeSpaceDbContext>().WorkflowRunHarnessProcessAttempt.AsNoTracking().SingleAsync(a => a.Id == handle.AttemptId);

        attempt.State.ShouldBe(HarnessProcessAttemptState.Lost, "the cancel's close already landed; a late writer's happy-path close must not reopen or overwrite it");
        attempt.ExitCode.ShouldBeNull("a late writer's exit code must never overwrite an already-terminal row");
        attempt.ErrorCode.ShouldBe(NativeRecordPlane.CancelOperatorCancelledErrorCode);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Launch a REAL sleeper under the local durable runner, then seed a Running AgentRun carrying its handle — the live post-launch state CancelRunningAsync targets. Returns the run id + supervisor pid.</summary>
    private async Task<(Guid RunId, int Pid)> SeedRunningDurableRunAsync(Guid teamId)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "cs-cancelrunning-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        _spoolDirs.Add(workDir);

        var spec = new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", "sleep 300" }, WorkingDirectory = workDir, TimeoutSeconds = 300 };

        SandboxHandle handle;
        using (var scope = _fixture.BeginScope())
            handle = await ((ISandboxDurableRunner)scope.Resolve<ISandboxRunnerRegistry>().Resolve(LocalProcessRunner.LocalKind))
                .LaunchAsync(spec, Guid.NewGuid().ToString("N"), CancellationToken.None);

        _spoolDirs.Add(handle.SpoolDirectory);

        var runId = Guid.NewGuid();
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.AgentRun.Add(new AgentRun
            {
                Id = runId, TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.Running,
                StartedAt = DateTimeOffset.UtcNow, HeartbeatAt = DateTimeOffset.UtcNow, FenceEpoch = 1,
                RunnerHandleJson = JsonSerializer.Serialize(handle, AgentJson.Options),
            });
            await db.SaveChangesAsync();
        }

        return (runId, handle.ProcessId);
    }

    private async Task<Guid> SeedRunAsync(Guid teamId, AgentRunStatus status, long fenceEpoch = 0)
    {
        var runId = Guid.NewGuid();
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.AgentRun.Add(new AgentRun { Id = runId, TeamId = teamId, Harness = "codex-cli", Status = status, FenceEpoch = fenceEpoch });
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
        db.User.Add(new User { Id = userId, Email = $"cancel-{userId:N}@test.local", Name = $"cancel-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"cancel-{teamId:N}", Name = "Cancel Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
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

    public void Dispose()
    {
        foreach (var pid in _launchedPids)
            try { using var p = Process.GetProcessById(pid); if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best-effort */ }

        foreach (var dir in _spoolDirs)
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }
}
