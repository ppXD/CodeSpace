using System.Diagnostics;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
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

    /// <summary>Seed a stale (20-min) Running run carrying a durable handle that points at a spool dir with an optional exit marker — the post-crash state the reconciler probes.</summary>
    private async Task<Guid> SeedDurableRunAsync(Guid teamId, int processId, int? exitCode)
    {
        var spoolDir = Path.Combine(Path.GetTempPath(), "cs-recover-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(spoolDir);
        _spoolDirs.Add(spoolDir);

        if (exitCode.HasValue) await File.WriteAllTextAsync(Path.Combine(spoolDir, "exit"), exitCode.Value.ToString());

        var handle = new SandboxHandle { Kind = "local", ProcessId = processId, SpoolDirectory = spoolDir, Deadline = DateTimeOffset.UtcNow.AddHours(1) };

        var runId = Guid.NewGuid();
        var stamp = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.AgentRun.Add(new AgentRun
        {
            Id = runId, TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.Running,
            StartedAt = stamp, HeartbeatAt = stamp, LeaseExpiresAt = stamp + AgentRunLiveness.Window,   // lease = last heartbeat + window (lapsed, since stamp is 20min old)
            RunnerHandleJson = JsonSerializer.Serialize(handle, AgentJson.Options),
        });
        await db.SaveChangesAsync();
        return runId;
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
        foreach (var dir in _spoolDirs)
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    private async Task<Guid> SeedRunAsync(Guid teamId, AgentRunStatus status, TimeSpan livenessAgo, bool withRecentEvent)
    {
        var runId = Guid.NewGuid();
        var stamp = DateTimeOffset.UtcNow - livenessAgo;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        // Lease = last heartbeat + the window, so the reconciler's lease-gate reproduces the heartbeat behaviour:
        // a 20-min-old stamp → lapsed lease (reclaimable); a 10s-old stamp → still-valid lease (left alone).
        db.AgentRun.Add(new AgentRun { Id = runId, TeamId = teamId, Harness = "codex-cli", Status = status, StartedAt = stamp, HeartbeatAt = stamp, LeaseExpiresAt = stamp + AgentRunLiveness.Window });

        if (withRecentEvent)
            db.AgentRunEvent.Add(new AgentRunEvent { Id = Guid.NewGuid(), AgentRunId = runId, Kind = AgentEventKind.CommandExecuted, Text = "still working" });

        await db.SaveChangesAsync();
        return runId;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"agent-{userId:N}@test.local", Name = $"agent-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"agent-{teamId:N}", Name = "Agent Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }
}
