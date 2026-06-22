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

    [Fact]
    public async Task Breaks_created_date_ties_by_id_descending_for_a_deterministic_boundary()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // All three share a created_date — child fan-out / batch replays insert in the same instant. With no
        // tiebreaker the cut at Take(limit) is nondeterministic; the query orders by id DESC (matching the keyset
        // index) so the order — and the page boundary — is stable. Postgres uuid ordering is a byte compare of the
        // canonical form, which equals an ordinal compare of the lowercase GUID string (NOT .NET's Guid.CompareTo).
        var sameInstant = DateTimeOffset.UtcNow;
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
            ids.Add(await InsertRunAsync(teamA, parentRunId: null, createdDate: sameInstant, workflowId: null));

        var expectedIdDesc = ids.OrderByDescending(id => id.ToString(), StringComparer.Ordinal).ToArray();

        var first = await ListAsync(teamA, 50);
        first.Select(r => r.Id).ShouldBe(expectedIdDesc, "tied created_date rows must order by id DESC (Postgres uuid order)");

        var page = await ListAsync(teamA, 2);
        page.Select(r => r.Id).ShouldBe(expectedIdDesc.Take(2).ToArray(),
            customMessage: "the limited page must be the deterministic id-DESC prefix, not an arbitrary 2 of the 4 ties");
    }

    [Fact]
    public async Task Keyset_pages_walk_every_run_once_in_order_with_no_overlap_or_gap()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var t = DateTimeOffset.UtcNow;
        // 5 runs; the first two SHARE a created_date so paging must carry the (created_date, id) tiebreaker across a
        // tie that straddles a page boundary — the classic keyset trap that an OFFSET or created_date-only cursor fails.
        await InsertRunAsync(teamA, parentRunId: null, createdDate: t, workflowId: null);
        await InsertRunAsync(teamA, parentRunId: null, createdDate: t, workflowId: null);
        await InsertRunAsync(teamA, parentRunId: null, createdDate: t.AddMinutes(-1), workflowId: null);
        await InsertRunAsync(teamA, parentRunId: null, createdDate: t.AddMinutes(-2), workflowId: null);
        await InsertRunAsync(teamA, parentRunId: null, createdDate: t.AddMinutes(-3), workflowId: null);

        // The canonical full order is a single big page (already proven deterministic by the tie test above).
        var canonical = (await ListAsync(teamA, 50)).Select(r => r.Id).ToList();
        canonical.Count.ShouldBe(5);

        // Walk in pages of 2 following the cursor; the concatenation must equal the canonical order exactly.
        var walked = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await PageAsync(teamA, cursor, 2);
            walked.AddRange(page.Items.Select(r => r.Id));
            cursor = page.NextCursor;
            (++pages).ShouldBeLessThan(10, "keyset paging must terminate, not loop");
        } while (cursor != null);

        walked.ShouldBe(canonical, "keyset pages must reconstruct the full order with no overlap, gap, or reorder");
        pages.ShouldBe(3, "5 rows in pages of 2 = pages of [2,2,1]; the last (partial) page ends pagination with a null cursor");
    }

    [Fact]
    public async Task Keyset_splits_a_created_date_tie_across_a_page_boundary_without_skip_or_dupe()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // FOUR rows share ONE created_date, plus one older row. With page size 2 the first boundary lands INSIDE the
        // tie group, so page 2 can only be selected by the cursor's (created_date == c.created_date && id < c.id)
        // tiebreaker branch — proving the id comparison runs in Postgres uuid order (the order the index + ORDER BY
        // use). If it translated to .NET Guid order instead, page 2 would skip/dupe a tied row and walked != canonical.
        var sameInstant = DateTimeOffset.UtcNow;
        for (var i = 0; i < 4; i++)
            await InsertRunAsync(teamA, parentRunId: null, createdDate: sameInstant, workflowId: null);
        await InsertRunAsync(teamA, parentRunId: null, createdDate: sameInstant.AddMinutes(-1), workflowId: null);

        var canonical = (await ListAsync(teamA, 50)).Select(r => r.Id).ToList();
        canonical.Count.ShouldBe(5);

        var walked = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await PageAsync(teamA, cursor, 2);   // boundary falls between tied rows 2 and 3
            walked.AddRange(page.Items.Select(r => r.Id));
            cursor = page.NextCursor;
        } while (cursor != null);

        walked.ShouldBe(canonical, "paging through a created_date tie group must reproduce the exact id-DESC order — the in-tie page boundary is decided solely by the cursor's id tiebreaker");
        walked.Distinct().Count().ShouldBe(5, "no row may appear on two pages across the in-tie boundary");
    }

    [Fact]
    public async Task A_full_last_page_still_reports_no_next_cursor()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var t = DateTimeOffset.UtcNow;
        await InsertRunAsync(teamA, parentRunId: null, createdDate: t, workflowId: null);
        await InsertRunAsync(teamA, parentRunId: null, createdDate: t.AddMinutes(-1), workflowId: null);

        // Exactly 2 rows, page size 2: the page is full, but there is no further row — NextCursor must be null, not a
        // cursor that yields an empty next page (the "fetch one extra" probe is what distinguishes these).
        var page = await PageAsync(teamA, cursor: null, limit: 2);
        page.Items.Count.ShouldBe(2);
        page.NextCursor.ShouldBeNull("a full page with nothing after it must not advertise a next page");
    }

    [Fact]
    public async Task Index_row_carries_the_parent_workflow_name_and_is_null_for_a_task_run()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var workflowId = await CreateNamedWorkflowAsync(teamId, userId, "Nightly Sync");
        await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);                                 // authored run (now)
        await InsertRunAsync(teamId, parentRunId: null, createdDate: DateTimeOffset.UtcNow.AddMinutes(-5), workflowId: null);   // task run

        var rows = await ListAsync(teamId, 50);

        rows.Single(r => r.WorkflowId == workflowId).WorkflowName.ShouldBe("Nightly Sync",
            customMessage: "the index LEFT JOINs the parent workflow so a row shows its name without a second lookup");
        rows.Single(r => r.WorkflowId == null).WorkflowName.ShouldBeNull("a task / snapshot run has no parent workflow → null name");
    }

    private async Task<Guid> CreateNamedWorkflowAsync(Guid teamId, Guid userId, string name)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<MediatR.IMediator>().Send(new CodeSpace.Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = name,
            Description = null,
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<CodeSpace.Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task<IReadOnlyList<WorkflowRunSummary>> ListAsync(Guid teamId, int limit)
    {
        var page = await PageAsync(teamId, cursor: null, limit);
        return page.Items;
    }

    private async Task<RunPage> PageAsync(Guid teamId, string? cursor, int limit)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowService>().ListTeamRunsAsync(teamId, cursor, limit, CancellationToken.None);
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
