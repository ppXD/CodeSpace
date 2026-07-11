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
        var decorator = new CriticPlannerDecorator(planner, critic, new NoAgentPlanReviewer());

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
        var decorator = new CriticPlannerDecorator(planner, critic, new NoAgentPlanReviewer());

        await decorator.PlanAsync(Request(ReviewMode.Improve), CancellationToken.None);

        planner.Requests.Count.ShouldBe(2, "IMPROVE re-plans exactly once");
        planner.Requests[1].ReviewerCritique.ShouldBe("add a test step", "the critique is folded into the re-plan request");
        planner.Requests[1].Review.ShouldBe(ReviewMode.None, "the re-plan goes through the BARE planner — no recursion");
    }

    [Fact]
    public async Task The_producer_model_row_reaches_the_critic_for_the_distinct_first_reviewer_pick()
    {
        var producerRow = Guid.NewGuid();
        var planner = new FakePlanner();
        var critic = new FakeCritic { Verdict = new CriticVerdict { Mode = ReviewMode.Gate, Approved = true } };
        var decorator = new CriticPlannerDecorator(planner, critic, new NoAgentPlanReviewer());

        await decorator.PlanAsync(new WorkflowPlanRequest { TaskText = "t", TeamId = Guid.NewGuid(), Review = ReviewMode.Gate, BrainModelId = producerRow }, CancellationToken.None);

        critic.LastRequest!.ProducerModelRowId.ShouldBe(producerRow,
            customMessage: "the reviewer's distinct-first ladder needs the producer's row — dropping it silently reverts to a same-model-possible pick");
    }

    [Fact]
    public async Task Gate_annotates_the_plan_risks_without_re_planning_or_discarding()
    {
        var planner = new FakePlanner();
        var critic = new FakeCritic { Verdict = new CriticVerdict { Mode = ReviewMode.Gate, Approved = false, Score = 40, Issues = new[] { new CriticIssue { Text = "no tests named", Evidence = "no subtask carries a test command" } }, Rationale = "too thin" } };
        var decorator = new CriticPlannerDecorator(planner, critic, new NoAgentPlanReviewer());

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
        var decorator = new CriticPlannerDecorator(planner, critic, new NoAgentPlanReviewer());

        var result = await decorator.PlanAsync(Request(ReviewMode.Improve), CancellationToken.None);

        planner.Requests.Count.ShouldBe(1, "a failed review does NOT re-plan — fail-open to the original");
        result.Risks.ShouldBeEmpty("a failed review leaves the plan untouched — never worse than no review");
    }

    [Fact]
    public async Task An_improve_with_a_blank_critique_falls_back_to_the_original()
    {
        var planner = new FakePlanner();
        var critic = new FakeCritic { Verdict = new CriticVerdict { Mode = ReviewMode.Improve, Critique = "   ", Rationale = "ok" } };
        var decorator = new CriticPlannerDecorator(planner, critic, new NoAgentPlanReviewer());

        await decorator.PlanAsync(Request(ReviewMode.Improve), CancellationToken.None);

        planner.Requests.Count.ShouldBe(1, "a blank critique gives nothing to revise against — keep the original");
    }

    // ── D①: the grounded plan-review ladder — real agent first → model critic → fail-open ──

    [Fact]
    public async Task The_grounded_opt_in_reviews_via_the_real_agent_and_skips_the_model_critic()
    {
        var planner = new FakePlanner();
        var critic = new FakeCritic();
        var agent = new RecordingAgentPlanReviewer { Verdict = new CriticVerdict { Mode = ReviewMode.Gate, Approved = false, Rationale = "assumption broken", Issues = new[] { new CriticIssue { Text = "the plan presumes a test project", Evidence = "no *Tests.csproj exists in the tree" } } } };
        var decorator = new CriticPlannerDecorator(planner, critic, agent);
        var request = GroundedRequest(ReviewMode.Gate, Guid.NewGuid());

        var result = await decorator.PlanAsync(request, CancellationToken.None);

        agent.Requests.Count.ShouldBe(1, "the grounded review runs FIRST — a real agent against the real tree beats a text impression");
        critic.LastRequest.ShouldBeNull("a usable agent verdict means the model critic is never consulted");

        var sent = agent.Requests[0];
        sent.RepositoryId.ShouldBe(request.RepositoryId!.Value);
        sent.TeamId.ShouldBe(request.TeamId);
        sent.WorkflowRunId.ShouldBe(request.WorkflowRunId, "the reviewer run lands on the plan node's cell");
        sent.NodeId.ShouldBe(request.NodeId);
        sent.ReviewerModelId.ShouldBe(request.ReviewerModelId, "the operator's reviewer model pin drives the reviewer agent");
        sent.Goal.ShouldBe("do x", "the task text is the reviewer's yardstick");
        sent.PlanArtifact.ShouldContain("- t: i", customMessage: "the rendered plan is the artifact under review");
        sent.PinnedSha.ShouldBe(request.PinnedSha, "S1: the launch's immutable base pin reaches the reviewer — it must judge the plan against the SAME tree the executing agents materialize");

        result.Risks.ShouldContain(r => r.Contains("no *Tests.csproj exists"), "the agent's EVIDENCE surfaces on the annotated risks");
        result.Risks.ShouldContain(r => r.Contains("flagged concerns"), "the agent's Gate-shaped verdict drives the same annotation a model verdict would");
    }

    [Fact]
    public async Task The_grounded_opt_in_without_a_repository_ladders_straight_to_the_model_critic()
    {
        var planner = new FakePlanner();
        var critic = new FakeCritic { Verdict = new CriticVerdict { Mode = ReviewMode.Gate, Approved = true, Rationale = "fine" } };
        var agent = new RecordingAgentPlanReviewer();
        var decorator = new CriticPlannerDecorator(planner, critic, agent);

        await decorator.PlanAsync(GroundedRequest(ReviewMode.Gate, repositoryId: null), CancellationToken.None);

        agent.Requests.ShouldBeEmpty("no repository ⇒ nothing to ground against — the agent is never staged");
        critic.LastRequest.ShouldNotBeNull("the model text review still runs");
    }

    [Fact]
    public async Task A_failed_grounded_review_ladders_down_to_the_model_critic()
    {
        var planner = new FakePlanner();
        var critic = new FakeCritic { Verdict = new CriticVerdict { Mode = ReviewMode.Gate, Approved = true, Rationale = "fine" } };
        var agent = new RecordingAgentPlanReviewer { Verdict = CriticVerdict.ReviewFailed(ReviewMode.Gate, "agent-reviewer: staging failed") };
        var decorator = new CriticPlannerDecorator(planner, critic, agent);

        var result = await decorator.PlanAsync(GroundedRequest(ReviewMode.Gate, Guid.NewGuid()), CancellationToken.None);

        agent.Requests.Count.ShouldBe(1, "the grounded review was attempted");
        critic.LastRequest.ShouldNotBeNull("a grounded review that can't produce a verdict ladders DOWN to the text review — never worse than no review");
        result.Risks.ShouldContain(r => r.Contains("approved"), "the model verdict annotates as usual");
    }

    [Fact]
    public async Task Improve_with_an_agent_disapproval_re_plans_on_the_composed_critique_with_the_recursion_pin()
    {
        var planner = new FakePlanner();
        var critic = new FakeCritic();
        var agent = new RecordingAgentPlanReviewer { Verdict = new CriticVerdict { Mode = ReviewMode.Gate, Approved = false, Rationale = "thin", Issues = new[] { new CriticIssue { Text = "no test step", Evidence = "no subtask names a test command" } } } };
        var decorator = new CriticPlannerDecorator(planner, critic, agent);

        await decorator.PlanAsync(GroundedRequest(ReviewMode.Improve, Guid.NewGuid()), CancellationToken.None);

        planner.Requests.Count.ShouldBe(2, "an agent disapproval under IMPROVE buys one re-plan");
        planner.Requests[1].ReviewerCritique.ShouldBe("thin Issues: no test step (evidence: no subtask names a test command)",
            customMessage: "the agent's Gate-shaped verdict composes rationale + evidence-attached issues into the critique");
        planner.Requests[1].Review.ShouldBe(ReviewMode.None, "the re-plan goes through the BARE planner — no recursion");
        planner.Requests[1].ReviewerAgent.ShouldBeFalse("the re-plan never re-stages a grounded review — the recursion pin");
    }

    [Fact]
    public async Task An_agent_approval_under_improve_keeps_the_original_plan()
    {
        var planner = new FakePlanner();
        var critic = new FakeCritic();
        var agent = new RecordingAgentPlanReviewer { Verdict = new CriticVerdict { Mode = ReviewMode.Gate, Approved = true, Rationale = "grounded and sound" } };
        var decorator = new CriticPlannerDecorator(planner, critic, agent);

        await decorator.PlanAsync(GroundedRequest(ReviewMode.Improve, Guid.NewGuid()), CancellationToken.None);

        planner.Requests.Count.ShouldBe(1, "an approval yields nothing to revise against — keep the original");
    }

    [Fact]
    public void The_effective_critique_prefers_the_improve_critique_and_composes_only_a_gate_disapproval()
    {
        var issue = new CriticIssue { Text = "no test step", Evidence = "no subtask names a test command" };

        CriticPlannerDecorator.EffectiveCritique(new CriticVerdict { Mode = ReviewMode.Improve, Critique = "add tests", Rationale = "r" })
            .ShouldBe("add tests", "an explicit critique always wins");
        CriticPlannerDecorator.EffectiveCritique(new CriticVerdict { Mode = ReviewMode.Gate, Approved = false, Rationale = "thin", Issues = new[] { issue } })
            .ShouldBe("thin Issues: no test step (evidence: no subtask names a test command)");
        CriticPlannerDecorator.EffectiveCritique(new CriticVerdict { Mode = ReviewMode.Gate, Approved = false, Rationale = "thin" })
            .ShouldBe("thin", "no issues ⇒ the rationale alone");
        CriticPlannerDecorator.EffectiveCritique(new CriticVerdict { Mode = ReviewMode.Gate, Approved = true, Rationale = "ok" })
            .ShouldBeNull("an approval yields nothing to revise against");
        CriticPlannerDecorator.EffectiveCritique(new CriticVerdict { Mode = ReviewMode.Improve, Critique = "   ", Rationale = "ok" })
            .ShouldBeNull("a blank Improve critique stays blank — the Gate compose never applies to an Improve verdict");
    }

    private static WorkflowPlanRequest Request(ReviewMode mode) => new() { TaskText = "do x", TeamId = Guid.NewGuid(), Review = mode };

    private static WorkflowPlanRequest GroundedRequest(ReviewMode mode, Guid? repositoryId) => new()
    {
        TaskText = "do x",
        TeamId = Guid.NewGuid(),
        Review = mode,
        ReviewerAgent = true,
        RepositoryId = repositoryId,
        WorkflowRunId = Guid.NewGuid(),
        NodeId = "plan-1",
        ReviewerModelId = Guid.NewGuid(),
        PinnedSha = "abc123def456",   // S1: the launch's immutable base pin rides the plan request into the reviewer
    };

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

    /// <summary>No grounded review requested — every call ladders straight to the model critic (the unit tier's default).</summary>
    private sealed class NoAgentPlanReviewer : CodeSpace.Core.Services.Agents.Review.IAgentPlanReviewer
    {
        public Task<CriticVerdict> ReviewAsync(CodeSpace.Core.Services.Agents.Review.PlanReviewRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(CriticVerdict.ReviewFailed(ReviewMode.Gate, "agent-reviewer: not requested"));
    }

    /// <summary>The D① grounded reviewer: records every request, answers a fixed verdict (default: a failed review, i.e. the ladder falls through).</summary>
    private sealed class RecordingAgentPlanReviewer : CodeSpace.Core.Services.Agents.Review.IAgentPlanReviewer
    {
        public CriticVerdict Verdict { get; set; } = CriticVerdict.ReviewFailed(ReviewMode.Gate, "agent-reviewer: staging failed");
        public List<CodeSpace.Core.Services.Agents.Review.PlanReviewRequest> Requests { get; } = new();

        public Task<CriticVerdict> ReviewAsync(CodeSpace.Core.Services.Agents.Review.PlanReviewRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Verdict);
        }
    }
}
