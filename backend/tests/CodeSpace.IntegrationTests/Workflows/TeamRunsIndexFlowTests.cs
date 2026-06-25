using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Pins the team runs index backing the Runs page — both <see cref="IWorkflowService.ListTeamRunsAsync"/> (keyset, the
/// live cockpit feed) and <see cref="IWorkflowService.ListTeamRunsPageAsync"/> (offset + total count, the numbered
/// History pager):
///   1. Team-scoped: only the asking team's runs (never another team's).
///   2. Nested-execution excluded: a flow.subworkflow child (SourceType `workflow.child`) is filtered out.
///   3. Forks included: a replay / rerun run carries a ParentRunId (lineage) but IS a top-level run — it stays.
///   4. Source-agnostic: a task / snapshot run (null WorkflowId) is included — TeamId is on the run directly.
///   5. Newest-first, capped at the requested limit; the same filter dimensions narrow both pagination paths.
///   6. Offset pages carry the filtered TotalCount (a past-the-end page is empty but still reports it).
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

    [Fact]
    public async Task Filters_by_status()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var t = DateTimeOffset.UtcNow;
        var failed = await InsertRunAsync(teamA, null, t, workflowId: null, status: WorkflowRunStatus.Failure);
        await InsertRunAsync(teamA, null, t.AddMinutes(-1), workflowId: null, status: WorkflowRunStatus.Success);
        await InsertRunAsync(teamA, null, t.AddMinutes(-2), workflowId: null, status: WorkflowRunStatus.Running);

        var rows = await FilterAsync(teamA, new RunListFilter { Statuses = new[] { WorkflowRunStatus.Failure } });

        rows.Select(r => r.Id).ShouldBe(new[] { failed }, "a one-element status set returns only runs in that lifecycle state");
    }

    [Fact]
    public async Task Filters_by_a_status_set_the_active_group_in_one_query()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var t = DateTimeOffset.UtcNow;
        var running = await InsertRunAsync(teamA, null, t, workflowId: null, status: WorkflowRunStatus.Running);
        var suspended = await InsertRunAsync(teamA, null, t.AddMinutes(-1), workflowId: null, status: WorkflowRunStatus.Suspended);
        await InsertRunAsync(teamA, null, t.AddMinutes(-2), workflowId: null, status: WorkflowRunStatus.Success);   // terminal — excluded
        await InsertRunAsync(teamA, null, t.AddMinutes(-3), workflowId: null, status: WorkflowRunStatus.Failure);   // terminal — excluded

        // The whole point of a status SET: the non-terminal "active" group is ONE query, not N — newest first.
        var rows = await FilterAsync(teamA, new RunListFilter { Statuses = new[] { WorkflowRunStatus.Running, WorkflowRunStatus.Suspended } });

        rows.Select(r => r.Id).ShouldBe(new[] { running, suspended }, "a status set returns runs in ANY of the listed states (SQL = ANY), excluding the rest");
    }

    [Fact]
    public async Task Filters_by_source_type()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var t = DateTimeOffset.UtcNow;
        var replay = await InsertRunAsync(teamA, null, t, workflowId: null, sourceType: WorkflowRunSourceTypes.Replay);
        await InsertRunAsync(teamA, null, t.AddMinutes(-1), workflowId: null, sourceType: WorkflowRunSourceTypes.Snapshot);
        var cron = await InsertRunAsync(teamA, null, t.AddMinutes(-2), workflowId: null, sourceType: WorkflowRunSourceTypes.ScheduleCron);

        (await FilterAsync(teamA, new RunListFilter { SourceTypes = new[] { WorkflowRunSourceTypes.Replay } }))
            .Select(r => r.Id).ShouldBe(new[] { replay }, "a one-element source set matches that token exactly");

        (await FilterAsync(teamA, new RunListFilter { SourceTypes = new[] { WorkflowRunSourceTypes.Replay, WorkflowRunSourceTypes.ScheduleCron } }))
            .Select(r => r.Id).ShouldBe(new[] { replay, cron },
                customMessage: "values within a field are OR'd (source_type = ANY) — replay + cron, snapshot excluded, newest first");
    }

    [Fact]
    public async Task Filters_by_created_date_window_inclusive_lower_exclusive_upper()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var t = DateTimeOffset.UtcNow;
        await InsertRunAsync(teamA, null, t, workflowId: null);                       // == Until → excluded (exclusive upper)
        var inWindowNew = await InsertRunAsync(teamA, null, t.AddHours(-1), workflowId: null);
        var inWindowOld = await InsertRunAsync(teamA, null, t.AddHours(-2), workflowId: null);   // == Since → included (inclusive lower)
        await InsertRunAsync(teamA, null, t.AddHours(-3), workflowId: null);          // < Since → excluded

        var rows = await FilterAsync(teamA, new RunListFilter { Since = t.AddHours(-2), Until = t });

        rows.Select(r => r.Id).ShouldBe(new[] { inWindowNew, inWindowOld },
            customMessage: "Since is an inclusive lower bound, Until an exclusive upper bound, on created_date — newest first");
    }

    [Fact]
    public async Task Filters_by_workflow_id_excluding_task_runs_and_other_workflows()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var wfA = await CreateNamedWorkflowAsync(teamId, userId, "Alpha");
        var wfB = await CreateNamedWorkflowAsync(teamId, userId, "Beta");
        var wfC = await CreateNamedWorkflowAsync(teamId, userId, "Gamma");
        var runA = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, wfA, teamId);
        var runB = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, wfB, teamId);
        await WorkflowsTestSeed.SeedManualRunAsync(_fixture, wfC, teamId);                            // workflow C — excluded
        await InsertRunAsync(teamId, null, DateTimeOffset.UtcNow.AddMinutes(-5), workflowId: null);   // a task run (null workflow)

        (await FilterAsync(teamId, new RunListFilter { WorkflowIds = new[] { wfA } }))
            .Select(r => r.Id).ShouldBe(new[] { runA }, "a one-element workflow set returns only that workflow's runs — not the task run");

        (await FilterAsync(teamId, new RunListFilter { WorkflowIds = new[] { wfA, wfB } }))
            .Select(r => r.Id).ShouldBe(new[] { runA, runB }, ignoreOrder: true,
                customMessage: "workflow ids are OR'd (workflow_id = ANY) — A and B, never C, never the null-workflow task run");
    }

    [Fact]
    public async Task Combines_filters_as_an_intersection()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var t = DateTimeOffset.UtcNow;
        var match = await InsertRunAsync(teamA, null, t, workflowId: null, sourceType: WorkflowRunSourceTypes.Replay, status: WorkflowRunStatus.Failure);
        await InsertRunAsync(teamA, null, t.AddMinutes(-1), workflowId: null, sourceType: WorkflowRunSourceTypes.Replay, status: WorkflowRunStatus.Success);   // wrong status
        await InsertRunAsync(teamA, null, t.AddMinutes(-2), workflowId: null, sourceType: WorkflowRunSourceTypes.Snapshot, status: WorkflowRunStatus.Failure);  // wrong source

        var rows = await FilterAsync(teamA, new RunListFilter { SourceTypes = new[] { WorkflowRunSourceTypes.Replay }, Statuses = new[] { WorkflowRunStatus.Failure } });

        rows.Select(r => r.Id).ShouldBe(new[] { match }, "multiple filter dimensions compose as AND — only the run matching every dimension");
    }

    [Fact]
    public async Task A_filter_narrows_keyset_pagination_without_leaking_non_matching_rows()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var t = DateTimeOffset.UtcNow;
        var failures = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            failures.Add(await InsertRunAsync(teamA, null, t.AddMinutes(-i), workflowId: null, status: WorkflowRunStatus.Failure));
            await InsertRunAsync(teamA, null, t.AddMinutes(-i), workflowId: null, status: WorkflowRunStatus.Success);   // interleaved non-matching
        }

        var filter = new RunListFilter { Statuses = new[] { WorkflowRunStatus.Failure } };
        var walked = new List<Guid>();
        string? cursor = null;
        do
        {
            using var scope = _fixture.BeginScope();
            var page = await scope.Resolve<IWorkflowService>().ListTeamRunsAsync(teamA, filter, cursor, limit: 2, CancellationToken.None);
            walked.AddRange(page.Items.Select(r => r.Id));
            cursor = page.NextCursor;
        } while (cursor != null);

        walked.ShouldBe(failures, "the filter holds across every page — keyset paging never leaks a non-matching (Success) row");
    }

    [Fact]
    public async Task Filters_by_run_kind_generated_from_source_and_by_projection_kind()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var t = DateTimeOffset.UtcNow;

        var task = await InsertRunAsync(teamA, null, t, workflowId: null, sourceType: WorkflowRunSourceTypes.Snapshot, projectionKind: "single-agent");        // → run_kind=task
        var supervisor = await InsertRunAsync(teamA, null, t.AddMinutes(-1), workflowId: null, sourceType: WorkflowRunSourceTypes.Snapshot, projectionKind: "supervisor");  // task + supervisor
        var replay = await InsertRunAsync(teamA, null, t.AddMinutes(-2), workflowId: null, sourceType: WorkflowRunSourceTypes.Replay);                          // → run_kind=replay, no projection

        // run_kind is GENERATED from source_type by Postgres — no population, always consistent.
        (await FilterAsync(teamA, new RunListFilter { RunKinds = [RunKinds.Task] }))
            .Select(r => r.Id).ShouldBe(new[] { task, supervisor }, ignoreOrder: true, "run_kind=task = source_type snapshot (generated column)");
        (await FilterAsync(teamA, new RunListFilter { RunKinds = [RunKinds.Replay] }))
            .Select(r => r.Id).ShouldBe(new[] { replay }, "run_kind=replay = source_type replay/rerun");
        (await FilterAsync(teamA, new RunListFilter { RunKinds = [RunKinds.Task, RunKinds.Replay] }))
            .Select(r => r.Id).ShouldBe(new[] { task, supervisor, replay }, ignoreOrder: true, "run kinds are OR'd (run_kind = ANY)");

        // projection_kind: only a task run carries one; a replay (null projection_kind) matches no projection filter.
        (await FilterAsync(teamA, new RunListFilter { ProjectionKinds = ["supervisor"] }))
            .Select(r => r.Id).ShouldBe(new[] { supervisor }, "projection_kind filter matches the coordination mode; a run without one is excluded");
    }

    [Fact]
    public async Task Filters_by_agent_definition_via_exists_over_the_runs_runtime_agents()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        Guid agentX = Guid.NewGuid(), agentY = Guid.NewGuid(), agentZ = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;

        var usedX = await InsertRunAsync(teamA, null, t, workflowId: null);
        await SeedAgentRunAsync(teamA, usedX, agentX);
        // A supervisor-style run that spawned TWO agents across turns — the EXISTS must match by EITHER, not just the first.
        var usedYandZ = await InsertRunAsync(teamA, null, t.AddMinutes(-1), workflowId: null);
        await SeedAgentRunAsync(teamA, usedYandZ, agentY, "agent#turn0#0");
        await SeedAgentRunAsync(teamA, usedYandZ, agentZ, "agent#turn1#0");
        await InsertRunAsync(teamA, null, t.AddMinutes(-2), workflowId: null);   // no agent runs → matches no agent filter

        (await FilterAsync(teamA, new RunListFilter { AgentDefinitionIds = [agentX] }))
            .Select(r => r.Id).ShouldBe(new[] { usedX }, "matches a run that spawned an agent of that persona (EXISTS over agent_run)");

        (await FilterAsync(teamA, new RunListFilter { AgentDefinitionIds = [agentZ] }))
            .Select(r => r.Id).ShouldBe(new[] { usedYandZ }, "matches by an agent spawned on a LATER turn — the set is runtime-evolving, not launch-fixed");

        (await FilterAsync(teamA, new RunListFilter { AgentDefinitionIds = [agentX, agentY] }))
            .Select(r => r.Id).ShouldBe(new[] { usedX, usedYandZ }, ignoreOrder: true, "agent ids are OR'd; a run with no agents matches none");
    }

    [Fact]
    public async Task Filters_by_actor_excluding_actorless_webhook_runs()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        Guid actorX = Guid.NewGuid(), actorY = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;
        var byX = await InsertRunAsync(teamA, null, t, workflowId: null, actorId: actorX);
        var byY = await InsertRunAsync(teamA, null, t.AddMinutes(-1), workflowId: null, actorId: actorY);
        await InsertRunAsync(teamA, null, t.AddMinutes(-2), workflowId: null, sourceType: WorkflowRunSourceTypes.Manual, actorId: null);   // a webhook-style run with no user actor

        (await FilterAsync(teamA, new RunListFilter { ActorIds = [actorX] }))
            .Select(r => r.Id).ShouldBe(new[] { byX }, "actor filter returns only that user's runs");

        (await FilterAsync(teamA, new RunListFilter { ActorIds = [actorX, actorY] }))
            .Select(r => r.Id).ShouldBe(new[] { byX, byY }, ignoreOrder: true, "actor ids are OR'd (actor_id = ANY); a null-actor run matches no actor filter");
    }

    [Fact]
    public async Task Filters_by_repository_and_project_scope_with_array_overlap()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        Guid repoA = Guid.NewGuid(), repoB = Guid.NewGuid(), repoC = Guid.NewGuid();
        Guid projP = Guid.NewGuid(), projQ = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;

        // a MULTI-repo run touching {A,B} in project P, a single-repo {C} in project Q, and a no-scope run.
        var multiRepo = await InsertRunAsync(teamA, null, t, workflowId: null, repositoryIds: [repoA, repoB], projectIds: [projP]);
        var singleRepo = await InsertRunAsync(teamA, null, t.AddMinutes(-1), workflowId: null, repositoryIds: [repoC], projectIds: [projQ]);
        await InsertRunAsync(teamA, null, t.AddMinutes(-2), workflowId: null);   // no scope → matches no repo/project filter

        (await FilterAsync(teamA, new RunListFilter { RepositoryIds = [repoB] }))
            .Select(r => r.Id).ShouldBe(new[] { multiRepo }, "a run whose multi-repo scope CONTAINS the filtered repo matches (array overlap)");

        (await FilterAsync(teamA, new RunListFilter { RepositoryIds = [repoA, repoC] }))
            .Select(r => r.Id).ShouldBe(new[] { multiRepo, singleRepo }, ignoreOrder: true,
                customMessage: "repo ids are OR'd (array overlap &&) — a run touching ANY listed repo matches");

        (await FilterAsync(teamA, new RunListFilter { ProjectIds = [projQ] }))
            .Select(r => r.Id).ShouldBe(new[] { singleRepo }, "project scope filters the same way");

        (await FilterAsync(teamA, new RunListFilter { RepositoryIds = [Guid.NewGuid()] }))
            .ShouldBeEmpty("an unrelated repo matches nothing");
    }

    [Fact]
    public async Task HasPendingDecision_matches_node_and_agent_grain_and_is_narrower_than_suspended()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var t = DateTimeOffset.UtcNow;
        var nodeDecision = await InsertRunAsync(teamA, null, t, workflowId: null, status: WorkflowRunStatus.Suspended);
        await SeedNodeDecisionWaitAsync(nodeDecision);                                                  // parked on a Decision wait
        var agentDecision = await InsertRunAsync(teamA, null, t.AddMinutes(-1), workflowId: null, status: WorkflowRunStatus.Running);
        await SeedAgentDecisionAsync(teamA, agentDecision);                                             // an agent.code decision.request
        var resolvedDecision = await InsertRunAsync(teamA, null, t.AddMinutes(-2), workflowId: null, status: WorkflowRunStatus.Suspended);
        await SeedNodeDecisionWaitAsync(resolvedDecision, resolved: true);                              // a RESOLVED decision → no longer pending
        var suspendedNoDecision = await InsertRunAsync(teamA, null, t.AddMinutes(-3), workflowId: null, status: WorkflowRunStatus.Suspended);   // Suspended, but on no decision

        (await FilterAsync(teamA, new RunListFilter { HasPendingDecision = true }))
            .Select(r => r.Id).ShouldBe(new[] { nodeDecision, agentDecision },
                customMessage: "matches a node-grain Decision-Pending wait OR an agent-grain decision.request — and is NARROWER than Suspended (the resolved decision + the decision-less suspended run are excluded)");

        (await FilterAsync(teamA, new RunListFilter { HasPendingDecision = false }))
            .Select(r => r.Id).ShouldBe(new[] { resolvedDecision, suspendedNoDecision },
                customMessage: "the false form is the exact complement — runs WITHOUT a pending decision");
    }

    [Fact]
    public async Task NeedsAttention_is_the_broad_union_actionable_suspend_unresolved_failure_or_stuck_running()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var t = DateTimeOffset.UtcNow;

        // (a) a pending decision
        var decisionRun = await InsertRunAsync(teamA, null, t, workflowId: null, status: WorkflowRunStatus.Running);
        await SeedAgentDecisionAsync(teamA, decisionRun);

        // (b) a HUMAN-actionable suspend (Approval) → attention; a self-advancing supervisor suspend → NOT
        var approvalSuspended = await InsertRunAsync(teamA, null, t.AddMinutes(-1), workflowId: null, status: WorkflowRunStatus.Suspended);
        await SeedWaitAsync(approvalSuspended, WorkflowWaitKinds.Approval, WorkflowWaitStatuses.Pending);
        var selfAdvance = await InsertRunAsync(teamA, null, t.AddMinutes(-2), workflowId: null, status: WorkflowRunStatus.Suspended);
        await SeedWaitAsync(selfAdvance, WorkflowWaitKinds.SupervisorDecision, WorkflowWaitStatuses.Pending);   // self-advance — needs no human

        // (c) an UNRESOLVED failure → attention; a failure with a successful replay child → NOT
        var unresolvedFailure = await InsertRunAsync(teamA, null, t.AddMinutes(-3), workflowId: null, status: WorkflowRunStatus.Failure);
        var resolvedFailure = await InsertRunAsync(teamA, null, t.AddMinutes(-4), workflowId: null, status: WorkflowRunStatus.Failure);
        var replayChild = await InsertRunAsync(teamA, parentRunId: resolvedFailure, t.AddMinutes(-3), workflowId: null, status: WorkflowRunStatus.Success);   // successful replay → resolves the failure

        // (d) a genuinely STUCK running run (old StartedAt, no ledger) → attention; a healthy long-running run (recent ledger) → NOT
        var stuckRunning = await InsertRunAsync(teamA, null, t.AddMinutes(-5), workflowId: null, status: WorkflowRunStatus.Running, startedAt: t.AddHours(-2));
        var healthyRunning = await InsertRunAsync(teamA, null, t.AddMinutes(-6), workflowId: null, status: WorkflowRunStatus.Running, startedAt: t.AddHours(-2));
        await SeedRunRecordAsync(healthyRunning, t.AddMinutes(-1));   // recent ledger progress → alive, not stuck

        var success = await InsertRunAsync(teamA, null, t.AddMinutes(-7), workflowId: null, status: WorkflowRunStatus.Success);

        (await FilterAsync(teamA, new RunListFilter { NeedsAttention = true }))
            .Select(r => r.Id).ShouldBe(new[] { decisionRun, approvalSuspended, unresolvedFailure, stuckRunning }, ignoreOrder: true,
                customMessage: "the broad union: pending-decision OR human-actionable-suspend OR unresolved-failure OR stuck-running — EXCLUDING a self-advance suspend, a replayed failure, a fresh-ledger long-runner, and a success");

        (await FilterAsync(teamA, new RunListFilter { NeedsAttention = false }))
            .Select(r => r.Id).ShouldBe(new[] { selfAdvance, resolvedFailure, replayChild, healthyRunning, success }, ignoreOrder: true,
                customMessage: "the false form is the exact complement — self-advance, resolved-failure + its replay, a live long-runner, and a success all need no human");
    }

    [Fact]
    public async Task Offset_page_returns_the_requested_slice_newest_first_with_the_total_count()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var t = DateTimeOffset.UtcNow;
        var byNewest = new List<Guid>();   // byNewest[0] = newest (i=0), byNewest[4] = oldest
        for (var i = 0; i < 5; i++)
            byNewest.Add(await InsertRunAsync(teamA, null, t.AddMinutes(-i), workflowId: null));

        var page1 = await OffsetPageAsync(teamA, RunListFilter.None, page: 1, pageSize: 2);
        page1.Items.Select(r => r.Id).ShouldBe(byNewest.Take(2), "page 1 is the 2 newest, newest first");
        page1.TotalCount.ShouldBe(5, "the total counts every matching row, not just the page's slice");
        page1.NextCursor.ShouldBeNull("an offset page carries a total, not a keyset cursor");

        var page2 = await OffsetPageAsync(teamA, RunListFilter.None, page: 2, pageSize: 2);
        page2.Items.Select(r => r.Id).ShouldBe(byNewest.Skip(2).Take(2), "page 2 is the next slice — no overlap with page 1");

        var page3 = await OffsetPageAsync(teamA, RunListFilter.None, page: 3, pageSize: 2);
        page3.Items.Select(r => r.Id).ShouldBe(byNewest.Skip(4), "the last page is the single remaining row");
        page3.TotalCount.ShouldBe(5);
    }

    [Fact]
    public async Task Offset_page_past_the_end_is_empty_but_still_reports_the_true_total()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var t = DateTimeOffset.UtcNow;
        for (var i = 0; i < 3; i++)
            await InsertRunAsync(teamA, null, t.AddMinutes(-i), workflowId: null);

        var beyond = await OffsetPageAsync(teamA, RunListFilter.None, page: 9, pageSize: 10);

        beyond.Items.ShouldBeEmpty("a page past the last row has no items");
        beyond.TotalCount.ShouldBe(3, "but the total still reflects every matching row, so the pager knows the real page count");
    }

    [Fact]
    public async Task Offset_page_counts_and_slices_only_filter_matching_rows_the_terminal_history_case()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var t = DateTimeOffset.UtcNow;
        // The History list's real query: terminal runs only, offset-paginated. Interleave live runs that must NOT
        // count toward the total or appear on a page — they live in the pinned Live zone, not the paged History list.
        var terminal = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            terminal.Add(await InsertRunAsync(teamA, null, t.AddMinutes(-2 * i), workflowId: null, status: WorkflowRunStatus.Success));
            await InsertRunAsync(teamA, null, t.AddMinutes(-2 * i - 1), workflowId: null, status: WorkflowRunStatus.Running);   // live — excluded
        }

        var filter = new RunListFilter { Statuses = new[] { WorkflowRunStatus.Success, WorkflowRunStatus.Failure, WorkflowRunStatus.Cancelled } };
        var page1 = await OffsetPageAsync(teamA, filter, page: 1, pageSize: 2);

        page1.TotalCount.ShouldBe(3, "the count honors the filter — only the 3 terminal runs, never the interleaved live ones");
        page1.Items.Select(r => r.Id).ShouldBe(terminal.Take(2), "the page is the filtered slice, newest first");
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
        return await scope.Resolve<IWorkflowService>().ListTeamRunsAsync(teamId, RunListFilter.None, cursor, limit, CancellationToken.None);
    }

    private async Task<RunPage> OffsetPageAsync(Guid teamId, RunListFilter filter, int page, int pageSize)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowService>().ListTeamRunsPageAsync(teamId, filter, page, pageSize, CancellationToken.None);
    }

    private async Task<IReadOnlyList<WorkflowRunSummary>> FilterAsync(Guid teamId, RunListFilter filter)
    {
        using var scope = _fixture.BeginScope();
        var page = await scope.Resolve<IWorkflowService>().ListTeamRunsAsync(teamId, filter, cursor: null, limit: 50, CancellationToken.None);
        return page.Items;
    }

    private async Task<Guid> InsertRunAsync(Guid teamId, Guid? parentRunId, DateTimeOffset createdDate, Guid? workflowId, string? sourceType = null, WorkflowRunStatus status = WorkflowRunStatus.Enqueued, DateTimeOffset? startedAt = null, List<Guid>? repositoryIds = null, List<Guid>? projectIds = null, Guid? actorId = null, string? projectionKind = null)
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
            Status = status,
            StartedAt = startedAt,   // set for a "stuck running" run — NeedsAttention's running-stale branch reads StartedAt (not the audit timestamp)
            ScopeRepositoryIds = repositoryIds ?? [],
            ScopeProjectIds = projectIds ?? [],
            ActorId = actorId,
            ProjectionKind = projectionKind,   // RunKind is GENERATED from source_type — not set here
            CreatedDate = createdDate,   // explicit → the audit interceptor leaves it (it only stamps a default value)
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync().ConfigureAwait(false);
        return runId;
    }

    /// <summary>Park a node-grain Decision wait on a run (Pending unless <paramref name="resolved"/>). Mirrors the durable Decision substrate's node grain.</summary>
    private Task SeedNodeDecisionWaitAsync(Guid runId, bool resolved = false) =>
        SeedWaitAsync(runId, WorkflowWaitKinds.Decision, resolved ? WorkflowWaitStatuses.Resolved : WorkflowWaitStatuses.Pending);

    /// <summary>Park a wait of any kind/status on a run — used to exercise the human-actionable (Approval/Action) vs self-advance (SupervisorDecision) attention scoping.</summary>
    private async Task SeedWaitAsync(Guid runId, string waitKind, string status)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            NodeId = "wait",
            IterationKey = string.Empty,
            WaitKind = waitKind,
            Token = Guid.NewGuid().ToString("N"),
            WakeAt = DateTimeOffset.UtcNow.AddMinutes(5),
            Status = status,
            PayloadJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            ResolvedAt = status == WorkflowWaitStatuses.Resolved ? DateTimeOffset.UtcNow : null,
        });

        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>Append a ledger record at <paramref name="occurredAt"/> — recent ledger activity is the liveness signal NeedsAttention's stuck-running branch reads (NOT the run row's audit timestamp).</summary>
    private async Task SeedRunRecordAsync(Guid runId, DateTimeOffset occurredAt)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.WorkflowRunRecord.Add(new WorkflowRunRecord
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            Sequence = 1,
            RecordType = WorkflowRunRecordTypes.RunQueued,
            NodeId = "n1",
            IterationKey = string.Empty,
            CorrelationId = null,
            PayloadJson = "{}",
            OccurredAt = occurredAt,
        });

        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>Spawn an agent run of the given persona under a run — the runtime-evolving relation the AgentDefinitionIds filter EXISTS over. A run can have several (supervisor spawns per turn), distinguished by <paramref name="iterationKey"/>.</summary>
    private async Task SeedAgentRunAsync(Guid teamId, Guid runId, Guid agentDefinitionId, string iterationKey = "agent#turn0#0")
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;

        db.AgentRun.Add(new AgentRun
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            WorkflowRunId = runId,
            AgentDefinitionId = agentDefinitionId,
            NodeId = "agent",
            IterationKey = iterationKey,
            Harness = "codex-cli",
            Status = AgentRunStatus.Running,
            TaskJson = "{}",
            CreatedDate = now,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedDate = now,
            LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>Park an agent-grain decision.request on a run: an AgentRun under the run + a tool-ledger row in the given status. Mirrors the durable Decision substrate's agent grain.</summary>
    private async Task SeedAgentDecisionAsync(Guid teamId, Guid runId, ToolCallLedgerStatus status = ToolCallLedgerStatus.AwaitingApproval)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var agentRunId = Guid.NewGuid();

        db.AgentRun.Add(new AgentRun
        {
            Id = agentRunId,
            TeamId = teamId,
            WorkflowRunId = runId,
            NodeId = "agent",
            IterationKey = "agent#turn0#0",
            Harness = "codex-cli",
            Status = AgentRunStatus.Running,
            TaskJson = "{}",
            CreatedDate = now,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedDate = now,
            LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync().ConfigureAwait(false);

        var ledgerId = Guid.NewGuid();
        db.ToolCallLedger.Add(new ToolCallLedger
        {
            Id = ledgerId,
            TeamId = teamId,
            AgentRunId = agentRunId,
            ToolKind = DecisionToolKinds.DecisionRequest,
            IdempotencyKey = $"decision.request:{ledgerId:N}",
            InputHash = new string('0', 64),
            Status = status,
            ApprovalDeadlineAt = now.AddMinutes(5),
            DecisionEnvelopeJson = "{}",
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
    }
}
