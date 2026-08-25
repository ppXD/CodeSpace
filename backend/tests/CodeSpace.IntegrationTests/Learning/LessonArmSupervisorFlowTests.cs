using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Learning;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.IntegrationTests.Learning;

/// <summary>
/// 🟢 Integration (real Postgres + the REAL <see cref="SupervisorTurnService"/> over the real decision ledger and
/// the real <see cref="ILessonReader"/>; only the decider is a stub, so the turn terminates without a model call):
/// D2's arm on the SUPERVISOR lane, which recorded nothing at all before this slice.
///
/// <para>Pins the whole measurement chain the referee needs: the arm is assigned from the run's UNDECORATED goal
/// (the projection's <c>displayTitle</c>, not the grounding-composed goal the node config carries), it lands on
/// the <c>supervisor_decision</c> row in Postgres, an injected run's lessons actually reach the turn prompt while
/// a withheld run's do not, and <see cref="SupervisorScorecardService"/> — the production reader behind the
/// supervisor scorecard query — surfaces the run's arm so the two arms can be compared.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class LessonArmSupervisorFlowTests
{
    private const string NodeId = "sup";
    private const string LessonText = "run restore before check.sh";

    private readonly PostgresFixture _fixture;

    public LessonArmSupervisorFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Theory]
    [InlineData(LessonArms.Injected)]
    [InlineData(LessonArms.Withheld)]
    public async Task A_supervisor_turn_persists_its_arm_and_only_an_injected_run_sees_the_lesson(string arm)
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedLessonAsync(teamId);

        var goal = GoalFor(teamId, arm);
        var runId = Guid.NewGuid();

        // The node config the DEEP projection bakes: the goal with a session grounding digest prepended, plus the
        // clean displayTitle. Hashing the composed goal is exactly the defect this pins — it would re-roll the arm.
        var goalConfig = new SupervisorGoalConfig { Goal = $"prior turn digest\n\n---\n{goal}", DisplayTitle = goal };

        var context = await RunTurnAsync(runId, teamId, goalConfig);

        context.LessonArm.ShouldBe(arm);

        if (arm == LessonArms.Injected)
            context.LessonLines.ShouldHaveSingleItem().ShouldContain(LessonText, customMessage: "the injected arm's treatment must actually reach the turn prompt, not just the ledger");
        else
            context.LessonLines.ShouldBeEmpty("the withheld arm is the control — no lesson text may reach the prompt");

        (await RecordedArmsAsync(runId, teamId)).ShouldAllBe(recorded => recorded == arm,
            "every decision row carries the run's arm — an unrecorded treatment contaminates the control group it is compared against");

        (await ScorecardArmAsync(teamId, runId)).ShouldBe(arm, "the supervisor scorecard is the production reader — without it the arm is written and never sliced");
    }

    [Fact]
    public async Task A_lesson_less_team_records_none_rather_than_a_control()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = Guid.NewGuid();

        var context = await RunTurnAsync(runId, teamId, new SupervisorGoalConfig { Goal = "any goal at all", DisplayTitle = "any goal at all" });

        context.LessonArm.ShouldBe(LessonArms.None);
        (await RecordedArmsAsync(runId, teamId)).ShouldAllBe(recorded => recorded == LessonArms.None, "no lesson existed — the run is outside the experiment, never a withheld control");
    }

    [Fact]
    public async Task The_recorded_arm_is_frozen_against_a_later_update()
    {
        // The arm is evidence, not state: 0166 extends the journal-immutability trigger to cover it, so no code path
        // (and no manual fix-up) can retro-assign a run to the arm that made its numbers look better.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedLessonAsync(teamId);

        var runId = Guid.NewGuid();
        await RunTurnAsync(runId, teamId, new SupervisorGoalConfig { Goal = GoalFor(teamId, LessonArms.Withheld), DisplayTitle = GoalFor(teamId, LessonArms.Withheld) });

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var ex = await Should.ThrowAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE supervisor_decision SET lesson_arm = 'injected' WHERE supervisor_run_id = {0}", runId));

        ex.ToString().ShouldContain("frozen at insert", customMessage: "the DB must reject the rewrite — an assignment that can be edited afterwards is not evidence");
    }

    // ─── Plumbing ────────────────────────────────────────────────────────────────

    /// <summary>The arm is a pure hash of (team, undecorated goal) — walk goals until one lands on the wanted arm (deterministic, so the test stays stable).</summary>
    private static string GoalFor(Guid teamId, string arm)
    {
        for (var i = 0; i < 256; i++)
            if (LessonArms.Assign(teamId, $"fix the flaky test {i}") == arm) return $"fix the flaky test {i}";

        throw new InvalidOperationException("256 candidates never hit the arm — the hash is broken");
    }

    private async Task<SupervisorTurnContext> RunTurnAsync(Guid runId, Guid teamId, SupervisorGoalConfig goalConfig)
    {
        using var scope = _fixture.BeginScope();

        await NewTurnService(scope).RunTurnAsync(runId, teamId, NodeId, goalConfig.Goal!, conversationId: null, goalConfig, CancellationToken.None);

        // Re-read through the real rehydrate so the assertions see what the NEXT turn would see off the durable tape.
        return await NewTurnService(scope).RehydrateFromDecisionLogAsync(runId, teamId, NodeId, goalConfig.Goal!, goalConfig, CancellationToken.None);
    }

    private async Task<IReadOnlyList<string?>> RecordedArmsAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId)
            .Select(d => d.LessonArm)
            .ToListAsync();
    }

    private async Task<string?> ScorecardArmAsync(Guid teamId, Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var card = await scope.Resolve<ISupervisorScorecardService>().ComputeAsync(teamId, since: null, CancellationToken.None);

        return card.Runs.Single(r => r.SupervisorRunId == runId).LessonArm;
    }

    private async Task SeedLessonAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.Lesson.Add(new Lesson
        {
            Id = Guid.NewGuid(), TeamId = teamId, Mode = "supervisor", FailureClass = "broken-acceptance-command",
            WhatFailed = "check.sh exits 2 on a clean tree", Why = "unrestored solution", HowToApply = LessonText,
            SourceRunIds = [Guid.NewGuid()], DistilledByModel = "test-model", ValidFrom = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
    }

    private static SupervisorTurnService NewTurnService(ILifetimeScope scope) => new(
        scope.Resolve<ISupervisorDecisionLog>(),
        new AlwaysStopDecider(),
        scope.Resolve<ISupervisorActionExecutor>(),
        scope.Resolve<CodeSpaceDbContext>(),
        scope.Resolve<ISupervisorAcceptanceGrader>(),
        scope.Resolve<Core.Services.Decisions.IDecisionQueueService>(),
        scope.Resolve<Core.Services.Supervisor.Arbiter.IDecisionArbiter>(),
        scope.Resolve<Core.Services.Decisions.IDecisionAnswerService>(),
        scope.Resolve<Core.Services.Plans.IWorkPlanService>(),
        scope.Resolve<Core.Services.Workflows.Lifecycle.IRunRecordLogger>(),
        scope.Resolve<Core.Services.Workflows.Artifacts.IArtifactOffloader>(),
        scope.Resolve<IPublishManifestStore>(),
        scope.Resolve<ISupervisorPublishedBranchResolver>(),
        scope.Resolve<Core.Services.Completion.ICompletionAssessmentComposer>(),
        scope.Resolve<Core.Services.Workflows.Budget.IBudgetLedger>(),
        scope.Resolve<ILessonReader>(),
        scope.Resolve<ILogger<SupervisorTurnService>>());

    private sealed class AlwaysStopDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new SupervisorDecision
            {
                Kind = SupervisorDecisionKinds.Stop,
                PayloadJson = JsonSerializer.Serialize(new SupervisorStopPayload { Outcome = "completed", Summary = "done" }, AgentJson.Options),
            });
    }
}
