using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentRunOwnershipFlowTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Reclaim_cannot_replace_a_current_database_lease()
    {
        var runId = await CreateRunningAsync();
        using var setup = fixture.BeginScope();
        var db = setup.Resolve<CodeSpaceDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = clock_timestamp() + interval '1 hour' WHERE id = {runId}");
        var before = await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        using var contender = fixture.BeginScope();
        (await contender.Resolve<IAgentRunService>().ReclaimForReattachAsync(runId, CancellationToken.None)).ShouldBeFalse();
        var after = await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        after.FenceEpoch.ShouldBe(before.FenceEpoch);
        after.LeaseExpiresAt.ShouldBe(before.LeaseExpiresAt);
        after.ReattachAttempts.ShouldBe(before.ReattachAttempts);
    }

    [Fact]
    public async Task Eight_independent_workers_can_reclaim_an_expired_lease_only_once()
    {
        var runId = await CreateRunningAsync();
        using var setup = fixture.BeginScope();
        var db = setup.Resolve<CodeSpaceDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = clock_timestamp() - interval '1 hour', heartbeat_at = clock_timestamp() - interval '1 hour' WHERE id = {runId}");
        var before = await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contenders = Enumerable.Range(0, 8).Select(async _ =>
        {
            using var scope = fixture.BeginScope();
            var service = scope.Resolve<IAgentRunService>();
            await barrier.Task;
            return await service.ReclaimForReattachAsync(runId, CancellationToken.None);
        }).ToArray();
        barrier.SetResult();
        var results = await Task.WhenAll(contenders);
        results.Count(won => won).ShouldBe(1, "status=Running alone is not a reclaim CAS; the first winner must exclude every other worker");
        var after = await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        after.FenceEpoch.ShouldBe(before.FenceEpoch + 1);
        after.ReattachAttempts.ShouldBe(before.ReattachAttempts + 1);
        after.LeaseExpiresAt.ShouldNotBeNull().ShouldBeGreaterThan(after.HeartbeatAt.ShouldNotBeNull());
    }

    [Fact]
    public async Task Heartbeats_cannot_reanimate_terminal_leases()
    {
        var runId = await CreateRunningAsync();
        using var setup = fixture.BeginScope();
        var db = setup.Resolve<CodeSpaceDbContext>();
        await db.AgentRun.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, AgentRunStatus.Cancelled));
        var before = await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        using var stale = fixture.BeginScope();
        await stale.Resolve<IAgentRunService>().HeartbeatAsync(runId, CancellationToken.None);
        var after = await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        after.Status.ShouldBe(AgentRunStatus.Cancelled);
        after.HeartbeatAt.ShouldBe(before.HeartbeatAt);
        after.LeaseExpiresAt.ShouldBe(before.LeaseExpiresAt);
    }

    private async Task<Guid> CreateRunningAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture, inProcessPool: false);
        using var scope = fixture.BeginScopeAs(userId, teamId: teamId);
        var service = scope.Resolve<IAgentRunService>();
        var run = await service.CreateAsync(new AgentTask { Goal = "Verify ownership", Harness = "codex-cli", Model = "gpt-5.3-codex" }, teamId, null, null, cancellationToken: CancellationToken.None);
        await service.MarkRunningAsync(run.Id, CancellationToken.None);
        return run.Id;
    }
}
