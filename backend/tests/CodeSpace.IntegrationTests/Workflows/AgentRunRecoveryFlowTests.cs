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
public class AgentRunRecoveryFlowTests
{
    private readonly PostgresFixture _fixture;

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

    private async Task<Guid> SeedRunAsync(Guid teamId, AgentRunStatus status, TimeSpan livenessAgo, bool withRecentEvent)
    {
        var runId = Guid.NewGuid();
        var stamp = DateTimeOffset.UtcNow - livenessAgo;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.AgentRun.Add(new AgentRun { Id = runId, TeamId = teamId, Harness = "codex-cli", Status = status, StartedAt = stamp, HeartbeatAt = stamp });

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
