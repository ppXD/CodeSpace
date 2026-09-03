using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Core.Services.Learning;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the pure mapping from the scorer's output onto a durable <c>run_scorecard</c> row.
///
/// <para>The one property worth guarding: the row's headline bit is COPIED from the scorer, never re-derived. A
/// second definition of "solved with delivery" living in the writer is precisely the drift the schema's own CHECK
/// constraint and <see cref="UnattendedDeliveryScorer.ScorerVersion"/> exist to prevent — so the theory below feeds
/// the SCORER and asserts the row agrees with it, rather than asserting a hand-written truth table twice.</para>
/// </summary>
[Trait("Category", "Unit")]
public class RunScorecardProjectionTests
{
    private static RunScorecard Project(bool solved, bool delivered, int touches, string? arm, decimal? cost = null, decimal? brain = null, string? brainModel = null)
    {
        var score = UnattendedDeliveryScorer.Score(new UnattendedDeliveryRunOutcome
        {
            WorkflowRunId = Guid.NewGuid(),
            Solved = solved,
            Delivered = delivered,
            HumanTouches = touches,
            CostUsd = cost,
        });

        return RunScorecardProjection.Apply(new RunScorecard(), new RunScorecardFacts
        {
            CompletedAt = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
            ProjectionKind = "supervisor",
            Score = score,
            BrainPlaneUsd = brain,
            BrainModel = brainModel,
            LessonArm = arm,
        });
    }

    [Theory]
    [InlineData(true, true, 0, LessonArms.Injected, true)]
    [InlineData(true, true, 0, LessonArms.Withheld, true)]
    [InlineData(true, true, 0, LessonArms.None, true)]
    [InlineData(true, true, 0, null, true)]
    [InlineData(true, true, 1, LessonArms.Injected, false)]   // a human was asked — not unattended, whatever the arm
    [InlineData(true, false, 0, LessonArms.Injected, false)]  // graded but never shipped
    [InlineData(false, true, 0, LessonArms.Injected, false)]  // shipped but never solved
    [InlineData(false, false, 3, LessonArms.Withheld, false)]
    public void The_row_carries_the_scorers_headline_bit_and_its_arm_verbatim(bool solved, bool delivered, int touches, string? arm, bool expectedHeadline)
    {
        var row = Project(solved, delivered, touches, arm);

        row.Solved.ShouldBe(solved);
        row.Delivered.ShouldBe(delivered);
        row.HumanTouches.ShouldBe(touches);
        row.LessonArm.ShouldBe(arm, "the arm is copied as-is — a blank/absent arm stays absent, it is not normalised into the 'none' control");
        row.UnattendedSolvedWithDelivery.ShouldBe(expectedHeadline, "the row must agree with the ONE scorer, never with a second predicate written here");
    }

    [Fact]
    public void Every_row_stamps_the_scorer_version_it_was_measured_under()
    {
        Project(solved: true, delivered: true, touches: 0, arm: LessonArms.None).ScorerVersion.ShouldBe(UnattendedDeliveryScorer.ScorerVersion);
    }

    [Fact]
    public void An_unpriceable_run_records_null_spend_rather_than_zero()
    {
        var row = Project(solved: true, delivered: true, touches: 0, arm: null, cost: null, brain: null);

        row.CostUsd.ShouldBeNull("nothing priceable is NOT a real $0 — the trend must not silently claim a free run");
        row.BrainPlaneUsd.ShouldBeNull();
    }

    [Fact]
    public void A_priced_run_records_both_planes_and_its_brain_model()
    {
        var row = Project(solved: true, delivered: true, touches: 0, arm: LessonArms.Injected, cost: 2.75m, brain: 0.40m, brainModel: "claude-sonnet-4-5");

        row.CostUsd.ShouldBe(2.75m);
        row.BrainPlaneUsd.ShouldBe(0.40m);
        row.BrainModel.ShouldBe("claude-sonnet-4-5", "which brain authored the decisions is half of any A/B claim about the arm");
    }

    [Fact]
    public void Re_projecting_an_existing_row_overwrites_it_in_place_rather_than_creating_a_second_opinion()
    {
        var row = new RunScorecard { Id = Guid.NewGuid(), WorkflowRunId = Guid.NewGuid(), Solved = true, Delivered = true, UnattendedSolvedWithDelivery = true, LessonArm = LessonArms.Injected };
        var id = row.Id;
        var runId = row.WorkflowRunId;

        var settled = UnattendedDeliveryScorer.Score(new UnattendedDeliveryRunOutcome { WorkflowRunId = runId, Solved = false, Delivered = true, HumanTouches = 2 });

        RunScorecardProjection.Apply(row, new RunScorecardFacts { CompletedAt = DateTimeOffset.UtcNow, Score = settled, LessonArm = LessonArms.Withheld });

        row.Id.ShouldBe(id, "identity is stable — the row is a projection of one run, not a history of opinions about it");
        row.WorkflowRunId.ShouldBe(runId);
        row.Solved.ShouldBeFalse();
        row.UnattendedSolvedWithDelivery.ShouldBeFalse();
        row.HumanTouches.ShouldBe(2);
        row.LessonArm.ShouldBe(LessonArms.Withheld);
    }
}
