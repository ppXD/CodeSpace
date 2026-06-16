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
/// High-fidelity chaos test (Rule 12): a REAL child process is started, its output streamed + persisted
/// live through the real AgentRunService, then the process is KILLED mid-run (worker token cancelled —
/// exactly what happens when a pod is torn down before the run completes). It then asserts the three
/// things that make the system trustworthy under a crash:
///   1. the events emitted before the crash are durably persisted, in order;
///   2. the log is immutable even after the crash (the append-only trigger rejects tampering);
///   3. the reconciler recovers the orphaned run to Failed — and the pre-crash log survives recovery.
/// Real process + real Postgres + real services resolved through CodeSpaceModule (no mocks).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentRunChaosRecoveryTests
{
    private readonly PostgresFixture _fixture;

    public AgentRunChaosRecoveryTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Worker_killed_mid_stream_keeps_a_durable_immutable_log_and_is_recovered()
    {
        if (OperatingSystem.IsWindows()) return;   // shell-based real process (Rule 12.1)

        var teamId = await SeedTeamAsync();

        Guid runId;
        using (var scope = _fixture.BeginScope())
            runId = (await scope.Resolve<IAgentRunService>().CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None);

        // A REAL child process emits 3 lines then sleeps. Each line is persisted live; after the 3rd we
        // cancel the worker token — which KILLS the real process mid-run and throws, like a pod torn down
        // before CompleteAsync ever runs.
        using var workerToken = new CancellationTokenSource();
        var persisted = 0;
        var spec = new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", "printf 'step1\\nstep2\\nstep3\\n'; sleep 5" }, TimeoutSeconds = 60 };

        using (var scope = _fixture.BeginScope())
        {
            var runner = (ISandboxStreamRunner)scope.Resolve<ISandboxRunnerRegistry>().Resolve(LocalProcessRunner.LocalKind);
            var svc = scope.Resolve<IAgentRunService>();

            await Should.ThrowAsync<OperationCanceledException>(() => runner.RunStreamingAsync(spec, async (line, ct) =>
            {
                await svc.AppendEventAsync(runId, new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = line }, ct);
                if (++persisted >= 3) workerToken.Cancel();
            }, workerToken.Token));
        }

        // The worker died without CompleteAsync: the run is still Running and the 3 pre-crash events are durable + ordered.
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            (await svc.GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Running);
            (await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None)).Select(e => e.Text).ShouldBe(new[] { "step1", "step2", "step3" });
        }

        // Log integrity survives the crash: the durable ledger rejects tampering.
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var evt = await db.AgentRunEvent.FirstAsync(e => e.AgentRunId == runId);
            evt.Text = "tampered";
            await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        // Recover. The reconciler reclaims a Running run whose LEASE has lapsed AND whose event window is quiet.
        // The claim stamped the lease (now + the default window); the window override empties the event-recency
        // check but doesn't touch the already-stamped lease, so lapse the lease directly — simulating the dead
        // worker no longer renewing it — then sweep.
        var original = System.Environment.GetEnvironmentVariable(AgentRunReconcilerService.LivenessWindowEnvVar);
        try
        {
            System.Environment.SetEnvironmentVariable(AgentRunReconcilerService.LivenessWindowEnvVar, "00:00:00");

            using (var lapse = _fixture.BeginScope())
                await lapse.Resolve<CodeSpaceDbContext>().Database
                    .ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = {DateTimeOffset.UtcNow.AddMinutes(-1)} WHERE id = {runId}");

            using var scope = _fixture.BeginScope();
            var marked = (await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None)).MarkedAbandonedFromRunning;
            marked.ShouldBeGreaterThanOrEqualTo(1);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(AgentRunReconcilerService.LivenessWindowEnvVar, original);
        }

        // Recovered to Failed; the pre-crash log SURVIVED recovery intact + in order; a recovery event was appended.
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            var run = await svc.GetAsync(runId, CancellationToken.None);
            run.Status.ShouldBe(AgentRunStatus.Failed);
            run.Error.ShouldNotBeNull();

            var events = await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None);
            events.Select(e => e.Text).Take(3).ShouldBe(new[] { "step1", "step2", "step3" });
            events.ShouldContain(e => e.Kind == AgentEventKind.Error);
        }
    }

    private static AgentTask BuildTask() => new() { Goal = "chaos", Harness = "codex-cli", Model = "gpt-5.3-codex" };

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
