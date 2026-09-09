using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Core.Services.Learning;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Llm;
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
        var lessonId = await SeedLessonAsync(teamId);

        var goal = GoalFor(teamId, arm);
        var runId = Guid.NewGuid();

        // The node config the DEEP projection bakes: the goal with a session grounding digest prepended, plus the
        // clean displayTitle. Hashing the composed goal is exactly the defect this pins — it would re-roll the arm.
        var goalConfig = new SupervisorGoalConfig { Goal = $"prior turn digest\n\n---\n{goal}", DisplayTitle = goal };

        var context = await RunTurnAsync(runId, teamId, goalConfig);

        context.LessonArm.ShouldBe(arm);

        if (arm == LessonArms.Injected)
        {
            context.LessonLines.ShouldHaveSingleItem().ShouldContain(LessonText, customMessage: "the injected arm's treatment must actually reach the turn prompt, not just the ledger");
            context.LessonIds.ShouldBe([lessonId], "the exact prompt exposure must survive rehydrate");
        }
        else
        {
            context.LessonLines.ShouldBeEmpty("the withheld arm is the control — no lesson text may reach the prompt");
            context.LessonIds.ShouldBeEmpty();
        }

        (await RecordedArmsAsync(runId, teamId)).ShouldAllBe(recorded => recorded == arm,
            "every decision row carries the run's arm — an unrecorded treatment contaminates the control group it is compared against");
        (await RecordedLessonIdsAsync(runId, teamId)).ShouldAllBe(ids => ids.SequenceEqual(context.LessonIds),
            "every decision row freezes exactly the lessons its model prompt saw");

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

    [Fact]
    public async Task First_turn_semantic_selection_is_reused_from_the_immutable_decision_receipt()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var selected = Lesson(teamId, "selected lesson");
        var unrelated = Lesson(teamId, "unrelated lesson");
        using (var seed = _fixture.BeginScope())
        {
            seed.Resolve<CodeSpaceDbContext>().Lesson.AddRange(selected, unrelated);
            await seed.Resolve<CodeSpaceDbContext>().SaveChangesAsync();
        }
        var reader = new SemanticReceiptReader([selected, unrelated], [selected]);
        var goal = GoalFor(teamId, LessonArms.Injected);
        var runId = Guid.NewGuid();

        var context = await RunTurnAsync(runId, teamId, new SupervisorGoalConfig { Goal = goal, DisplayTitle = goal }, reader);

        context.LessonIds.ShouldBe([selected.Id]);
        context.LessonLines.ShouldHaveSingleItem().ShouldContain("selected lesson");
        context.LessonLines.ShouldAllBe(line => !line.Contains("unrelated lesson", StringComparison.Ordinal));
        reader.SemanticCalls.ShouldBe(1, "rehydration must reuse the first decision's exact receipt instead of re-sampling model relevance");
        reader.ObservedCallKind.ShouldBe(LlmLessonRelevanceEvaluator.CallKind, "the first semantic decision must enter the durable brain-plane cost/call scope");
        (await RecordedLessonIdsAsync(runId, teamId)).ShouldAllBe(ids => ids.SequenceEqual(new[] { selected.Id }));
    }

    // ─── Plumbing ────────────────────────────────────────────────────────────────

    /// <summary>The arm is a pure hash of (team, undecorated goal) — walk goals until one lands on the wanted arm (deterministic, so the test stays stable).</summary>
    private static string GoalFor(Guid teamId, string arm)
    {
        for (var i = 0; i < 256; i++)
            if (LessonArms.Assign(teamId, $"fix the flaky test {i}") == arm) return $"fix the flaky test {i}";

        throw new InvalidOperationException("256 candidates never hit the arm — the hash is broken");
    }

    private async Task<SupervisorTurnContext> RunTurnAsync(Guid runId, Guid teamId, SupervisorGoalConfig goalConfig, ILessonReader? lessons = null)
    {
        using var root = _fixture.BeginScope();
        using var scope = root.BeginLifetimeScope(builder => builder.RegisterInstance(AllRelevantEvaluator.Instance).As<ILessonRelevanceEvaluator>());

        await NewTurnService(scope, lessons).RunTurnAsync(runId, teamId, NodeId, goalConfig.Goal!, conversationId: null, goalConfig, CancellationToken.None);

        // Re-read through the real rehydrate so the assertions see what the NEXT turn would see off the durable tape.
        return await NewTurnService(scope, lessons).RehydrateFromDecisionLogAsync(runId, teamId, NodeId, goalConfig.Goal!, goalConfig, CancellationToken.None);
    }

    private async Task<IReadOnlyList<string?>> RecordedArmsAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId)
            .Select(d => d.LessonArm)
            .ToListAsync();
    }

    private async Task<IReadOnlyList<List<Guid>>> RecordedLessonIdsAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId)
            .Select(d => d.LessonIds)
            .ToListAsync();
    }

    private async Task<string?> ScorecardArmAsync(Guid teamId, Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var card = await scope.Resolve<ISupervisorScorecardService>().ComputeAsync(teamId, since: null, CancellationToken.None);

        return card.Runs.Single(r => r.SupervisorRunId == runId).LessonArm;
    }

    private async Task<Guid> SeedLessonAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var lesson = new Lesson
        {
            Id = Guid.NewGuid(), TeamId = teamId, Mode = "supervisor", FailureClass = "broken-acceptance-command",
            WhatFailed = "check.sh exits 2 on a clean tree", Why = "unrestored solution", HowToApply = LessonText,
            SourceRunIds = [Guid.NewGuid()], DistilledByModel = "test-model", ValidFrom = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.Add(LessonConsolidation.Lifetime),
        };
        db.Lesson.Add(lesson);

        await db.SaveChangesAsync();
        return lesson.Id;
    }

    private static SupervisorTurnService NewTurnService(ILifetimeScope scope, ILessonReader? lessons = null) => new(
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
        lessons ?? scope.Resolve<ILessonReader>(),
        scope.Resolve<ILogger<SupervisorTurnService>>());

    private static Lesson Lesson(Guid teamId, string howToApply) => new()
    {
        Id = Guid.NewGuid(), TeamId = teamId, Mode = RunModeKeys.Supervisor, FailureClass = "prior-failure", WhatFailed = "prior attempt failed", Why = "missing context", HowToApply = howToApply,
        SourceRunIds = [Guid.NewGuid()], DistilledByModel = "test-model", ValidFrom = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
    };

    private sealed class SemanticReceiptReader(IReadOnlyList<Lesson> candidates, IReadOnlyList<Lesson> selected) : ILessonReader
    {
        public int SemanticCalls { get; private set; }
        public string? ObservedCallKind { get; private set; }
        public Task<IReadOnlyList<Lesson>> ListCurrentAsync(LessonReadRequest request, CancellationToken cancellationToken) => Task.FromResult(candidates);
        public Task<LessonRelevanceResult> SelectAsync(LessonRelevanceRequest request, CancellationToken cancellationToken)
        {
            SemanticCalls++;
            ObservedCallKind = LlmCallContext.Current?.Kind;
            return Task.FromResult(new LessonRelevanceResult(selected, request.Candidates.Select(lesson => lesson.Id).ToList(), LessonRelevanceStatuses.Selected, "observed-model", new string('a', 64)));
        }
    }

    private sealed class AllRelevantEvaluator : ILessonRelevanceEvaluator
    {
        public static readonly AllRelevantEvaluator Instance = new();
        public Task<LessonRelevanceResult> EvaluateAsync(LessonRelevanceRequest request, CancellationToken cancellationToken)
        {
            var selected = request.Candidates.Take(request.Take).ToList();
            return Task.FromResult(new LessonRelevanceResult(selected, request.Candidates.Select(lesson => lesson.Id).ToList(), selected.Count == 0 ? LessonRelevanceStatuses.NoCandidates : LessonRelevanceStatuses.Selected, "test-observed-model", selected.Count == 0 ? null : new string('d', 64)));
        }
    }

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
