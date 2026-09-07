using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Constants;
using CodeSpace.Core.Middlewares.Transactional;
using CodeSpace.Core.Services.Tasks;
using CodeSpace.Core.Services.Tasks.Launch.Providers.Chat;
using CodeSpace.Core.Services.Tasks.RoutePreview;
using CodeSpace.Core.Services.Tasks.RoutePreview.Exceptions;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Tasks;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TaskRouteSnapshotFlowTests
{
    private readonly PostgresFixture _fixture;

    public TaskRouteSnapshotFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_creator_scope_with_a_tracked_preview_reads_another_workers_committed_consumption()
    {
        var input = await InputAsync();
        using var creator = _fixture.BeginScope();
        var preview = await creator.Resolve<ITaskRoutePreviewService>().PreviewAsync(input, CancellationToken.None);
        input = input with { RouteSnapshotId = preview.RouteSnapshotId };
        using var worker = _fixture.BeginScope();
        var original = await worker.Resolve<ITaskLaunchService>().LaunchAsync(input, CancellationToken.None);
        var seed = await new ChatSeedProvider().SeedAsync(input, CancellationToken.None);
        var repeated = await creator.Resolve<ITaskRouteSnapshotService>().ConsumeAsync(new TaskRouteSnapshotConsumption(input, seed, () => throw new InvalidOperationException("A stale tracked preview tried to launch twice.")), CancellationToken.None);
        repeated.RunId.ShouldBe(original.RunId);
        (await creator.Resolve<CodeSpaceDbContext>().WorkflowRun.CountAsync(r => r.TeamId == input.TeamId)).ShouldBe(1);
    }

    [Fact]
    public async Task Cancellation_rolls_back_the_row_lock_without_consuming_the_reference()
    {
        var input = await InputAsync();
        using var previewScope = _fixture.BeginScope();
        var preview = await previewScope.Resolve<ITaskRoutePreviewService>().PreviewAsync(input, CancellationToken.None);
        input = input with { RouteSnapshotId = preview.RouteSnapshotId };
        var seed = await new ChatSeedProvider().SeedAsync(input, CancellationToken.None);
        using (var cancelled = _fixture.BeginScope())
            await Should.ThrowAsync<OperationCanceledException>(() => cancelled.Resolve<ITaskRouteSnapshotService>().ConsumeAsync(new TaskRouteSnapshotConsumption(input, seed, () => throw new OperationCanceledException()), CancellationToken.None));
        using var retry = _fixture.BeginScope();
        var launched = await retry.Resolve<ITaskLaunchService>().LaunchAsync(input, CancellationToken.None);
        launched.RunId.ShouldNotBe(Guid.Empty);
        (await retry.Resolve<CodeSpaceDbContext>().TaskRouteSnapshot.AsNoTracking().SingleAsync(s => s.Id == preview.RouteSnapshotId)).ConsumedRunId.ShouldBe(launched.RunId);
    }

    [Fact]
    public async Task Normalized_source_drift_is_rejected_even_if_the_wire_input_did_not_change()
    {
        var input = await InputAsync();
        using var scope = _fixture.BeginScope();
        var preview = await scope.Resolve<ITaskRoutePreviewService>().PreviewAsync(input, CancellationToken.None);
        input = input with { RouteSnapshotId = preview.RouteSnapshotId };
        var seed = await new ChatSeedProvider().SeedAsync(input, CancellationToken.None);
        await Should.ThrowAsync<TaskRouteSnapshotMismatchException>(() => scope.Resolve<ITaskRouteSnapshotService>().ReadAsync(input, seed with { GroundingContext = "The source document has changed." }, CancellationToken.None));
    }

    [Fact]
    public async Task Exact_retries_remain_idempotent_after_expiry_but_changed_intent_still_fails()
    {
        var input = await InputAsync();
        using var scope = _fixture.BeginScope();
        var preview = await scope.Resolve<ITaskRoutePreviewService>().PreviewAsync(input, CancellationToken.None);
        input = input with { RouteSnapshotId = preview.RouteSnapshotId };
        var original = await scope.Resolve<ITaskLaunchService>().LaunchAsync(input, CancellationToken.None);
        await scope.Resolve<CodeSpaceDbContext>().TaskRouteSnapshot.Where(s => s.Id == preview.RouteSnapshotId).ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAt, DateTimeOffset.UtcNow.AddDays(-1)).SetProperty(r => r.PolicyFingerprint, "old-policy"));
        using var retry = _fixture.BeginScope();
        (await retry.Resolve<ITaskLaunchService>().LaunchAsync(input, CancellationToken.None)).RunId.ShouldBe(original.RunId);
        await Should.ThrowAsync<TaskRouteSnapshotMismatchException>(() => retry.Resolve<ITaskLaunchService>().LaunchAsync(input with { Autonomy = "Trusted" }, CancellationToken.None));
        (await retry.Resolve<CodeSpaceDbContext>().WorkflowRun.CountAsync(r => r.TeamId == input.TeamId)).ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Same_scope_failure_restores_caller_work_and_discards_only_rolled_back_dispatch(bool ambient)
    {
        var input = await InputAsync();
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var preview = await scope.Resolve<ITaskRoutePreviewService>().PreviewAsync(input, CancellationToken.None);
        input = input with { RouteSnapshotId = preview.RouteSnapshotId };
        var seed = await new ChatSeedProvider().SeedAsync(input, CancellationToken.None);
        var postCommit = scope.Resolve<IPostCommitActions>();
        var observed = new List<string>();
        await using var transaction = ambient ? await db.Database.BeginTransactionAsync() : null;
        if (ambient) await postCommit.RunAfterCommitAsync(_ => { observed.Add("caller"); return Task.CompletedTask; }, CancellationToken.None);
        var team = await db.Team.SingleAsync(t => t.Id == input.TeamId);
        team.Name = "caller pending edit";
        var pendingUser = new User { Id = Guid.NewGuid(), Email = $"pending-{Guid.NewGuid():N}@test.local", Name = "caller pending insert", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };
        db.User.Add(pendingUser);
        Guid failedRunId = default;

        await Should.ThrowAsync<IOException>(() => scope.Resolve<ITaskRouteSnapshotService>().ConsumeAsync(new TaskRouteSnapshotConsumption(input, seed, async () =>
        {
            failedRunId = (await scope.Resolve<ITaskLaunchService>().LaunchAsync(input with { RouteSnapshotId = null }, CancellationToken.None)).RunId;
            await postCommit.RunAfterCommitAsync(_ => { observed.Add("rolled-back"); return Task.CompletedTask; }, CancellationToken.None);
            throw new IOException("fault after the real run and caller edit were flushed");
        }), CancellationToken.None));

        team.Name.ShouldBe("caller pending edit");
        db.Entry(team).State.ShouldBe(EntityState.Modified, "rollback must restore the caller's uncommitted edit, not clear or mark it saved");
        db.Entry(pendingUser).State.ShouldBe(EntityState.Added, "caller inserts must remain pending after the nested rollback");
        if (transaction is not null)
        {
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            await transaction.DisposeAsync();
            await postCommit.RunAllAsync(CancellationToken.None);
            observed.ShouldBe(["caller"], "catching a failed consumption and committing the caller transaction must not dispatch the rolled-back launch");
        }
        using (var beforeRetry = _fixture.BeginScope())
            (await beforeRetry.Resolve<CodeSpaceDbContext>().WorkflowRun.CountAsync(r => r.TeamId == input.TeamId)).ShouldBe(0);

        var retry = await scope.Resolve<ITaskLaunchService>().LaunchAsync(input, CancellationToken.None);
        retry.RunId.ShouldNotBe(failedRunId);
        observed.ShouldNotContain("rolled-back");
        using var read = _fixture.BeginScope();
        (await read.Resolve<CodeSpaceDbContext>().WorkflowRun.CountAsync(r => r.TeamId == input.TeamId)).ShouldBe(1);
        (await read.Resolve<CodeSpaceDbContext>().Team.SingleAsync(t => t.Id == input.TeamId)).Name.ShouldBe("caller pending edit");
        (await read.Resolve<CodeSpaceDbContext>().User.SingleAsync(u => u.Id == pendingUser.Id)).Name.ShouldBe("caller pending insert");
    }

    [Fact]
    public async Task Expiry_uses_the_database_clock_after_waiting_for_the_consumption_lock()
    {
        var input = await InputAsync();
        using var previewScope = _fixture.BeginScope();
        var preview = await previewScope.Resolve<ITaskRoutePreviewService>().PreviewAsync(input, CancellationToken.None);
        input = input with { RouteSnapshotId = preview.RouteSnapshotId };
        var seed = await new ChatSeedProvider().SeedAsync(input, CancellationToken.None);
        using var holderScope = _fixture.BeginScope();
        using var consumerScope = _fixture.BeginScope();
        var holder = holderScope.Resolve<CodeSpaceDbContext>();
        var consumer = consumerScope.Resolve<CodeSpaceDbContext>();
        await consumer.Database.OpenConnectionAsync();
        var consumerPid = await consumer.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        await using var lockTransaction = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync($"UPDATE task_route_snapshot SET expires_at = clock_timestamp() + interval '10 minutes' WHERE id = {preview.RouteSnapshotId}");
        var invoked = false;
        var consume = consumerScope.Resolve<ITaskRouteSnapshotService>().ConsumeAsync(new TaskRouteSnapshotConsumption(input, seed, () => { invoked = true; throw new InvalidOperationException("expired stage was invoked"); }), CancellationToken.None);
        var locked = false;
        for (var attempt = 0; attempt < 100 && !locked; attempt++)
        {
            locked = await holder.Database.SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid = {consumerPid} AND wait_event_type = 'Lock') AS \"Value\"").SingleAsync();
            if (!locked) await Task.Delay(10);
        }
        locked.ShouldBeTrue("the consumption must be waiting on the snapshot row before the deadline advances");
        await holder.Database.ExecuteSqlInterpolatedAsync($"UPDATE task_route_snapshot SET expires_at = clock_timestamp() + interval '100 milliseconds' WHERE id = {preview.RouteSnapshotId}");
        await holder.Database.ExecuteSqlRawAsync("SELECT pg_sleep(0.2)");
        await lockTransaction.CommitAsync();
        await Should.ThrowAsync<TaskRouteSnapshotMismatchException>(() => consume);
        invoked.ShouldBeFalse("expiry is checked with DB time after acquiring the lock, never with an earlier worker timestamp");
    }

    private async Task<TaskLaunchRequest> InputAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var scope = _fixture.BeginScope();
        scope.Resolve<InMemoryBackgroundJobClient>().AutoExecute = false;
        return new TaskLaunchRequest { TeamId = teamId, ActorUserId = userId, SurfaceKind = "chat", TaskText = "Explain the preview consumption contract", RequestedEffort = "quick", Autonomy = "Confined" };
    }
}
