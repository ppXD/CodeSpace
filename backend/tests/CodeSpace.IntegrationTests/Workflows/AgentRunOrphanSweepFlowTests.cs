using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The orphaned-Queued-agent-run leak backstop (the durability audit's P1 finding). An <c>agent.code</c> /
/// supervisor suspension commits the Queued <c>agent_run</c> (CreateAsync) but can crash BEFORE its
/// <c>workflow_run_wait</c> commits — leaving a Queued run NO wait references. None of the existing sweeps see it
/// (ReconcilePendingWaits inspects only wait-referenced runs; SweepStaleRunning is Running-only), so it would sit
/// Queued forever, permanently counted against the <see cref="AdmissionController"/> in-flight cap. These tests
/// prove <c>SweepQueuedUnderTerminalParentAsync</c> collects exactly that orphan — and ONLY that orphan: a
/// still-Queued run under a LIVE parent (a healthy just-staged run, or a supervisor crash-orphan the next spawn
/// reclaims) is left alone, a wait-referenced run is left to the existing path, and a standalone run is untouched.
///
/// <para>Integration tier (real Postgres): the leak is a state-split invariant — the split is seeded directly
/// (a Queued run + a terminal parent + no wait) rather than by racing a real crash, which is the rigorous,
/// deterministic way to pin a recovery invariant. No model is exercised, so there is no real-model E2E surface.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentRunOrphanSweepFlowTests
{
    private readonly PostgresFixture _fixture;

    public AgentRunOrphanSweepFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Theory]
    [InlineData(WorkflowRunStatus.Failure)]
    [InlineData(WorkflowRunStatus.Cancelled)]
    [InlineData(WorkflowRunStatus.Success)]
    public async Task Queued_orphan_under_a_terminal_parent_with_no_wait_is_cancelled_and_frees_the_admission_slot(WorkflowRunStatus parentStatus)
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var parentRunId = await SeedWorkflowRunAsync(teamId, parentStatus);
        var orphanId = await SeedQueuedAgentRunAsync(teamId, parentRunId);

        (await CountInflightAsync(teamId)).ShouldBe(1, "the orphan counts against the in-flight admission cap before the sweep");

        AgentRunReconcileSummary summary;
        using (var scope = _fixture.BeginScope())
            summary = await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

        summary.CancelledQueuedUnderTerminalParent.ShouldBeGreaterThanOrEqualTo(1);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var run = await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == orphanId);
        run.Status.ShouldBe(AgentRunStatus.Cancelled, "the orphan under a terminal parent will never launch — cancel it");
        run.Error.ShouldNotBeNull();
        run.CompletedAt.ShouldNotBeNull();

        (await db.AgentRunEvent.AsNoTracking().AnyAsync(e => e.AgentRunId == orphanId && e.Kind == AgentEventKind.Error))
            .ShouldBeTrue("the reconciler appends an Error event so the timeline shows the cancellation");

        (await CountInflightAsync(teamId)).ShouldBe(0, "cancelling the orphan frees the admission slot it permanently leaked");
    }

    [Theory]
    [InlineData(WorkflowRunStatus.Suspended)]
    [InlineData(WorkflowRunStatus.Running)]
    [InlineData(WorkflowRunStatus.Pending)]
    public async Task Queued_run_under_a_live_parent_is_left_alone(WorkflowRunStatus parentStatus)
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var parentRunId = await SeedWorkflowRunAsync(teamId, parentStatus);
        var runId = await SeedQueuedAgentRunAsync(teamId, parentRunId);

        AgentRunReconcileSummary summary;
        using (var scope = _fixture.BeginScope())
            summary = await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

        summary.CancelledQueuedUnderTerminalParent.ShouldBe(0, "a live parent's Queued run may be healthy or a supervisor orphan to reclaim — never collected");

        using var verify = _fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(AgentRunStatus.Queued, "the run under a still-live parent is left untouched");
    }

    [Fact]
    public async Task Wait_referenced_queued_run_under_a_terminal_parent_is_not_counted_by_the_orphan_sweep()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var parentRunId = await SeedWorkflowRunAsync(teamId, WorkflowRunStatus.Failure);
        var runId = await SeedQueuedAgentRunAsync(teamId, parentRunId);
        await SeedPendingAgentRunWaitAsync(parentRunId, runId);

        AgentRunReconcileSummary summary;
        using (var scope = _fixture.BeginScope())
            summary = await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

        summary.CancelledQueuedUnderTerminalParent.ShouldBe(0, "a wait-referenced run is the existing ReconcilePendingWaits case — the orphan sweep must exclude it, no double-collect");

        using var verify = _fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(AgentRunStatus.Cancelled, "it is still collected — by the existing wait-referenced path, not the orphan sweep");
    }

    [Fact]
    public async Task Standalone_queued_run_with_no_parent_run_is_left_alone()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedQueuedAgentRunAsync(teamId, workflowRunId: null);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

        using var verify = _fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(AgentRunStatus.Queued, "a standalone run has no parent — the WorkflowRunId != null guard skips it");
    }

    /// <summary>Count the team's in-flight (Queued + Running) agent runs — the exact set the admission cap counts.</summary>
    private async Task<int> CountInflightAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking()
            .CountAsync(r => r.TeamId == teamId && (r.Status == AgentRunStatus.Queued || r.Status == AgentRunStatus.Running));
    }

    /// <summary>Seed a parent workflow run (request + run) in the given status — WorkflowId null (no Workflow row needed; the FK is optional).</summary>
    private async Task<Guid> SeedWorkflowRunAsync(Guid teamId, WorkflowRunStatus status)
    {
        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Manual, ActorType = "user",
            ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}", Status = WorkflowRunRequestStatus.Consumed,
            ReceivedAt = DateTimeOffset.UtcNow, VerifiedAt = DateTimeOffset.UtcNow, NormalizedAt = DateTimeOffset.UtcNow,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Manual,
            Status = status, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return runId;
    }

    /// <summary>Seed a Queued agent run linked to the given parent (or standalone when null) — the staged-but-uncommitted-wait state.</summary>
    private async Task<Guid> SeedQueuedAgentRunAsync(Guid teamId, Guid? workflowRunId)
    {
        var runId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.AgentRun.Add(new AgentRun
        {
            Id = runId, TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.Queued,
            WorkflowRunId = workflowRunId, NodeId = workflowRunId == null ? null : "agent", IterationKey = "",
        });

        await db.SaveChangesAsync();
        return runId;
    }

    /// <summary>Seed the pending AgentRun wait the suspension would have committed — Token = the agent-run id, RunId = the parent workflow run.</summary>
    private async Task SeedPendingAgentRunWaitAsync(Guid parentRunId, Guid agentRunId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = Guid.NewGuid(), RunId = parentRunId, NodeId = "agent", IterationKey = "",
            WaitKind = WorkflowWaitKinds.AgentRun, Token = agentRunId.ToString(),
            Status = WorkflowWaitStatuses.Pending, CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
    }
}
