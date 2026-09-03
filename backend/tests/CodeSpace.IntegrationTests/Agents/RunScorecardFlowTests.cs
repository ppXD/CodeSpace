using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Core.Services.Learning;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Agents;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// 🟢 High fidelity: the REAL <see cref="IRunScorecardWriter"/> / <see cref="IRunScorecardBackfillService"/> /
/// <see cref="IRunScorecardTrendService"/> / <see cref="IBenchmarkResultStore"/> over real Postgres — the exact
/// seams production wires, including the real unique index and the real CHECK constraints. Direct-seeds the ledger
/// tables the writer reads (mirroring <see cref="UnattendedDeliveryScorecardFlowTests"/>): the services under test
/// are DB read/projection layers, so seeding the tables they read is the right tier (Rule 12), not a lower one.
///
/// <para>What this pins: exactly ONE row per terminal run and idempotence on replay (the row is a projection, not
/// an append-only opinion log); the era gate (a pre-protocol run is never scored, matching the live rollup's own
/// denominator); the backfill's skip-what-exists predicate; the trend's tenancy and day bucketing over real rows;
/// and the by-arm slice actually reading the lesson arm off the ledger.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RunScorecardFlowTests
{
    private readonly PostgresFixture _fixture;

    public RunScorecardFlowTests(PostgresFixture fixture) => _fixture = fixture;

    // ─── The write seam ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_completion_shadow_sweep_projects_a_terminal_runs_row()
    {
        // The seam itself, driven end to end: every terminal contract-era run passes through this sweep's
        // never-assessed pass EXACTLY once, and that visit is what makes a new run's trend point exist at all.
        // No assessment is pre-seeded on purpose — a run that already has one is not a sweep candidate, so
        // seeding one would test a state a real run never reaches before its first visit.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<ICompletionShadowService>().SweepAsync(50, CancellationToken.None);

        var row = await RowForAsync(runId);
        row.ShouldNotBeNull("the sweep is the write seam — a terminal run that passed through it must have its durable row");
        row.ScorerVersion.ShouldBe(UnattendedDeliveryScorer.ScorerVersion);
    }

    [Fact]
    public async Task A_replayed_projection_updates_the_one_row_rather_than_appending_a_second()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedMetricAssessmentAsync(teamId, runId, "Solved");
        await SeedDeliveredManifestAsync(teamId, runId);

        (await WriteAsync(runId, teamId)).ShouldBeTrue();
        (await WriteAsync(runId, teamId)).ShouldBeTrue("a replay re-projects the same settled facts — it is not an error");
        (await WriteAsync(runId, teamId)).ShouldBeTrue();

        var rows = await AllRowsForAsync(runId);
        rows.Count.ShouldBe(1, "the row is a PROJECTION of one run, not a history of opinions about it — the unique index is the guard");
        rows[0].Solved.ShouldBeTrue();
        rows[0].Delivered.ShouldBeTrue();
        rows[0].UnattendedSolvedWithDelivery.ShouldBeTrue();
    }

    [Fact]
    public async Task Later_evidence_moves_the_existing_row_instead_of_leaving_a_stale_one()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedMetricAssessmentAsync(teamId, runId, "Solved");

        await WriteAsync(runId, teamId);
        (await RowForAsync(runId))!.Delivered.ShouldBeFalse("nothing had left the sandbox yet");

        // A reconciler settles the manifest AFTER the run terminalized — the exact late-evidence case the shadow's
        // own revisit pass exists for.
        await SeedDeliveredManifestAsync(teamId, runId);
        await WriteAsync(runId, teamId);

        var row = (await RowForAsync(runId))!;
        row.Delivered.ShouldBeTrue("late-arriving delivery evidence must move the row, not be locked out by its own first write");
        row.UnattendedSolvedWithDelivery.ShouldBeTrue();
    }

    [Fact]
    public async Task A_pre_protocol_run_is_never_scored()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success, contractEra: false);

        (await WriteAsync(runId, teamId)).ShouldBeFalse();
        (await RowForAsync(runId)).ShouldBeNull("the era-aware denominator is the SAME here as in the live rollup — old tape is never re-derived into a trend point");
    }

    [Fact]
    public async Task An_in_flight_run_is_never_scored()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Running);

        (await WriteAsync(runId, teamId)).ShouldBeFalse();
        (await RowForAsync(runId)).ShouldBeNull("a run that has not finished has not yet had the chance to deliver");
    }

    [Fact]
    public async Task A_run_from_another_team_is_not_writable_through_a_borrowed_team_id()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamA, WorkflowRunStatus.Success);

        (await WriteAsync(runId, teamB)).ShouldBeFalse("the candidate query is team-scoped and fail-closed");
        (await RowForAsync(runId)).ShouldBeNull();
    }

    [Fact]
    public async Task The_row_carries_the_runs_lesson_arm_and_brain_model()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedMetricAssessmentAsync(teamId, runId, "Solved");
        await SeedDecisionAsync(teamId, runId, sequence: 1, LessonArms.Injected);
        await SeedBrainInteractionAsync(teamId, runId, model: "claude-sonnet-4-6", inputTokens: 1000, outputTokens: 500);

        await WriteAsync(runId, teamId);

        var row = (await RowForAsync(runId))!;
        row.LessonArm.ShouldBe(LessonArms.Injected, "without the arm on the row the A/B is recorded but never sliceable");
        row.BrainModel.ShouldBe("claude-sonnet-4-6");
        row.BrainPlaneUsd.ShouldNotBeNull("the run's own decision call was priceable — the brain plane is spend the agent-cost plane does not see");
    }

    [Fact]
    public async Task A_run_with_no_decision_ledger_records_no_arm_rather_than_the_none_control()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);

        await WriteAsync(runId, teamId);

        (await RowForAsync(runId))!.LessonArm.ShouldBeNull("a single-agent run was never IN the experiment — that is not the same claim as drawing its empty-lesson control");
    }

    // ─── The backfill ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_backfill_fills_row_less_runs_and_skips_the_ones_already_projected()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var already = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        var missingA = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        var missingB = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Failure);

        await WriteAsync(already, teamId);
        var stampBefore = (await RowForAsync(already))!.LastModifiedDate;

        var written = await BackfillAsync(50);

        written.ShouldBeGreaterThanOrEqualTo(2, "both row-less runs were candidates");
        (await RowForAsync(missingA)).ShouldNotBeNull();
        (await RowForAsync(missingB)).ShouldNotBeNull();
        (await RowForAsync(already))!.LastModifiedDate.ShouldBe(stampBefore, "a run that HAS a row is not a candidate — the backlog only ever shrinks");
    }

    [Fact]
    public async Task The_backfill_is_self_terminating()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);

        await BackfillAsync(50);

        // Every row-less run of THIS team is now projected, so a second pass can find nothing of ours left. (Other
        // tests in the collection may leave their own candidates, so the assertion is over this team's runs.)
        var mine = await RowCountForTeamAsync(teamId);
        await BackfillAsync(50);
        (await RowCountForTeamAsync(teamId)).ShouldBe(mine, "a converged team stops being work — a run leaves the candidate set the moment its row lands");
    }

    [Fact]
    public async Task The_backfill_command_reaches_the_service_through_the_mediator()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            await scope.Resolve<IMediator>().Send(new BackfillRunScorecardsCommand { BatchSize = 50 });

        (await RowForAsync(runId)).ShouldNotBeNull("the Rule-14 chain is wired end to end, not just unit-proven");
    }

    // ─── The trend read ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_trend_buckets_a_teams_rows_by_utc_day_and_never_leaks_another_teams()
    {
        var (teamA, userA) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var today = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);

        await SeedRowAsync(teamA, completedAt: today.AddHours(2), headline: true);
        await SeedRowAsync(teamA, completedAt: today.AddHours(5), headline: false);
        await SeedRowAsync(teamA, completedAt: today.AddDays(-1).AddHours(3), headline: true);
        await SeedRowAsync(teamB, completedAt: today.AddHours(4), headline: true);

        var trend = await TrendAsync(userA, teamA, days: 7);

        trend.ScoredRuns.ShouldBe(3, "team B's row must never reach team A's trend — the team comes from ICurrentTeam, never the wire");
        trend.Buckets.Count.ShouldBe(2);
        trend.Buckets[^1].Day.ShouldBe(today);
        trend.Buckets[^1].Runs.ShouldBe(2);
        trend.Buckets[^1].UnattendedSolveWithDeliveryRate.ShouldBe(0.5);
        trend.Buckets[0].UnattendedSolveWithDeliveryRate.ShouldBe(1.0);
    }

    [Fact]
    public async Task A_row_outside_the_window_is_not_in_the_trend()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var today = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);

        await SeedRowAsync(teamId, completedAt: today, headline: true);
        await SeedRowAsync(teamId, completedAt: today.AddDays(-30), headline: true);

        var trend = await TrendAsync(userId, teamId, days: 7);

        trend.ScoredRuns.ShouldBe(1, "a rate names exactly the window it was measured over");
        trend.Since.ShouldBe(today.AddDays(-6));
    }

    [Fact]
    public async Task The_trend_slices_the_window_by_lesson_arm()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var today = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);

        await SeedRowAsync(teamId, today, headline: true, arm: LessonArms.Injected);
        await SeedRowAsync(teamId, today, headline: true, arm: LessonArms.Injected);
        await SeedRowAsync(teamId, today, headline: false, arm: LessonArms.Withheld);
        await SeedRowAsync(teamId, today, headline: false, arm: null);

        var trend = await TrendAsync(userId, teamId, days: 7);

        trend.ByLessonArm.Single(s => s.Arm == LessonArms.Injected).UnattendedSolveWithDeliveryRate.ShouldBe(1.0);
        trend.ByLessonArm.Single(s => s.Arm == LessonArms.Withheld).UnattendedSolveWithDeliveryRate.ShouldBe(0.0);
        trend.ByLessonArm.Single(s => s.Arm == LessonArmSlicer.Unmeasured).Runs.ShouldBe(1, "the arm-less run stays out of both experiment arms and out of the control");
    }

    [Fact]
    public async Task An_empty_window_reports_no_buckets_rather_than_a_flat_line()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var trend = await TrendAsync(userId, teamId, days: 7);

        trend.ScoredRuns.ShouldBe(0);
        trend.Buckets.ShouldBeEmpty("no data is not a 0% rate");
        trend.ByLessonArm.ShouldBeEmpty();
    }

    // ─── The live rollup's by-arm slice ─────────────────────────────────────────────

    [Fact]
    public async Task The_live_rollup_slices_its_window_by_the_arm_on_the_ledger()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var injected = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedMetricAssessmentAsync(teamId, injected, "Solved");
        await SeedDeliveredManifestAsync(teamId, injected);
        await SeedDecisionAsync(teamId, injected, sequence: 1, LessonArms.Injected);

        var withheld = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedDecisionAsync(teamId, withheld, sequence: 1, LessonArms.Withheld);

        UnattendedDeliveryScorecard card;
        using (var scope = _fixture.BeginScope())
            card = await scope.Resolve<IUnattendedDeliveryScorecardService>().ComputeAsync(teamId, null, CancellationToken.None);

        card.Rollup.ByLessonArm.Single(s => s.Arm == LessonArms.Injected).UnattendedSolveWithDeliveryRate.ShouldBe(1.0);
        card.Rollup.ByLessonArm.Single(s => s.Arm == LessonArms.Withheld).UnattendedSolveWithDeliveryRate.ShouldBe(0.0);
        card.Rollup.ByLessonArm.Sum(s => s.Runs).ShouldBe(card.Rollup.TotalRuns, "the slice's denominator must equal the rollup's — a partial population would be a different measurement wearing the same label");
    }

    [Fact]
    public async Task The_by_arm_slice_prefers_the_persisted_row_when_one_exists()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedMetricAssessmentAsync(teamId, runId, "Solved");
        await SeedDeliveredManifestAsync(teamId, runId);
        await SeedDecisionAsync(teamId, runId, sequence: 1, LessonArms.Injected);

        await WriteAsync(runId, teamId);

        UnattendedDeliveryScorecard card;
        using (var scope = _fixture.BeginScope())
            card = await scope.Resolve<IUnattendedDeliveryScorecardService>().ComputeAsync(teamId, null, CancellationToken.None);

        card.Rollup.ByLessonArm.ShouldHaveSingleItem().Arm.ShouldBe(LessonArms.Injected);
        card.Rollup.ByLessonArm[0].UnattendedSolvedWithDeliveryRuns.ShouldBe(1);
    }

    [Fact]
    public async Task A_stale_persisted_row_cannot_make_the_slice_disagree_with_the_rollup()
    {
        // The review finding: the slice used to prefer the PERSISTED bits, so a row written before its run's
        // manifest settled made SolvedRuns and sum(ByLessonArm.SolvedRuns) disagree on the same page — two numbers
        // over the same runs, measured by two different clocks. Only the ARM comes from the row now.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedDecisionAsync(teamId, runId, sequence: 1, LessonArms.Injected);

        // Project while the run looks unsolved and undelivered — the row is now stale by construction.
        await WriteAsync(runId, teamId);
        (await RowForAsync(runId))!.Solved.ShouldBeFalse();

        // The evidence lands afterwards, and nothing revisits the row.
        await SeedMetricAssessmentAsync(teamId, runId, "Solved");
        await SeedDeliveredManifestAsync(teamId, runId);

        UnattendedDeliveryScorecard card;
        using (var scope = _fixture.BeginScope())
            card = await scope.Resolve<IUnattendedDeliveryScorecardService>().ComputeAsync(teamId, null, CancellationToken.None);

        (await RowForAsync(runId))!.Solved.ShouldBeFalse("the row is deliberately left stale — this test is about what the SLICE does with it");

        var injected = card.Rollup.ByLessonArm.Single(s => s.Arm == LessonArms.Injected);
        injected.SolvedRuns.ShouldBe(card.Rollup.SolvedRuns, "the slice is a PARTITION of the rollup — same runs, same bits, same totals, only grouped");
        injected.DeliveredRuns.ShouldBe(card.Rollup.DeliveredRuns);
        injected.UnattendedSolvedWithDeliveryRuns.ShouldBe(card.Rollup.UnattendedSolvedWithDeliveryRuns);
        injected.SolvedRuns.ShouldBe(1, "the live bits say solved even though the stale row says otherwise");
    }

    [Fact]
    public async Task A_planner_lane_run_is_measured_under_the_arm_its_plan_was_authored_with()
    {
        // Before this, RunLessonArms read supervisor_decision only, so a TREATED planner run — arm assigned,
        // lessons folded into the plan prompt — reported `unmeasured` and sat outside the experiment it was in.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedMetricAssessmentAsync(teamId, runId, "Solved");
        await SeedPlanAuthorNodeCompletedAsync(runId, LessonArms.Injected);

        await WriteAsync(runId, teamId);

        (await RowForAsync(runId))!.LessonArm.ShouldBe(LessonArms.Injected, "the planner lane assigns an arm too — reading only the supervisor ledger reported a treated run as unmeasured");
    }

    [Fact]
    public async Task A_plan_author_run_with_no_arm_stays_unmeasured()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedPlanAuthorNodeCompletedAsync(runId, lessonArm: null);

        await WriteAsync(runId, teamId);

        (await RowForAsync(runId))!.LessonArm.ShouldBeNull("an unstamped plan was never in the experiment — that is not the 'none' control");
    }

    [Fact]
    public async Task A_planner_arm_is_not_readable_through_a_borrowed_team_id()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamA, WorkflowRunStatus.Success);
        await SeedPlanAuthorNodeCompletedAsync(runId, LessonArms.Injected);

        using var scope = _fixture.BeginScope();
        var arms = await RunLessonArms.ReadAsync(scope.Resolve<CodeSpaceDbContext>(), [runId], teamB, CancellationToken.None);

        arms.ShouldBeEmpty("WorkflowRunRecord carries no team of its own — tenancy is a JOIN on the run, not a trusted argument");
    }

    // ─── Benchmark cells ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_graded_benchmark_cell_lands_a_durable_row_with_its_grade_and_intervention_flags()
    {
        // The persistence seam itself against real Postgres. The runner → store WIRING (one call per graded cell,
        // never for an infra-errored one, and swallowed on failure) is pinned by CorpusBenchmarkRunnerTests with a
        // recording double, so this tier proves the columns rather than re-proving the loop.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var agentRunId = Guid.NewGuid();

        var result = new BenchmarkResult
        {
            TaskId = "fix-the-failing-check",
            Mode = BenchmarkMode.HarnessCliWithMcp,
            AgentRunId = agentRunId,
            RunStatus = AgentRunStatus.Succeeded,
            DurationSeconds = 42.5,
            Grade = new BenchmarkGrade { Passed = true, Detail = "tests-passed" },
            TokenUsage = new AgentTokenUsage { InputTokens = 2000, OutputTokens = 800 },
            ReviseRounds = 2,
            McpFullCatalog = true,
            ExitReason = "output-flagged",
        };

        var selection = new BenchmarkAgentSelection { Harness = "claude-code", Model = "claude-sonnet-4-6" };

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IBenchmarkResultStore>().RecordAsync(teamId, "sha256/corpus-v2:abc123", result, selection, CancellationToken.None);

        using var read = _fixture.BeginScope();
        var row = await read.Resolve<CodeSpaceDbContext>().BenchmarkResultRecord.AsNoTracking()
            .SingleAsync(r => r.TeamId == teamId && r.AgentRunId == agentRunId);

        row.SuiteVersion.ShouldBe("sha256/corpus-v2:abc123", "the suite's content-derived identity is what a cross-run comparison joins on");
        row.TaskId.ShouldBe("fix-the-failing-check");
        row.Mode.ShouldBe(nameof(BenchmarkMode.HarnessCliWithMcp));
        row.Harness.ShouldBe("claude-code");
        row.Model.ShouldBe("claude-sonnet-4-6");
        row.Solved.ShouldBeTrue("the persisted verdict is the OBJECTIVE grade, not run completion");
        row.RunStatus.ShouldBe(nameof(AgentRunStatus.Succeeded));
        row.ReviseRounds.ShouldBe(2, "a solve rate that rode on extra attempts must be visible, not hidden in the headline");
        row.McpFullCatalog.ShouldBeTrue();
        row.ExitReason.ShouldBe("output-flagged");
        row.DurationSeconds.ShouldBe(42.5);
        row.CostUsd.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_cell_the_fake_cli_ran_records_no_cost_rather_than_zero()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var agentRunId = Guid.NewGuid();

        var result = new BenchmarkResult
        {
            TaskId = "fake-cli-cell",
            Mode = BenchmarkMode.HarnessCli,
            AgentRunId = agentRunId,
            RunStatus = AgentRunStatus.Succeeded,
            Grade = new BenchmarkGrade { Passed = false, Detail = "tests-failed-exit-1" },
            McpFullCatalog = false,
        };

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IBenchmarkResultStore>().RecordAsync(teamId, "sha256/corpus-v2:abc123", result, selection: null, CancellationToken.None);

        using var read = _fixture.BeginScope();
        var row = await read.Resolve<CodeSpaceDbContext>().BenchmarkResultRecord.AsNoTracking()
            .SingleAsync(r => r.TeamId == teamId && r.AgentRunId == agentRunId);

        row.CostUsd.ShouldBeNull("the deterministic fake CLI reports no usage — that is not a free real run");
        row.Harness.ShouldBeNull("no selection ⇒ the corpus default, recorded as absent rather than guessed");
        row.Model.ShouldBeNull();
        row.Solved.ShouldBeFalse();
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────

    private async Task<bool> WriteAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IRunScorecardWriter>().WriteAsync(runId, teamId, CancellationToken.None);
    }

    private async Task<int> BackfillAsync(int batchSize)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IRunScorecardBackfillService>().BackfillAsync(batchSize, CancellationToken.None);
    }

    private async Task<RunScorecardTrend> TrendAsync(Guid userId, Guid teamId, int days)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new GetScorecardTrendQuery { Days = days });
    }

    private async Task<RunScorecard?> RowForAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().RunScorecard.AsNoTracking().SingleOrDefaultAsync(s => s.WorkflowRunId == runId);
    }

    private async Task<List<RunScorecard>> AllRowsForAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().RunScorecard.AsNoTracking().Where(s => s.WorkflowRunId == runId).ToListAsync();
    }

    private async Task<int> RowCountForTeamAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().RunScorecard.AsNoTracking().CountAsync(s => s.TeamId == teamId);
    }

    /// <summary>A row seeded DIRECTLY, for the read-side tests — so the trend's bucketing + tenancy are pinned independently of everything the writer has to gather.</summary>
    private async Task SeedRowAsync(Guid teamId, DateTimeOffset completedAt, bool headline, string? arm = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.RunScorecard.Add(new RunScorecard
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            WorkflowRunId = Guid.NewGuid(),
            CompletedAt = completedAt,
            Solved = headline,
            Delivered = headline,
            HumanTouches = 0,
            UnattendedSolvedWithDelivery = headline,
            LessonArm = arm,
            ScorerVersion = UnattendedDeliveryScorer.ScorerVersion,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>A snapshot-style (WorkflowId-less) run in the given status — the single-agent lane's shape, mirroring <see cref="UnattendedDeliveryScorecardFlowTests"/>.</summary>
    private async Task<Guid> SeedTerminalRunAsync(Guid teamId, WorkflowRunStatus status, bool contractEra = true)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId,
            TeamId = teamId,
            SourceType = WorkflowRunSourceTypes.Manual,
            ActorType = "user",
            ActorId = SystemUsers.SeederId,
            NormalizedPayloadJson = "{}",
            Status = WorkflowRunRequestStatus.Consumed,
            ReceivedAt = now,
            VerifiedAt = now,
            NormalizedAt = now,
        });

        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId,
            TeamId = teamId,
            RunRequestId = requestId,
            SourceType = WorkflowRunSourceTypes.Manual,
            Status = status,
            CompletedAt = status is WorkflowRunStatus.Success or WorkflowRunStatus.Failure or WorkflowRunStatus.Cancelled ? now : null,
            CompletionPolicyVersion = contractEra ? CompletionPolicy.CurrentVersion : null,
            CompletionEnforcementMode = contractEra ? CompletionPolicy.CurrentMode.ToString() : null,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return runId;
    }

    /// <summary>The run's latest metric@1 verdict — the primary solve bit's only source.</summary>
    private async Task SeedMetricAssessmentAsync(Guid teamId, Guid runId, string metricOutcome)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.CompletionAssessmentRecord.Add(new CompletionAssessmentRecord
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            WorkflowRunId = runId,
            EnforcementMode = CompletionPolicy.CurrentMode.ToString(),
            Basis = "Receipts",
            Outcome = metricOutcome,
            Verification = "Objective",
            AssessmentJson = "{}",
            MetricOutcome = metricOutcome,
            MetricJson = "{}",
            LegacyIsSolved = metricOutcome == "Solved",
        });

        await db.SaveChangesAsync();
    }

    private async Task SeedDeliveredManifestAsync(Guid teamId, Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.PublishManifest.Add(new PublishManifest
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Kind = PublishManifestKind.Integration,
            WorkflowRunId = runId,
            RepositoryAlias = "primary",
            AcceptanceState = PublishAcceptanceState.Passed,
            PublishStateValue = PublishState.Pushed,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>A supervisor decision row carrying the frozen lesson arm — the ledger the arm is read back off.</summary>
    private async Task SeedDecisionAsync(Guid teamId, Guid runId, long sequence, string lessonArm)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;

        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            SupervisorRunId = runId,
            Sequence = sequence,
            DecisionKind = SupervisorDecisionKinds.Plan,
            IdempotencyKey = $"plan-{Guid.NewGuid():N}",
            InputHash = "test",
            Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"items":[]}""",
            LessonArm = lessonArm,
            FenceEpoch = 1,
            CreatedDate = now,
            CreatedBy = Guid.Empty,
            LastModifiedDate = now,
            LastModifiedBy = Guid.Empty,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A <c>plan.author</c> <c>node.completed</c> record carrying the promoted <c>lessonArm</c> output — the
    /// planner lane's durable arm carrier. Serialized through the REAL <c>RunRecordLogger.NodeCompletedPayload</c>
    /// shape (outputs + duration_ms) so the reader can only pass against the payload production actually writes.
    /// </summary>
    private async Task SeedPlanAuthorNodeCompletedAsync(Guid runId, string? lessonArm)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var outputs = new Dictionary<string, object> { ["planId"] = Guid.NewGuid(), ["version"] = 1, ["goal"] = "do the thing" };

        if (lessonArm is not null) outputs[RunLessonArms.PlanAuthorArmOutputKey] = lessonArm;

        db.WorkflowRunRecord.Add(new WorkflowRunRecord
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            NodeId = "plan",
            RecordType = WorkflowRunRecordTypes.NodeCompleted,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { outputs, duration_ms = 1200L }),
        });

        await db.SaveChangesAsync();
    }

    /// <summary>One priced <c>interaction.completed</c> brain-plane row — the ledger the run's brain model and brain spend are folded from.</summary>
    private async Task SeedBrainInteractionAsync(Guid teamId, Guid runId, string model, int inputTokens, int outputTokens)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.WorkflowRunRecord.Add(new WorkflowRunRecord
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            NodeId = "sup",
            RecordType = WorkflowRunRecordTypes.InteractionCompleted,
            // Serialized through the real serializer rather than hand-written, so the payload shape can only be the
            // one RecordingStructuredLLMClientDecorator's CompletedPayload actually writes.
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                kind = RunScorecardWriter.SupervisorDecisionCallKind,
                provider = "Anthropic",
                model,
                usage = new { inputTokens, outputTokens },
            }),
        });

        await db.SaveChangesAsync();
    }
}
