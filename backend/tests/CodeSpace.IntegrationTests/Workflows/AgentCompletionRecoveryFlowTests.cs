using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentCompletionRecoveryFlowTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly List<Guid> _waitIds = [];

    [Theory]
    [InlineData("running")]
    [InlineData("fresh-queued")]
    [InlineData("malformed")]
    public async Task Ineligible_waits_cannot_hide_a_durably_completed_agent_beyond_the_first_batch(string blocker)
    {
        var parent = await SeedParentAsync();
        for (var index = 0; index < AgentRunReconcilerService.BatchSize; index++)
            await SeedWaitAsync(parent, blocker, index);
        var completed = await SeedWaitAsync(parent, "terminal", AgentRunReconcilerService.BatchSize);

        using (var recovery = fixture.BeginScope())
            await recovery.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

        using var verify = fixture.BeginScope();
        var wait = await verify.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().SingleAsync(w => w.Id == completed.WaitId);
        wait.Status.ShouldBe(WorkflowWaitStatuses.Resolved, "eligibility must be applied before the bounded global batch; healthy or malformed waits cannot hide a completed run indefinitely");
        wait.PayloadJson.ShouldNotBeNull().ShouldContain("Succeeded");
    }

    [Fact]
    public async Task A_failed_notification_is_not_counted_as_resumed_and_cannot_monopolize_the_next_worker_sweep()
    {
        var parent = await SeedParentAsync();
        var poisoned = new HashSet<Guid>();
        for (var index = 0; index < AgentRunReconcilerService.BatchSize; index++)
            poisoned.Add((await SeedWaitAsync(parent, "terminal", index)).AgentId);
        var completed = await SeedWaitAsync(parent, "terminal", AgentRunReconcilerService.BatchSize);

        using (var firstWorker = BeginRecoveryScope(poisoned))
        {
            var first = await firstWorker.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);
            first.ResumedStalledParents.ShouldBe(0, "a best-effort notifier returning normally is not a persisted wait acknowledgement");
        }
        using (var secondWorker = BeginRecoveryScope(poisoned))
            await secondWorker.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

        using var verify = fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.Id == completed.WaitId)).Status.ShouldBe(WorkflowWaitStatuses.Resolved,
            "retry ordering must survive a fresh worker scope; fifty failed notifications cannot starve the next completed run");
        (await db.WorkflowRunWait.CountAsync(w => w.RunId == parent.RunId && w.Status == WorkflowWaitStatuses.Pending)).ShouldBe(AgentRunReconcilerService.BatchSize);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_token_for_an_agent_from_another_parent_or_team_is_never_selected(bool foreignTeam)
    {
        var parent = await SeedParentAsync();
        var completed = await SeedWaitAsync(parent, "terminal", 0);
        var other = await SeedParentAsync();
        using (var mutate = fixture.BeginScope())
        {
            var db = mutate.Resolve<CodeSpaceDbContext>();
            if (foreignTeam) await db.AgentRun.Where(a => a.Id == completed.AgentId).ExecuteUpdateAsync(s => s.SetProperty(a => a.TeamId, other.TeamId));
            else await db.WorkflowRunWait.Where(w => w.Id == completed.WaitId).ExecuteUpdateAsync(s => s.SetProperty(w => w.RunId, other.RunId));
        }
        using (var worker = fixture.BeginScope())
            (await worker.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None)).ResumedStalledParents.ShouldBe(0);
        using var verify = fixture.BeginScope();
        var wait = await verify.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().SingleAsync(w => w.Id == completed.WaitId);
        wait.Status.ShouldBe(WorkflowWaitStatuses.Pending);
        wait.LastAgentRecoveryAttemptAt.ShouldBeNull("foreign correlation is ineligible, not a failed callback to keep retrying");
    }

    [Fact]
    public async Task A_wait_locked_by_another_worker_does_not_block_an_independent_completion()
    {
        var parent = await SeedParentAsync();
        var locked = await SeedWaitAsync(parent, "terminal", 0);
        var free = await SeedWaitAsync(parent, "terminal", 1);
        using var holder = fixture.BeginScope();
        var holderDb = holder.Resolve<CodeSpaceDbContext>();
        await using var transaction = await holderDb.Database.BeginTransactionAsync();
        await holderDb.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM workflow_run_wait WHERE id = {locked.WaitId} FOR UPDATE");
        using (var worker = fixture.BeginScope())
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await worker.Resolve<IAgentRunReconcilerService>().ReconcileAsync(deadline.Token);
            result.ResumedStalledParents.ShouldBe(1);
        }
        (await holderDb.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.Id == locked.WaitId)).LastAgentRecoveryAttemptAt.ShouldBeNull();
        (await holderDb.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.Id == free.WaitId)).Status.ShouldBe(WorkflowWaitStatuses.Resolved);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task A_worker_lost_after_selection_leaves_a_durable_retry_and_never_a_false_acknowledgement()
    {
        var parent = await SeedParentAsync();
        await SeedWaitAsync(parent, "running", 0);
        var completed = await SeedWaitAsync(parent, "terminal", 1);
        using (var lostWorker = BeginRecoveryScope([completed.AgentId]))
            (await lostWorker.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None)).ResumedStalledParents.ShouldBe(0);
        using (var persisted = fixture.BeginScope())
        {
            var db = persisted.Resolve<CodeSpaceDbContext>();
            var wait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.Id == completed.WaitId);
            wait.Status.ShouldBe(WorkflowWaitStatuses.Pending);
            wait.LastAgentRecoveryAttemptAt.ShouldNotBeNull();
            // Advance the persisted retry age, without sleeping or manipulating the process clock.
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE workflow_run_wait SET last_agent_recovery_attempt_at = clock_timestamp() - interval '1 minute' WHERE id = {completed.WaitId}");
        }
        using (var newWorker = fixture.BeginScope())
            (await newWorker.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None)).ResumedStalledParents.ShouldBe(1);
        using var verify = fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().SingleAsync(w => w.Id == completed.WaitId)).Status.ShouldBe(WorkflowWaitStatuses.Resolved);
    }

    private ILifetimeScope BeginRecoveryScope(HashSet<Guid> poisoned) => fixture.BeginScope(builder => builder.Register(context => new SelectiveNotifier(
        new WorkflowResumeAgentRunCompletionNotifier(context.Resolve<CodeSpaceDbContext>(), context.Resolve<IWorkflowResumeService>(), NullLogger<WorkflowResumeAgentRunCompletionNotifier>.Instance), poisoned)).As<IAgentRunCompletionNotifier>());

    private async Task<Parent> SeedParentAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        db.WorkflowRunRequest.Add(new WorkflowRunRequest { Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Manual, ActorType = WorkflowRunActorTypes.User, ActorId = userId, NormalizedPayloadJson = "{}", Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = DateTimeOffset.UtcNow });
        db.WorkflowRun.Add(new WorkflowRun { Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Manual, Status = WorkflowRunStatus.Suspended, CreatedBy = userId, LastModifiedBy = userId });
        await db.SaveChangesAsync();
        return new Parent(runId, teamId);
    }

    private async Task<WaitingAgent> SeedWaitAsync(Parent parent, string kind, int index)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var agentId = Guid.NewGuid();
        var waitId = Guid.NewGuid();
        var status = kind == "terminal" ? AgentRunStatus.Succeeded : kind == "fresh-queued" ? AgentRunStatus.Queued : AgentRunStatus.Running;
        if (kind != "malformed")
            db.AgentRun.Add(new AgentRun { Id = agentId, TeamId = parent.TeamId, WorkflowRunId = parent.RunId, NodeId = "agent", IterationKey = index.ToString(), Harness = "codex-cli", Status = status, HeartbeatAt = DateTimeOffset.UtcNow, LeaseExpiresAt = DateTimeOffset.UtcNow.AddHours(1), CompletedAt = status == AgentRunStatus.Succeeded ? DateTimeOffset.UtcNow : null });
        db.WorkflowRunWait.Add(new WorkflowRunWait { Id = waitId, RunId = parent.RunId, NodeId = "agent", IterationKey = index.ToString(), WaitKind = WorkflowWaitKinds.AgentRun, Token = kind == "malformed" ? "not-an-agent-id-" + index : agentId.ToString(), Status = WorkflowWaitStatuses.Pending, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5).AddSeconds(index) });
        await db.SaveChangesAsync();
        _waitIds.Add(waitId);
        return new WaitingAgent(agentId, waitId);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        using var scope = fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.Where(w => _waitIds.Contains(w.Id)).ExecuteDeleteAsync();
    }

    private sealed record Parent(Guid RunId, Guid TeamId);
    private sealed record WaitingAgent(Guid AgentId, Guid WaitId);
    private sealed class SelectiveNotifier(IAgentRunCompletionNotifier inner, HashSet<Guid> poisoned) : IAgentRunCompletionNotifier
    {
        public Task NotifyCompletedAsync(Guid agentRunId, CancellationToken cancellationToken) => poisoned.Contains(agentRunId) ? Task.CompletedTask : inner.NotifyCompletedAsync(agentRunId, cancellationToken);
    }
}
