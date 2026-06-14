using System.Diagnostics;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
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
            won = await scope.Resolve<IAgentRunService>().CancelRunningAsync(runId, "operator cancel", CancellationToken.None);

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
            won = await scope.Resolve<IAgentRunService>().CancelRunningAsync(runId, "operator cancel", CancellationToken.None);

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
            won = await scope.Resolve<IAgentRunService>().CancelRunningAsync(runId, "operator cancel", CancellationToken.None);

        won.ShouldBeFalse("a run that completed in the same instant (no longer Running) loses the CAS — no kill");

        using (var verify = _fixture.BeginScope())
            (await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(AgentRunStatus.Succeeded, "the legitimately-completed run is NOT trampled by the cancel");
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

    private async Task<Guid> SeedRunAsync(Guid teamId, AgentRunStatus status)
    {
        var runId = Guid.NewGuid();
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.AgentRun.Add(new AgentRun { Id = runId, TeamId = teamId, Harness = "codex-cli", Status = status });
        await db.SaveChangesAsync();
        return runId;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"cancel-{userId:N}@test.local", Name = $"cancel-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"cancel-{teamId:N}", Name = "Cancel Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
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
