using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Dispatch;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (high fidelity, Rule 12): the REAL <see cref="IWorkflowRunDispatcher"/> over real Postgres refuses to queue a
/// sub-workflow child whose ancestry has finished — the last door before a worker walks it, which every resume, revive and
/// reconciler re-dispatch passes through — while a rerun or replay, whose parent is only its lineage, still goes. The job
/// client records the enqueue without running the engine, so the dispatch itself is what is observed.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DispatcherFinishedAncestorFlowTests
{
    private readonly PostgresFixture _fixture;

    public DispatcherFinishedAncestorFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Theory]
    [InlineData(WorkflowRunStatus.Success)]
    [InlineData(WorkflowRunStatus.Failure)]
    [InlineData(WorkflowRunStatus.Cancelled)]
    public async Task A_child_of_a_finished_parent_is_not_queued(WorkflowRunStatus finished)
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var parentId = await SeedRunAsync(teamId, finished, null, WorkflowRunSourceTypes.Manual);   // WorkflowEngine terminal / WorkflowService.CancelRunAsync
        var childId = await SeedRunAsync(teamId, WorkflowRunStatus.Pending, parentId, WorkflowRunSourceTypes.ChildWorkflow);   // SubworkflowService.StageChildRunAsync
        var versionBefore = await RowVersionAsync(childId);

        (await DispatchAsync(childId)).ShouldBeFalse($"nothing waits for a child of a {finished} parent");

        (await RowVersionAsync(childId)).ShouldBe(versionBefore, "the refusal comes before the Pending → Enqueued CAS, so the row is not written");
    }

    [Fact]
    public async Task A_grandchild_is_not_queued_once_the_root_has_finished_though_its_parent_lives()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var rootId = await SeedRunAsync(teamId, WorkflowRunStatus.Failure, null, WorkflowRunSourceTypes.Manual);   // WorkflowEngine terminal
        var childId = await SeedRunAsync(teamId, WorkflowRunStatus.Suspended, rootId, WorkflowRunSourceTypes.ChildWorkflow);   // SubworkflowService.StageChildRunAsync, then its own park
        var grandchildId = await SeedRunAsync(teamId, WorkflowRunStatus.Pending, childId, WorkflowRunSourceTypes.ChildWorkflow);   // SubworkflowService.StageChildRunAsync

        (await DispatchAsync(grandchildId)).ShouldBeFalse("the root's finish ends every level under it");
        (await StatusAsync(grandchildId)).ShouldBe(WorkflowRunStatus.Pending);
    }

    [Fact]
    public async Task A_child_of_a_live_parent_is_queued()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var parentId = await SeedRunAsync(teamId, WorkflowRunStatus.Suspended, null, WorkflowRunSourceTypes.Manual);   // WorkflowEngine park
        var childId = await SeedRunAsync(teamId, WorkflowRunStatus.Pending, parentId, WorkflowRunSourceTypes.ChildWorkflow);   // SubworkflowService.StageChildRunAsync

        (await DispatchAsync(childId)).ShouldBeTrue();
        (await StatusAsync(childId)).ShouldBe(WorkflowRunStatus.Enqueued);
    }

    [Fact]
    public async Task A_child_of_a_live_rerun_fork_is_queued_though_the_forks_original_has_finished()
    {
        // The walk climbs only through sub-workflow children: the fork is where it stops, never the fork's lineage.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var originalId = await SeedRunAsync(teamId, WorkflowRunStatus.Failure, null, WorkflowRunSourceTypes.Manual);   // WorkflowEngine terminal
        var forkId = await SeedRunAsync(teamId, WorkflowRunStatus.Suspended, originalId, WorkflowRunSourceTypes.Rerun);   // WorkflowService.StageForkedRunAsync, then the engine park
        var childId = await SeedRunAsync(teamId, WorkflowRunStatus.Pending, forkId, WorkflowRunSourceTypes.ChildWorkflow);   // SubworkflowService.StageChildRunAsync

        (await DispatchAsync(childId)).ShouldBeTrue("the live fork waits for its child");
        (await StatusAsync(childId)).ShouldBe(WorkflowRunStatus.Enqueued);
    }

    [Theory]
    [InlineData(WorkflowRunSourceTypes.Rerun)]
    [InlineData(WorkflowRunSourceTypes.Replay)]
    public async Task A_fork_of_a_finished_run_is_queued(string forkSource)
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var originalId = await SeedRunAsync(teamId, WorkflowRunStatus.Failure, null, WorkflowRunSourceTypes.Manual);   // WorkflowEngine terminal
        var forkId = await SeedRunAsync(teamId, WorkflowRunStatus.Pending, originalId, forkSource);   // WorkflowService.StageForkedRunAsync

        (await DispatchAsync(forkId)).ShouldBeTrue($"a {forkSource}'s parent is its lineage, finished by definition — never a reason to hold it back");
        (await StatusAsync(forkId)).ShouldBe(WorkflowRunStatus.Enqueued);
    }

    [Fact]
    public async Task A_timer_firing_on_a_child_of_a_finished_parent_does_not_queue_it()
    {
        // The engine schedules a timer's wake with Hangfire when it parks, and nothing unschedules it when the parent ends.
        // The wake resolves the child's wait and flips it to Pending; the dispatch it then asks for is refused.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var parentId = await SeedRunAsync(teamId, WorkflowRunStatus.Failure, null, WorkflowRunSourceTypes.Manual);   // WorkflowEngine terminal
        var childId = await SeedRunAsync(teamId, WorkflowRunStatus.Suspended, parentId, WorkflowRunSourceTypes.ChildWorkflow);   // SubworkflowService.StageChildRunAsync, then its own park
        var timerWaitId = await SeedTimerWaitAsync(childId);   // WorkflowEngine.TryParkAsync

        using (var manual = JobClient().ManualExecution())
        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IWorkflowResumeService>().ResumeWaitAsync(childId, timerWaitId, null, CancellationToken.None)).ShouldBeTrue("precondition: the scheduled wake resolved the child's timer");

        (await StatusAsync(childId)).ShouldBe(WorkflowRunStatus.Pending, "the wake flipped the child, but the dispatcher would not queue it — no worker walks it");
    }

    private async Task<bool> DispatchAsync(Guid runId)
    {
        using var manual = JobClient().ManualExecution();   // record the enqueue; never run the engine behind it
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowRunDispatcher>().DispatchAsync(runId, CancellationToken.None);
    }

    private InMemoryBackgroundJobClient JobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    private async Task<Guid> SeedRunAsync(Guid teamId, WorkflowRunStatus status, Guid? parentRunId, string sourceType)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var now = DateTimeOffset.UtcNow;
        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        db.WorkflowRunRequest.Add(new WorkflowRunRequest { Id = requestId, TeamId = teamId, SourceType = sourceType, ActorType = "user", ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}", Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now });
        db.WorkflowRun.Add(new WorkflowRun { Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = sourceType, ParentRunId = parentRunId, Status = status, StartedAt = status == WorkflowRunStatus.Pending ? null : now, CompletedAt = WorkflowRunState.IsTerminal(status) ? now : null, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();
        return runId;
    }

    private async Task<Guid> SeedTimerWaitAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var waitId = Guid.NewGuid();
        db.WorkflowRunWait.Add(new WorkflowRunWait { Id = waitId, RunId = runId, NodeId = "delay", IterationKey = string.Empty, WaitKind = WorkflowWaitKinds.Timer, Token = Guid.NewGuid().ToString("N"), WakeAt = DateTimeOffset.UtcNow, Status = WorkflowWaitStatuses.Pending, CreatedAt = DateTimeOffset.UtcNow });

        await db.SaveChangesAsync();
        return waitId;
    }

    private async Task<WorkflowRunStatus> StatusAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().Where(r => r.Id == runId).Select(r => r.Status).SingleAsync();
    }

    private async Task<uint> RowVersionAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().Where(r => r.Id == runId).Select(r => r.Xmin).SingleAsync();
    }
}
