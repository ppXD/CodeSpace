using CodeSpace.Core.Services.Review;
using CodeSpace.Core.Services.Workflows.Planning;
using CodeSpace.Core.Services.Workflows.Planning.Planners;
using CodeSpace.Messages.Dtos.Workflows.Planning;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The generic adversarial-review decorator over the planner: default-off is byte-identical; IMPROVE re-plans ONCE
/// through the bare planner with the critique folded in (no recursion); GATE annotates the plan's risks without
/// discarding it; a FAILED review falls back to the original plan (never worse than no review). Pure logic with a fake
/// inner planner + fake critic — no DB / no model.
/// </summary>
[Trait("Category", "Unit")]
public class CriticPlannerDecoratorTests
{
    [Fact]
    public async Task None_uses_the_bare_planner_verbatim_and_never_reviews()
    {
        var planner = new FakePlanner();
        var critic = new FakeCritic();
        var decorator = new CriticPlannerDecorator(planner, critic);

        var result = await decorator.PlanAsync(Request(ReviewMode.None), CancellationToken.None);

        planner.Requests.Count.ShouldBe(1, "the bare planner is called once");
        critic.LastRequest.ShouldBeNull("ReviewMode.None never reviews — byte-identical to the bare planner");
        result.Goal.ShouldBe("g");
    }

    [Fact]
    public async Task Improve_re_plans_once_through_the_bare_planner_with_the_critique_folded_in()
    {
        var planner = new FakePlanner();
        var critic = new FakeCritic { Verdict = new CriticVerdict { Mode = ReviewMode.Improve, Critique = "add a test step", Rationale = "thin" } };
        var decorator = new CriticPlannerDecorator(planner, critic);

        await decorator.PlanAsync(Request(ReviewMode.Improve), CancellationToken.None);

        planner.Requests.Count.ShouldBe(2, "IMPROVE re-plans exactly once");
        planner.Requests[1].ReviewerCritique.ShouldBe("add a test step", "the critique is folded into the re-plan request");
        planner.Requests[1].Review.ShouldBe(ReviewMode.None, "the re-plan goes through the BARE planner — no recursion");
    }

    [Fact]
    public async Task Gate_annotates_the_plan_risks_without_re_planning_or_discarding()
    {
        var planner = new FakePlanner();
        var critic = new FakeCritic { Verdict = new CriticVerdict { Mode = ReviewMode.Gate, Approved = false, Score = 40, Issues = new[] { "no tests named" }, Rationale = "too thin" } };
        var decorator = new CriticPlannerDecorator(planner, critic);

        var result = await decorator.PlanAsync(Request(ReviewMode.Gate), CancellationToken.None);

        planner.Requests.Count.ShouldBe(1, "GATE does not re-plan");
        result.Subtasks.Count.ShouldBe(1, "the usable plan is kept, never discarded");
        result.Risks.ShouldContain(r => r.Contains("no tests named"), "the reviewer's issues surface as risks");
        result.Risks.ShouldContain(r => r.Contains("flagged concerns") && r.Contains("40"), "the verdict + score is surfaced for the human reviewer");
    }

    [Fact]
    public async Task A_failed_review_falls_back_to_the_original_plan()
    {
        var planner = new FakePlanner();
        var critic = new FakeCritic { Verdict = CriticVerdict.ReviewFailed(ReviewMode.Improve, "no reviewer model") };
        var decorator = new CriticPlannerDecorator(planner, critic);

        var result = await decorator.PlanAsync(Request(ReviewMode.Improve), CancellationToken.None);

        planner.Requests.Count.ShouldBe(1, "a failed review does NOT re-plan — fail-open to the original");
        result.Risks.ShouldBeEmpty("a failed review leaves the plan untouched — never worse than no review");
    }

    [Fact]
    public async Task An_improve_with_a_blank_critique_falls_back_to_the_original()
    {
        var planner = new FakePlanner();
        var critic = new FakeCritic { Verdict = new CriticVerdict { Mode = ReviewMode.Improve, Critique = "   ", Rationale = "ok" } };
        var decorator = new CriticPlannerDecorator(planner, critic);

        await decorator.PlanAsync(Request(ReviewMode.Improve), CancellationToken.None);

        planner.Requests.Count.ShouldBe(1, "a blank critique gives nothing to revise against — keep the original");
    }

    private static WorkflowPlanRequest Request(ReviewMode mode) => new() { TaskText = "do x", TeamId = Guid.NewGuid(), Review = mode };

    private sealed class FakePlanner : IWorkflowPlanner
    {
        public List<WorkflowPlanRequest> Requests { get; } = new();

        public Task<PlannedWorkflow> PlanAsync(WorkflowPlanRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new PlannedWorkflow { Goal = "g", Subtasks = new[] { new PlannedSubtask { Id = "1", Title = "t", Instruction = "i" } } });
        }
    }

    private sealed class FakeCritic : IStructuredCritic
    {
        public CriticVerdict Verdict { get; set; } = new() { Mode = ReviewMode.Gate };
        public CriticRequest? LastRequest { get; private set; }

        public Task<CriticVerdict> ReviewAsync(CriticRequest request, Guid teamId, Guid? reviewerModelId, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(Verdict);
        }
    }
}
