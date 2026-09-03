using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Learning;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Tests.Fakes;
using CodeSpace.UnitTests.Agents;
using CodeSpace.UnitTests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Learning;

/// <summary>
/// 🟢 Unit: the D2 arm is the RUN's unit of assignment — driven through the REAL
/// <see cref="SupervisorTurnService"/>.<c>RehydrateFromDecisionLogAsync</c> against the shared in-memory ledger.
///
/// <para>Turn 1 assigns from (team, the run's undecorated goal) and the arm is stamped onto the decision row;
/// every later turn reads THAT arm back off the tape instead of re-deciding against a lesson ledger the nightly
/// distiller has since changed. Without the read-back a run whose team had no lesson at turn 1 (<c>none</c> —
/// outside the experiment) would silently become a treated run the moment a lesson landed, and its own control
/// data would be worthless.</para>
/// </summary>
[Trait("Category", "Unit")]
public class LessonArmFreezeTests
{
    private readonly Guid _runId = Guid.NewGuid();
    private readonly Guid _teamId = Guid.NewGuid();

    [Theory]
    [InlineData(LessonArms.Withheld)]
    [InlineData(LessonArms.None)]
    public async Task An_arm_recorded_on_the_tape_survives_a_lesson_landing_mid_run(string recorded)
    {
        // The team now HAS lessons — a turn that re-decided from scratch would inject them into a run assigned
        // to the control arm (withheld), or promote a run that was never in the experiment at all (none).
        var ledger = new FakeSupervisorDecisionLog();
        ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Plan, "{}", "{}", lessonArm: recorded);
        var lessons = new SeededLessonReader(LessonCount);

        var context = await Service(ledger, lessons).RehydrateFromDecisionLogAsync(_runId, _teamId, "sup", "goal", goalConfig: null, CancellationToken.None);

        context.LessonArm.ShouldBe(recorded, "the run's arm is read back off its own tape, never re-rolled per turn");
        context.LessonLines.ShouldBeEmpty("a run outside the treatment must not start carrying lessons mid-flight");
        lessons.Calls.ShouldBe(0, "a frozen control arm needs no lesson read at all — the control costs nothing per turn");
    }

    [Fact]
    public async Task A_frozen_injected_arm_keeps_carrying_the_teams_current_lessons()
    {
        // The complement: assignment is frozen, the injected CONTENT is not. The intervention under test is
        // "the prompt carries the team's CURRENT lessons", so a treated run re-reads them every turn.
        var ledger = new FakeSupervisorDecisionLog();
        ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Plan, "{}", "{}", lessonArm: LessonArms.Injected);

        var context = await Service(ledger, new SeededLessonReader(LessonCount)).RehydrateFromDecisionLogAsync(_runId, _teamId, "sup", "goal", goalConfig: null, CancellationToken.None);

        context.LessonArm.ShouldBe(LessonArms.Injected);
        context.LessonLines.Count.ShouldBe(LessonCount);
        context.LessonLines[0].ShouldBe(LessonArms.Line(Lesson(0)), "both lanes render a lesson line through the one shared renderer");
    }

    [Fact]
    public async Task Turn_one_assigns_from_the_clean_goal_and_stamps_the_arm_onto_the_decision_row()
    {
        // An empty tape: the arm is decided here, from the projection's clean displayTitle (NOT the composed goal,
        // which carries the session grounding), and must land on the row a scorecard reads.
        var ledger = new FakeSupervisorDecisionLog();
        var goalConfig = new SupervisorGoalConfig { DisplayTitle = "ship the feature" };
        var expected = LessonArms.Assign(_teamId, "ship the feature");

        await Service(ledger, new SeededLessonReader(LessonCount)).RunTurnAsync(_runId, _teamId, "sup", "grounding digest\n\n---\nship the feature", conversationId: null, goalConfig, CancellationToken.None);

        ledger.Rows.Count.ShouldBe(1);
        ledger.Rows[0].LessonArm.ShouldBe(expected,
            "a treatment nobody recorded contaminates its own control group — every decision row carries the run's arm");
    }

    [Fact]
    public async Task A_team_with_no_lesson_is_recorded_as_none_not_as_a_control()
    {
        var ledger = new FakeSupervisorDecisionLog();

        await Service(ledger, new SeededLessonReader(0)).RunTurnAsync(_runId, _teamId, "sup", "goal", conversationId: null, goalConfig: null, CancellationToken.None);

        ledger.Rows[0].LessonArm.ShouldBe(LessonArms.None, "no lesson existed — this run is outside the experiment, never a withheld control");
    }

    private const int LessonCount = 3;

    private static Lesson Lesson(int i) => new() { Id = Guid.NewGuid(), FailureClass = $"class-{i}", WhatFailed = $"failed-{i}", HowToApply = $"apply-{i}" };

    /// <summary>Counts reads so a test can prove the control arm never pays for one.</summary>
    private sealed class SeededLessonReader : ILessonReader
    {
        private readonly int _count;

        public SeededLessonReader(int count) => _count = count;

        public int Calls { get; private set; }

        public Task<IReadOnlyList<Lesson>> ListCurrentAsync(Guid teamId, Guid? repositoryId, int take, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<Lesson>>(Enumerable.Range(0, _count).Select(LessonArmFreezeTests.Lesson).ToList());
        }
    }

    private static SupervisorTurnService Service(FakeSupervisorDecisionLog ledger, ILessonReader lessons) =>
        new(ledger, new StubSupervisorDecider(), new StubSupervisorActionExecutor(), db: Infrastructure.EmptyTestDb.New(), new FakeAcceptanceGrader(), new FakeDecisionQueue(), new FakeDecisionArbiter(), new FakeDecisionAnswerService(), new FakeWorkPlanStore(), null!, null!, new FakePublishManifestStore(), new FakeSupervisorPublishedBranchResolver(), new NullCompletionComposer(), new AdmitAllBudgetLedger(), lessons, NullLogger<SupervisorTurnService>.Instance);
}
