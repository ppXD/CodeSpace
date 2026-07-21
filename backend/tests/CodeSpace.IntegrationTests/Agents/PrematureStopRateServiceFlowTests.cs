using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Agents;
using CodeSpace.Messages.Tasks;
using MediatR;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// 🟢 High fidelity: the REAL <see cref="PrematureStopRateService"/> over real Postgres, direct-seeding
/// <c>WorkflowRun</c> (+ <c>SupervisorDecisionRecord</c> for the supervisor lane) rather than driving the full
/// engine — the service under test is a pure DB-read/classification layer (mirrors
/// <c>UnattendedDeliveryScorecardFlowTests</c>'s own fidelity call, Rule 12). Proves the metric's core promise: the
/// supervisor lane's misleadingly-clean <c>WorkflowRunStatus.Success</c> is overridden by its OWN stop decision, a
/// still-in-progress run is counted (never silently dropped), a genuinely stuck run is surfaced loudly, and a
/// non-task (authored) run never enters the population at all.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class PrematureStopRateServiceFlowTests
{
    [Fact]
    public async Task An_abstaining_stop_lands_in_its_own_bucket_not_the_degraded_numerator()
    {
        // P5-1b: the honest ask is measurable on its own — the degraded numerator stays a pure stability signal,
        // and over-asking can never hide inside either success or failure.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, WorkflowRunStatus.Failure, TaskProjectionKinds.Supervisor);
        await SeedStopDecisionAsync(teamId, runId, payloadJson: "{}", outcomeJson: """{"outcome":"needs_clarification","summary":"Which env?"}""");

        var report = await ComputeAsync(teamId);

        report.NeedsClarificationRuns.ShouldBe(1);
        report.DegradedRuns.ShouldBe(0, "asking is not a stability failure");
        report.SucceededRuns.ShouldBe(0, "asking is not a success either");
    }

    private readonly PostgresFixture _fixture;

    public PrematureStopRateServiceFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_single_agent_Success_run_is_Succeeded()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedRunAsync(teamId, WorkflowRunStatus.Success, TaskProjectionKinds.SingleAgent);

        var report = await ComputeAsync(teamId);

        report.TotalRuns.ShouldBe(1);
        report.SucceededRuns.ShouldBe(1);
        report.PrematureStopRate.ShouldBe(0.0);
    }

    [Fact]
    public async Task A_single_agent_Failure_run_is_Degraded()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedRunAsync(teamId, WorkflowRunStatus.Failure, TaskProjectionKinds.SingleAgent);

        var report = await ComputeAsync(teamId);

        report.DegradedRuns.ShouldBe(1);
        report.PrematureStopRate.ShouldBe(1.0);
    }

    [Fact]
    public async Task A_plan_map_Failure_run_is_Degraded()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedRunAsync(teamId, WorkflowRunStatus.Failure, TaskProjectionKinds.PlanMapDynamic);

        var report = await ComputeAsync(teamId);

        report.DegradedRuns.ShouldBe(1);
    }

    [Fact]
    public async Task A_supervisor_run_with_a_forced_stop_is_Degraded_even_though_WorkflowRunStatus_reads_Success()
    {
        // THE core gap: the DAG's Terminal node can still report Success even when the supervisor itself was
        // force-stopped by a bound. This proves the REAL query joins to the run's own stop decision correctly,
        // not just the pure Classify unit already pinned.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, WorkflowRunStatus.Success, TaskProjectionKinds.Supervisor);
        await SeedStopDecisionAsync(teamId, runId, payloadJson: """{"reason":"no progress"}""", outcomeJson: null);

        var report = await ComputeAsync(teamId);

        report.DegradedRuns.ShouldBe(1);
        report.SucceededRuns.ShouldBe(0);
    }

    [Fact]
    public async Task A_supervisor_run_with_a_genuine_success_stop_is_Succeeded()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, WorkflowRunStatus.Success, TaskProjectionKinds.Supervisor);
        await SeedStopDecisionAsync(teamId, runId, payloadJson: "{}", outcomeJson: """{"outcome":"completed","summary":"done"}""");

        var report = await ComputeAsync(teamId);

        report.SucceededRuns.ShouldBe(1);
        report.DegradedRuns.ShouldBe(0);
    }

    [Fact]
    public async Task A_supervisor_run_with_no_stop_decision_falls_back_to_its_own_WorkflowRunStatus()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedRunAsync(teamId, WorkflowRunStatus.Failure, TaskProjectionKinds.Supervisor);

        var report = await ComputeAsync(teamId);

        report.DegradedRuns.ShouldBe(1, "no stop decision was ever recorded — the run's own honest Failure status decides");
    }

    [Fact]
    public async Task A_cancelled_run_is_excluded_from_the_rate_but_counted_in_TotalRuns()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedRunAsync(teamId, WorkflowRunStatus.Cancelled, TaskProjectionKinds.SingleAgent);

        var report = await ComputeAsync(teamId);

        report.TotalRuns.ShouldBe(1);
        report.CancelledRuns.ShouldBe(1);
        report.PrematureStopRate.ShouldBeNull("a deliberate cancel is the ONLY settled outcome, and it's excluded from both halves of the rate — nothing left to divide");
    }

    [Fact]
    public async Task A_still_running_run_is_counted_but_never_folded_into_the_rate()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedRunAsync(teamId, WorkflowRunStatus.Running, TaskProjectionKinds.SingleAgent, createdAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        var report = await ComputeAsync(teamId);

        report.TotalRuns.ShouldBe(1);
        report.StillInProgressRuns.ShouldBe(1);
        report.StuckRuns.ShouldBe(0, "5 minutes is nowhere near the default 24h stuck threshold");
        report.PrematureStopRate.ShouldBeNull("nothing has settled yet");
    }

    [Theory]
    [InlineData(WorkflowRunStatus.Pending)]
    [InlineData(WorkflowRunStatus.Enqueued)]
    [InlineData(WorkflowRunStatus.Running)]
    [InlineData(WorkflowRunStatus.Suspended)]
    public async Task Every_non_terminal_status_counts_as_still_in_progress(WorkflowRunStatus status)
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedRunAsync(teamId, status, TaskProjectionKinds.SingleAgent);

        (await ComputeAsync(teamId)).StillInProgressRuns.ShouldBe(1);
    }

    [Fact]
    public async Task A_run_stuck_past_the_default_threshold_is_surfaced_as_StuckRuns_not_silently_hidden()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedRunAsync(teamId, WorkflowRunStatus.Running, TaskProjectionKinds.SingleAgent, createdAt: DateTimeOffset.UtcNow.AddHours(-25));

        var report = await ComputeAsync(teamId);

        report.StillInProgressRuns.ShouldBe(1);
        report.StuckRuns.ShouldBe(1, "25 hours exceeds the default 24h stuck threshold");
    }

    [Fact]
    public async Task A_non_task_authored_run_with_no_projection_kind_never_enters_the_population()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedRunAsync(teamId, WorkflowRunStatus.Failure, projectionKind: null);

        (await ComputeAsync(teamId)).TotalRuns.ShouldBe(0, "this metric is scoped to task launches (ProjectionKind is not null) — an authored/webhook run is out of scope");
    }

    [Fact]
    public async Task The_rate_reflects_a_realistic_mix_across_lanes()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        await SeedRunAsync(teamId, WorkflowRunStatus.Success, TaskProjectionKinds.SingleAgent);        // Succeeded
        await SeedRunAsync(teamId, WorkflowRunStatus.Success, TaskProjectionKinds.SingleAgent);        // Succeeded
        await SeedRunAsync(teamId, WorkflowRunStatus.Failure, TaskProjectionKinds.SingleAgent);        // Degraded
        var supRunId = await SeedRunAsync(teamId, WorkflowRunStatus.Success, TaskProjectionKinds.Supervisor);
        await SeedStopDecisionAsync(teamId, supRunId, """{"reason":"cost cap reached"}""", null);      // Degraded (forced)
        await SeedRunAsync(teamId, WorkflowRunStatus.Cancelled, TaskProjectionKinds.SingleAgent);      // Cancelled (excluded from rate)

        var report = await ComputeAsync(teamId);

        report.TotalRuns.ShouldBe(5);
        report.SucceededRuns.ShouldBe(2);
        report.DegradedRuns.ShouldBe(2);
        report.CancelledRuns.ShouldBe(1);
        report.PrematureStopRate.ShouldBe(0.5, "2 degraded of 4 non-cancelled settled runs (2 succeeded + 2 degraded)");
    }

    [Fact]
    public async Task The_since_filter_windows_on_created_date()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var recent = await SeedRunAsync(teamId, WorkflowRunStatus.Success, TaskProjectionKinds.SingleAgent, createdAt: DateTimeOffset.UtcNow.AddHours(-1));
        await SeedRunAsync(teamId, WorkflowRunStatus.Success, TaskProjectionKinds.SingleAgent, createdAt: DateTimeOffset.UtcNow.AddDays(-30));

        var report = await ComputeAsync(teamId, since: DateTimeOffset.UtcNow.AddDays(-7));

        report.TotalRuns.ShouldBe(1, "only the 1-hour-old run is inside the 7-day window");
        recent.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task A_different_team_sees_none_of_another_teams_runs()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        await SeedRunAsync(teamA, WorkflowRunStatus.Failure, TaskProjectionKinds.SingleAgent);

        (await ComputeAsync(teamB)).TotalRuns.ShouldBe(0);
    }

    [Fact]
    public async Task The_team_scoped_query_handler_returns_only_the_callers_team_report()
    {
        var (teamA, userA) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        await SeedRunAsync(teamA, WorkflowRunStatus.Success, TaskProjectionKinds.SingleAgent);
        await SeedRunAsync(teamB, WorkflowRunStatus.Failure, TaskProjectionKinds.SingleAgent);
        await SeedRunAsync(teamB, WorkflowRunStatus.Failure, TaskProjectionKinds.SingleAgent);

        using var scope = _fixture.BeginScopeAs(userA, teamA, Roles.Admin);
        var report = await scope.Resolve<IMediator>().Send(new GetPrematureStopRateQuery { Since = null });

        report.TotalRuns.ShouldBe(1, "team A's own single run — team B's 2 runs must never leak in");
        report.SucceededRuns.ShouldBe(1);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────

    private async Task<PrematureStopRateReport> ComputeAsync(Guid teamId, DateTimeOffset? since = null)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IPrematureStopRateService>().ComputeAsync(teamId, since, CancellationToken.None);
    }

    /// <summary>A snapshot-style (WorkflowId-less) task run, mirroring <c>UnattendedDeliveryScorecardFlowTests.SeedTerminalRunAsync</c> but additionally stamping <see cref="WorkflowRun.ProjectionKind"/> — the axis THIS service dispatches its classification on. Null projection kind seeds a non-task (authored) run.</summary>
    private async Task<Guid> SeedRunAsync(Guid teamId, WorkflowRunStatus status, string? projectionKind, DateTimeOffset? createdAt = null)
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
            ProjectionKind = projectionKind,
            Status = status,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
            CreatedDate = createdAt ?? default,
        });

        await db.SaveChangesAsync();
        return runId;
    }

    private async Task SeedStopDecisionAsync(Guid teamId, Guid supervisorRunId, string payloadJson, string? outcomeJson)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;

        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = supervisorRunId, Sequence = 1,
            DecisionKind = SupervisorDecisionKinds.Stop, IdempotencyKey = $"stop-{Guid.NewGuid():N}", InputHash = "test",
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payloadJson, OutcomeJson = outcomeJson,
            FenceEpoch = 1, CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });

        await db.SaveChangesAsync();
    }
}
