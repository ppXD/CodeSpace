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
/// Drives the REAL AgentRunService (resolved through CodeSpaceModule's DI, proving it's registered)
/// against real Postgres across a full lifecycle — create → running → append events → complete, then
/// reads back run + events via the live cursor — plus the guards: an illegal transition and a
/// non-terminal completion status each throw, and re-running an already-running run is rejected.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentRunServiceTests
{
    private readonly PostgresFixture _fixture;

    public AgentRunServiceTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Full_lifecycle_create_run_append_events_complete()
    {
        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var run = await scope.Resolve<IAgentRunService>().CreateAsync(BuildTask(), teamId, null, null, CancellationToken.None);
            run.Status.ShouldBe(AgentRunStatus.Queued);
            runId = run.Id;
        }

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            await svc.AppendEventAsync(runId, new AgentEvent { Kind = AgentEventKind.CommandExecuted, Text = "npm test", Data = JsonSerializer.SerializeToElement(new { command = "npm test", exitCode = 0 }) }, CancellationToken.None);
            await svc.AppendEventAsync(runId, new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = "Fixed the failing tests." }, CancellationToken.None);
        }

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().CompleteAsync(runId, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = "Fixed.", ChangedFiles = new[] { "src/a.ts" } }, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();

            var run = await svc.GetAsync(runId, CancellationToken.None);
            run.Status.ShouldBe(AgentRunStatus.Succeeded);
            run.StartedAt.ShouldNotBeNull();
            run.CompletedAt.ShouldNotBeNull();
            run.ResultJson.ShouldNotBeNull();
            run.ResultJson!.ShouldContain("completed");

            var events = await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None);
            events.Select(e => e.Kind).ShouldBe(new[] { AgentEventKind.CommandExecuted, AgentEventKind.AssistantMessage });
            events[0].DataJson.ShouldNotBeNull();
            events[0].DataJson!.ShouldContain("npm test");

            // cursor: events strictly after the first
            var tail = await svc.GetEventsAsync(runId, teamId, events[0].Sequence, CancellationToken.None);
            tail.Select(e => e.Kind).ShouldBe(new[] { AgentEventKind.AssistantMessage });
        }
    }

    [Fact]
    public async Task Reading_events_for_another_teams_run_returns_empty()
    {
        // The events read is team-scoped: a foreign run id leaks neither events nor the run's existence.
        var ownerTeam = await SeedTeamAsync();
        var otherTeam = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            var run = await svc.CreateAsync(BuildTask(), ownerTeam, null, null, CancellationToken.None);
            runId = run.Id;
            await svc.MarkRunningAsync(runId, CancellationToken.None);
            await svc.AppendEventAsync(runId, new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = "owner-only" }, CancellationToken.None);
        }

        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();

            (await svc.GetEventsAsync(runId, otherTeam, 0, CancellationToken.None)).ShouldBeEmpty("a foreign team sees no events");
            (await svc.GetEventsAsync(runId, ownerTeam, 0, CancellationToken.None)).ShouldHaveSingleItem();
        }
    }

    [Fact]
    public async Task Completing_a_queued_run_is_illegal()
    {
        var teamId = await SeedTeamAsync();
        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IAgentRunService>();

        var run = await svc.CreateAsync(BuildTask(), teamId, null, null, CancellationToken.None);

        // Queued → Succeeded is illegal — a run can't succeed without running.
        await Should.ThrowAsync<AgentRunTransitionException>(() =>
            svc.CompleteAsync(run.Id, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" }, CancellationToken.None));
    }

    [Fact]
    public async Task Completing_with_a_nonterminal_status_is_rejected()
    {
        var teamId = await SeedTeamAsync();
        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IAgentRunService>();

        var run = await svc.CreateAsync(BuildTask(), teamId, null, null, CancellationToken.None);
        await svc.MarkRunningAsync(run.Id, CancellationToken.None);

        await Should.ThrowAsync<AgentRunTransitionException>(() =>
            svc.CompleteAsync(run.Id, new AgentRunResult { Status = AgentRunStatus.Running, ExitReason = "still going" }, CancellationToken.None));
    }

    [Fact]
    public async Task Re_running_an_already_running_run_is_rejected()
    {
        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var run = await scope.Resolve<IAgentRunService>().CreateAsync(BuildTask(), teamId, null, null, CancellationToken.None);
            runId = run.Id;
        }

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
            await Should.ThrowAsync<AgentRunTransitionException>(() =>
                scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None));
    }

    [Fact]
    public async Task Claim_returns_the_bumped_fence_epoch_and_completion_under_it_succeeds()
    {
        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
            runId = (await scope.Resolve<IAgentRunService>().CreateAsync(BuildTask(), teamId, null, null, CancellationToken.None)).Id;

        long epoch;
        using (var scope = _fixture.BeginScope())
            epoch = await scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None);

        epoch.ShouldBe(1, "the claim bumps the fence epoch from its 0 default");

        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            await svc.CompleteAsync(runId, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" }, epoch, CancellationToken.None);
            (await svc.GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Succeeded);
        }
    }

    [Fact]
    public async Task Completing_under_a_stale_epoch_is_fenced_out()
    {
        // Simulates a reclaim: the run's epoch is bumped (a lease-expiry reclaim / restart re-claim) AFTER this
        // worker claimed it, so the original worker's epoch-fenced completion must lose — no double-completion.
        var teamId = await SeedTeamAsync();

        Guid runId;
        long claimedEpoch;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, CancellationToken.None)).Id;
            claimedEpoch = await svc.MarkRunningAsync(runId, CancellationToken.None);
        }

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<CodeSpaceDbContext>().Database
                .ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET fence_epoch = fence_epoch + 1 WHERE id = {runId}");

        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();

            await Should.ThrowAsync<AgentRunTransitionException>(() =>
                svc.CompleteAsync(runId, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" }, claimedEpoch, CancellationToken.None));

            // The run was NOT completed — it stays Running for the reclaimer to finish (no double-completion).
            (await svc.GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Running);
        }
    }

    [Fact]
    public async Task Claim_stamps_a_lease_and_heartbeat_renews_it()
    {
        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
            runId = (await scope.Resolve<IAgentRunService>().CreateAsync(BuildTask(), teamId, null, null, CancellationToken.None)).Id;

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None);

        DateTimeOffset claimedLease;
        using (var scope = _fixture.BeginScope())
        {
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            run.LeaseExpiresAt.ShouldNotBeNull("the claim stamps a lease");
            claimedLease = run.LeaseExpiresAt!.Value;
            claimedLease.ShouldBeGreaterThan(DateTimeOffset.UtcNow, "the lease is in the future (now + window)");
        }

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().HeartbeatAsync(runId, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            run.LeaseExpiresAt!.Value.ShouldBeGreaterThanOrEqualTo(claimedLease, "the heartbeat pushes the lease forward");
        }
    }

    [Fact]
    public async Task Reclaim_for_reattach_bumps_the_epoch_and_re_leases_a_running_run()
    {
        var teamId = await SeedTeamAsync();

        Guid runId;
        long claimedEpoch;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, CancellationToken.None)).Id;
            claimedEpoch = await svc.MarkRunningAsync(runId, CancellationToken.None);
        }

        // Lapse the lease (the claiming worker stopped renewing it — it died) so the reclaim mirrors the real path.
        using (var scope = _fixture.BeginScope())
            await scope.Resolve<CodeSpaceDbContext>().Database
                .ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = {DateTimeOffset.UtcNow.AddMinutes(-1)} WHERE id = {runId}");

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IAgentRunService>().ReclaimForReattachAsync(runId, CancellationToken.None))
                .ShouldBeTrue("reclaiming a Running run wins the CAS");

        using (var scope = _fixture.BeginScope())
        {
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            run.Status.ShouldBe(AgentRunStatus.Running, "the reclaim keeps the run Running — it's re-claimed, not completed");
            run.FenceEpoch.ShouldBe(claimedEpoch + 1, "the reclaim bumps the fence epoch so a revived original observer is fenced out");
            run.LeaseExpiresAt!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow, "the reclaim re-leases into the future so the run drops out of the stale sweep");
        }
    }

    [Fact]
    public async Task Reclaim_for_reattach_is_a_noop_on_a_terminal_run()
    {
        var teamId = await SeedTeamAsync();

        Guid runId;
        long epoch;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, CancellationToken.None)).Id;
            epoch = await svc.MarkRunningAsync(runId, CancellationToken.None);
            await svc.CompleteAsync(runId, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" }, epoch, CancellationToken.None);
        }

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IAgentRunService>().ReclaimForReattachAsync(runId, CancellationToken.None))
                .ShouldBeFalse("a terminal run can't be reclaimed for re-attach");

        using (var scope = _fixture.BeginScope())
        {
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            run.Status.ShouldBe(AgentRunStatus.Succeeded);
            run.FenceEpoch.ShouldBe(epoch, "a lost reclaim leaves the epoch untouched");
        }
    }

    [Fact]
    public async Task Completing_under_the_pre_reclaim_epoch_is_fenced_out_after_a_reattach_reclaim()
    {
        // The double-completion fence: a reclaim bumps the epoch for the re-attaching worker, so the ORIGINAL
        // worker (if it revives and tries to complete under its old epoch) loses — no double-completion.
        var teamId = await SeedTeamAsync();

        Guid runId;
        long originalEpoch;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, CancellationToken.None)).Id;
            originalEpoch = await svc.MarkRunningAsync(runId, CancellationToken.None);
        }

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IAgentRunService>().ReclaimForReattachAsync(runId, CancellationToken.None)).ShouldBeTrue();

        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();

            await Should.ThrowAsync<AgentRunTransitionException>(() =>
                svc.CompleteAsync(runId, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" }, originalEpoch, CancellationToken.None));

            (await svc.GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Running, "the original worker lost the epoch-fenced CAS; the run stays Running for the re-attacher");
        }
    }

    [Fact]
    public async Task CreateAsync_rejects_the_run_that_would_breach_the_per_team_cap()
    {
        // The D4a chokepoint over real Postgres: with the per-team cap pinned to 2 (via the env override), seed
        // 2 in-flight runs for the team, then the 3rd CreateAsync must throw AgentRunAdmissionException —
        // fail-closed BEFORE the row is persisted.
        using var caps = WithCaps(perTeam: 2, global: 1000);

        var teamId = await SeedTeamAsync();

        await SeedInflightRunsAsync(teamId, queued: 1, running: 1);   // 2 in flight → AT the cap

        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IAgentRunService>();

        var ex = await Should.ThrowAsync<AgentRunAdmissionException>(() =>
            svc.CreateAsync(BuildTask(), teamId, null, null, CancellationToken.None));

        ex.Message.ShouldContain(AdmissionController.MaxInflightPerTeamEnvVar);   // names the env var to raise

        // The rejected run never touched the table — still exactly the 2 we seeded.
        (await CountInflightForTeamAsync(teamId)).ShouldBe(2, "the over-cap run was refused pre-persist, not inserted");
    }

    [Fact]
    public async Task CreateAsync_admits_a_run_while_under_the_per_team_cap()
    {
        using var caps = WithCaps(perTeam: 5, global: 1000);

        var teamId = await SeedTeamAsync();

        await SeedInflightRunsAsync(teamId, queued: 2, running: 1);   // 3 in flight, cap 5 → headroom

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(BuildTask(), teamId, null, null, CancellationToken.None);

        run.Status.ShouldBe(AgentRunStatus.Queued, "a sub-cap run is admitted + persisted Queued");
        (await CountInflightForTeamAsync(teamId)).ShouldBe(4);
    }

    [Fact]
    public async Task The_per_team_cap_isolates_teams_a_full_team_does_not_block_a_different_team()
    {
        // Per-team isolation: one team being AT its cap must not starve another team — the cap counts only the
        // requesting team's in-flight runs.
        using var caps = WithCaps(perTeam: 2, global: 1000);

        var fullTeam = await SeedTeamAsync();
        var otherTeam = await SeedTeamAsync();

        await SeedInflightRunsAsync(fullTeam, queued: 1, running: 1);   // fullTeam is AT its cap
        await SeedInflightRunsAsync(otherTeam, queued: 1, running: 0);  // otherTeam has plenty of headroom

        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IAgentRunService>();

        await Should.ThrowAsync<AgentRunAdmissionException>(() =>
            svc.CreateAsync(BuildTask(), fullTeam, null, null, CancellationToken.None));

        // The OTHER team, well under its own cap, is admitted normally despite the first team being full.
        var run = await svc.CreateAsync(BuildTask(), otherTeam, null, null, CancellationToken.None);
        run.Status.ShouldBe(AgentRunStatus.Queued, "a different team under its own cap is unaffected by a full team");
    }

    [Fact]
    public async Task The_global_cap_trips_across_teams_even_when_each_team_is_under_its_own_cap()
    {
        // The deployment-wide ceiling: spread the in-flight runs across two teams so NEITHER is at its per-team
        // cap, but TOGETHER they hit the global cap — the next create (for a third team well under its own cap)
        // is still refused by the global gate.
        using var caps = WithCaps(perTeam: 100, global: 3);

        var teamA = await SeedTeamAsync();
        var teamB = await SeedTeamAsync();
        var teamC = await SeedTeamAsync();

        await SeedInflightRunsAsync(teamA, queued: 1, running: 1);   // 2
        await SeedInflightRunsAsync(teamB, queued: 1, running: 0);   // +1 = 3 global → AT the global cap

        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IAgentRunService>();

        var ex = await Should.ThrowAsync<AgentRunAdmissionException>(() =>
            svc.CreateAsync(BuildTask(), teamC, null, null, CancellationToken.None));

        ex.Message.ShouldContain(AdmissionController.MaxInflightGlobalEnvVar, customMessage: "the global gate, not the per-team gate, is the binding limit here");
    }

    // ─── Admission helpers ──────────────────────────────────────────────────────

    /// <summary>Pin the in-flight caps for one test via the env overrides; restores the prior values on Dispose so the shared-process env stays isolated (mirrors AdmissionControllerTests).</summary>
    private static IDisposable WithCaps(int perTeam, int global) => new CapOverride(perTeam, global);

    private sealed class CapOverride : IDisposable
    {
        private readonly string? _perTeam = Environment.GetEnvironmentVariable(AdmissionController.MaxInflightPerTeamEnvVar);
        private readonly string? _global = Environment.GetEnvironmentVariable(AdmissionController.MaxInflightGlobalEnvVar);

        public CapOverride(int perTeam, int global)
        {
            Environment.SetEnvironmentVariable(AdmissionController.MaxInflightPerTeamEnvVar, perTeam.ToString());
            Environment.SetEnvironmentVariable(AdmissionController.MaxInflightGlobalEnvVar, global.ToString());
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(AdmissionController.MaxInflightPerTeamEnvVar, _perTeam);
            Environment.SetEnvironmentVariable(AdmissionController.MaxInflightGlobalEnvVar, _global);
        }
    }

    // Insert raw in-flight (Queued/Running) AgentRun rows directly — bypassing CreateAsync (the gate under test),
    // so the seed itself is never refused. The whole-test isolation comes from each team being a fresh GUID.
    private async Task SeedInflightRunsAsync(Guid teamId, int queued, int running)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        for (var i = 0; i < queued; i++)
            db.AgentRun.Add(new AgentRun { Id = Guid.NewGuid(), TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.Queued, TaskJson = "{}" });

        for (var i = 0; i < running; i++)
            db.AgentRun.Add(new AgentRun { Id = Guid.NewGuid(), TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.Running, TaskJson = "{}" });

        await db.SaveChangesAsync();
    }

    private async Task<int> CountInflightForTeamAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking()
            .CountAsync(r => r.TeamId == teamId && (r.Status == AgentRunStatus.Queued || r.Status == AgentRunStatus.Running));
    }

    private static AgentTask BuildTask(string goal = "Fix the failing billing tests") =>
        new() { Goal = goal, Harness = "codex-cli", Model = "gpt-5.3-codex" };

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
