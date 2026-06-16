using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The contract the executor's heartbeat loop exists to satisfy: a Running agent run whose heartbeat is
/// fresh is NOT abandoned by the reconciler even when it has emitted ZERO events (a long quiet step — the
/// agent thinking, a silent compile/test). Complements <see cref="AgentRunChaosRecoveryTests"/>, which
/// proves the opposite end (no heartbeat + no events past the window IS abandoned). Real Postgres + real
/// services resolved through CodeSpaceModule (Rule 12). Asserts on THIS run's status (not a global count)
/// so it's robust to other runs sharing the collection's database.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentRunHeartbeatLivenessTests
{
    private readonly PostgresFixture _fixture;

    public AgentRunHeartbeatLivenessTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_running_run_with_a_fresh_heartbeat_and_no_events_is_not_abandoned()
    {
        // A TIGHT window (not the 5-min default) so survival is unambiguously due to the heartbeat being
        // FRESH within the window — a run whose heartbeat had gone stale past this window IS abandoned
        // (that negative end is pinned by AgentRunChaosRecoveryTests at window 0). Restored in finally.
        var original = Environment.GetEnvironmentVariable(AgentRunLiveness.WindowEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(AgentRunLiveness.WindowEnvVar, "00:00:30");

            var teamId = await SeedTeamAsync();

            Guid runId;
            using (var scope = _fixture.BeginScope())
                runId = (await scope.Resolve<IAgentRunService>().CreateAsync(
                    new AgentTask { Goal = "quiet", Harness = "codex-cli", Model = "test-model" }, teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;

            // MarkRunningAsync stamps a fresh HeartbeatAt — exactly what the executor's heartbeat loop keeps
            // refreshed. We append NO events, so the heartbeat is the ONLY liveness signal protecting this run.
            using (var scope = _fixture.BeginScope())
                await scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None);

            using (var scope = _fixture.BeginScope())
                await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

            using (var scope = _fixture.BeginScope())
                (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Running);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentRunLiveness.WindowEnvVar, original);
        }
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
