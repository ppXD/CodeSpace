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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lease_time_is_sampled_after_the_database_row_lock_is_acquired(bool reclaim)
    {
        var runId = await CreateRunningAsync();
        using var holderScope = fixture.BeginScope();
        using var consumerScope = fixture.BeginScope();
        var holder = holderScope.Resolve<CodeSpaceDbContext>();
        var consumer = consumerScope.Resolve<CodeSpaceDbContext>();
        if (reclaim) await holder.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = clock_timestamp() - interval '1 hour' WHERE id = {runId}");
        await consumer.Database.OpenConnectionAsync();
        var consumerPid = await consumer.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        await using var transaction = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM agent_run WHERE id = {runId} FOR UPDATE");
        var service = consumerScope.Resolve<IAgentRunService>();
        Task operation = reclaim ? service.ReclaimForReattachAsync(runId, CancellationToken.None) : service.HeartbeatAsync(runId, CancellationToken.None);
        var locked = false;
        for (var attempt = 0; attempt < 100 && !locked; attempt++)
        {
            locked = await holder.Database.SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid = {consumerPid} AND wait_event_type = 'Lock') AS \"Value\"").SingleAsync();
            if (!locked) await Task.Delay(10);
        }
        locked.ShouldBeTrue("the lease operation must be blocked before the release timestamp is sampled");
        var releasingAt = await holder.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync();
        await transaction.CommitAsync();
        await operation;
        var actual = await holder.AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        actual.HeartbeatAt.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(releasingAt, "a database clock expression evaluated before waiting is still a stale lease timestamp");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_null_lease_uses_the_latest_persisted_liveness_signal(bool expired)
    {
        var runId = await CreateRunningAsync();
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var age = expired ? TimeSpan.FromHours(1) : TimeSpan.Zero;
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = NULL, heartbeat_at = clock_timestamp() - {age}, started_at = clock_timestamp() - interval '2 hours' WHERE id = {runId}");
        var before = await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        (await scope.Resolve<IAgentRunService>().ReclaimForReattachAsync(runId, CancellationToken.None)).ShouldBe(expired);
        var after = await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        after.FenceEpoch.ShouldBe(before.FenceEpoch + (expired ? 1 : 0));
        after.ReattachAttempts.ShouldBe(before.ReattachAttempts + (expired ? 1 : 0));
        if (!expired) after.LeaseExpiresAt.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_waiting_reclaimer_rechecks_a_newly_renewed_or_terminal_row(bool terminal)
    {
        var runId = await CreateRunningAsync();
        using var holderScope = fixture.BeginScope();
        using var contenderScope = fixture.BeginScope();
        var holder = holderScope.Resolve<CodeSpaceDbContext>();
        var contender = contenderScope.Resolve<CodeSpaceDbContext>();
        await holder.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = clock_timestamp() - interval '1 hour' WHERE id = {runId}");
        await contender.Database.OpenConnectionAsync();
        var pid = await contender.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        await using var transaction = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM agent_run WHERE id = {runId} FOR UPDATE");
        var operation = contenderScope.Resolve<IAgentRunService>().ReclaimForReattachAsync(runId, CancellationToken.None);
        var locked = false;
        for (var attempt = 0; attempt < 100 && !locked; attempt++)
        {
            locked = await holder.Database.SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid = {pid} AND wait_event_type = 'Lock') AS \"Value\"").SingleAsync();
            if (!locked) await Task.Delay(10);
        }
        locked.ShouldBeTrue();
        if (terminal)
            await holder.AgentRun.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, AgentRunStatus.Cancelled));
        else
            await holder.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = clock_timestamp() + interval '1 hour' WHERE id = {runId}");
        var before = await holder.AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        await transaction.CommitAsync();
        (await operation).ShouldBeFalse("a candidate selected before the lock wait cannot overwrite the winner's current row");
        var after = await holder.AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        after.Status.ShouldBe(before.Status);
        after.LeaseExpiresAt.ShouldBe(before.LeaseExpiresAt);
        after.FenceEpoch.ShouldBe(before.FenceEpoch);
        after.ReattachAttempts.ShouldBe(before.ReattachAttempts);
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
