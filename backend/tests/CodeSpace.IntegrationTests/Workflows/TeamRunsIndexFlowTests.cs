using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Pins <see cref="IWorkflowService.ListTeamRunsAsync"/> — the team runs index backing the Runs page:
///   1. Team-scoped: only the asking team's runs (never another team's).
///   2. Nested-execution excluded: a flow.subworkflow child (SourceType `workflow.child`) is filtered out.
///   3. Forks included: a replay / rerun run carries a ParentRunId (lineage) but IS a top-level run — it stays.
///   4. Source-agnostic: a task / snapshot run (null WorkflowId) is included — TeamId is on the run directly.
///   5. Newest-first, capped at the requested limit.
/// Real DB (the query is the whole behaviour), so this is the integration tier.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class TeamRunsIndexFlowTests
{
    private readonly PostgresFixture _fixture;

    public TeamRunsIndexFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Lists_teams_top_level_runs_newest_first_keeping_forks_dropping_children()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var t = DateTimeOffset.UtcNow;
        // Task / snapshot runs (null WorkflowId) — the index must include them, and they avoid the request's
        // workflow_id FK. The query has no WorkflowId predicate, so this also covers the authored-run path.
        var older = await InsertRunAsync(teamA, parentRunId: null, createdDate: t.AddMinutes(-10), workflowId: null);
        // A replay fork: it carries a ParentRunId (lineage to `older`) but is a top-level run the user launched — kept.
        var fork = await InsertRunAsync(teamA, parentRunId: older, createdDate: t.AddMinutes(-5), workflowId: null, sourceType: WorkflowRunSourceTypes.Replay);
        var newer = await InsertRunAsync(teamA, parentRunId: null, createdDate: t, workflowId: null);
        // A sub-workflow child: runs inside its parent's Run Room — excluded.
        await InsertRunAsync(teamA, parentRunId: older, createdDate: t.AddMinutes(-1), workflowId: null, sourceType: WorkflowRunSourceTypes.ChildWorkflow);
        await InsertRunAsync(teamB, parentRunId: null, createdDate: t, workflowId: null);   // other team — excluded

        var result = await ListAsync(teamA, 50);

        result.Select(r => r.Id).ShouldBe(new[] { newer, fork, older });   // newest-first; child + other-team filtered out, fork kept
        result[0].WorkflowId.ShouldBeNull();                                // a task run (null WorkflowId) is in the index
    }

    [Fact]
    public async Task Caps_at_the_requested_limit()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var t = DateTimeOffset.UtcNow;
        await InsertRunAsync(teamA, parentRunId: null, createdDate: t.AddMinutes(-2), workflowId: null);
        var mid = await InsertRunAsync(teamA, parentRunId: null, createdDate: t.AddMinutes(-1), workflowId: null);
        var top = await InsertRunAsync(teamA, parentRunId: null, createdDate: t, workflowId: null);

        var result = await ListAsync(teamA, 2);

        result.Select(r => r.Id).ShouldBe(new[] { top, mid });   // the 2 newest only
    }

    private async Task<IReadOnlyList<WorkflowRunSummary>> ListAsync(Guid teamId, int limit)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowService>().ListTeamRunsAsync(teamId, limit, CancellationToken.None);
    }

    private async Task<Guid> InsertRunAsync(Guid teamId, Guid? parentRunId, DateTimeOffset createdDate, Guid? workflowId, string? sourceType = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var resolvedSource = sourceType ?? (workflowId == null ? WorkflowRunSourceTypes.Snapshot : WorkflowRunSourceTypes.Manual);

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId,
            TeamId = teamId,
            WorkflowId = workflowId,
            SourceType = resolvedSource,
            ActorType = "user",
            ActorId = SystemUsers.SeederId,
            NormalizedPayloadJson = "{}",
            Status = WorkflowRunRequestStatus.Consumed,
            ReceivedAt = createdDate,
            VerifiedAt = createdDate,
            NormalizedAt = createdDate,
        });

        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId,
            WorkflowId = workflowId,
            WorkflowVersion = workflowId == null ? null : 1,
            TeamId = teamId,
            RunRequestId = requestId,
            SourceType = resolvedSource,   // denorm mirrors the request — the team index now excludes children by THIS column
            ParentRunId = parentRunId,
            Status = WorkflowRunStatus.Enqueued,
            CreatedDate = createdDate,   // explicit → the audit interceptor leaves it (it only stamps a default value)
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync().ConfigureAwait(false);
        return runId;
    }
}
