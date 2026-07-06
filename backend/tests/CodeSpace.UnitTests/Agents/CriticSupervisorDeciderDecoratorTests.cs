using CodeSpace.Core.Services.Review;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// The generic adversarial-review decorator over the supervisor decider: default-off (ReviewMode.None) is byte-identical
/// — a pure passthrough that never reviews; IMPROVE re-decides ONCE through the bare decider with the critique folded in
/// (no recursion); GATE is HARD (S8) — a disapproved decision does not execute: one re-decide against the critique, a
/// second independent review, and a still-disapproved decision ESCALATES to the human ask card; an answered escalation's
/// 'approve' is a ONE-SHOT positional absolution. A FAILED review falls back to the original (never worse than no
/// review). Pure logic with a fake inner decider + fake critic — no DB / no model.
/// </summary>
[Trait("Category", "Unit")]
public class CriticSupervisorDeciderDecoratorTests
{
    [Fact]
    public async Task None_uses_the_bare_decider_verbatim_and_never_reviews()
    {
        var inner = new FakeDecider();
        var critic = new FakeCritic();
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic, new NoAgentPlanReviewer());

        var decision = await decorator.DecideAsync(Context(ReviewMode.None), CancellationToken.None);

        inner.Contexts.Count.ShouldBe(1, "the bare decider is called once");
        critic.LastRequest.ShouldBeNull("ReviewMode.None never reviews — byte-identical to the bare decider");
        decision.Kind.ShouldBe(SupervisorDecisionKinds.Spawn);
    }

    [Fact]
    public async Task Improve_re_decides_once_through_the_bare_decider_with_the_critique_folded_in()
    {
        var inner = new FakeDecider();
        var critic = new FakeCritic { Verdict = new CriticVerdict { Mode = ReviewMode.Improve, Critique = "spawn fewer agents", Rationale = "over-fanned" } };
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic, new NoAgentPlanReviewer());

        await decorator.DecideAsync(Context(ReviewMode.Improve), CancellationToken.None);

        inner.Contexts.Count.ShouldBe(2, "IMPROVE re-decides exactly once");
        inner.Contexts[1].ReviewerCritique.ShouldBe("spawn fewer agents", "the critique is folded into the re-decide context");
        inner.Contexts[1].DecisionReviewMode.ShouldBe(ReviewMode.None, "the re-decide goes through the BARE decider — no recursion");

        critic.LastRequest.ShouldNotBeNull();
        critic.LastRequest!.ArtifactKind.ShouldBe("supervisor decision");
        critic.LastRequest.Artifact.ShouldContain(SupervisorDecisionKinds.Spawn, customMessage: "the decision verb + payload is what the critic judges");
        critic.LastRequest.Goal.ShouldContain("tests pass", customMessage: "the acceptance criteria ride into the reviewer's yardstick");
    }

    [Fact]
    public async Task Gate_approves_and_the_original_decision_stands_after_one_review()
    {
        var inner = new FakeDecider();
        var critic = new FakeCritic { Verdict = new CriticVerdict { Mode = ReviewMode.Gate, Approved = true, Rationale = "sound" } };
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic, new NoAgentPlanReviewer());

        var decision = await decorator.DecideAsync(Context(ReviewMode.Gate), CancellationToken.None);

        inner.Contexts.Count.ShouldBe(1);
        critic.Requests.Count.ShouldBe(1);
        decision.Kind.ShouldBe(SupervisorDecisionKinds.Spawn);
    }

    [Fact]
    public async Task A_disapproved_gate_re_decides_once_and_a_satisfied_second_review_ships_the_revision()
    {
        var inner = new FakeDecider();
        var critic = new FakeCritic();
        critic.Queue(Disapproved("over-fanned"), new CriticVerdict { Mode = ReviewMode.Gate, Approved = true, Rationale = "revision is sound" });
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic, new NoAgentPlanReviewer());

        var decision = await decorator.DecideAsync(Context(ReviewMode.Gate), CancellationToken.None);

        inner.Contexts.Count.ShouldBe(2, "the hard gate buys ONE re-decide against the critique");
        inner.Contexts[1].ReviewerCritique.ShouldContain("over-fanned", customMessage: "the evidence-attached critique steers the revision");
        inner.Contexts[1].DecisionReviewMode.ShouldBe(ReviewMode.None, "the re-decide goes through the BARE decider — no recursion");
        critic.Requests.Count.ShouldBe(2, "the REVISION earns its own independent review");
        decision.Kind.ShouldBe(SupervisorDecisionKinds.Spawn, "a satisfied second review ships the revised decision");
    }

    [Fact]
    public async Task A_twice_disapproved_gate_escalates_to_the_human_instead_of_executing()
    {
        var inner = new FakeDecider();
        var critic = new FakeCritic();
        critic.Queue(Disapproved("premature"), Disapproved("still premature"));
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic, new NoAgentPlanReviewer());

        var decision = await decorator.DecideAsync(Context(ReviewMode.Gate), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.AskHuman, "the blocked decision does NOT execute — the human is the tie-breaker");
        SupervisorGateEscalation.QuestionCarriesMarker(decision.PayloadJson).ShouldBeTrue("the card carries the gate's own marker");
        decision.PayloadJson.ShouldContain("still premature", customMessage: "the human rules on the CRITIQUE, not a mystery");
        decision.PayloadJson.ShouldContain("two subtasks remain unfinished", customMessage: "the evidence rides the card");
    }

    [Fact]
    public async Task An_approved_escalation_is_a_one_shot_absolution_that_skips_the_review()
    {
        var inner = new FakeDecider();
        var critic = new FakeCritic { Verdict = Disapproved("would block again") };
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic, new NoAgentPlanReviewer());

        var context = Context(ReviewMode.Gate) with { PriorDecisions = new[] { AnsweredEscalation("approve — proceed") } };
        var decision = await decorator.DecideAsync(context, CancellationToken.None);

        critic.Requests.ShouldBeEmpty("the human already ruled — this decision proceeds unreviewed (one-shot)");
        decision.Kind.ShouldBe(SupervisorDecisionKinds.Spawn);
    }

    [Fact]
    public async Task A_non_approve_escalation_answer_is_guidance_and_the_fresh_decision_is_still_reviewed()
    {
        var inner = new FakeDecider();
        var critic = new FakeCritic { Verdict = new CriticVerdict { Mode = ReviewMode.Gate, Approved = true, Rationale = "redirected fine" } };
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic, new NoAgentPlanReviewer());

        var context = Context(ReviewMode.Gate) with { PriorDecisions = new[] { AnsweredEscalation("split it into two smaller spawns") } };
        await decorator.DecideAsync(context, CancellationToken.None);

        critic.Requests.Count.ShouldBe(1, "guidance is not absolution — the redirected decision earns a fresh review");
    }

    [Fact]
    public async Task An_old_absolution_never_leaks_past_the_next_decision()
    {
        var inner = new FakeDecider();
        var critic = new FakeCritic { Verdict = new CriticVerdict { Mode = ReviewMode.Gate, Approved = true, Rationale = "ok" } };
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic, new NoAgentPlanReviewer());

        // The answered escalation is followed by a later decision — no longer the LATEST → no bypass.
        var priors = new[] { AnsweredEscalation("approve"), Prior(SupervisorDecisionKinds.Spawn) };
        await decorator.DecideAsync(Context(ReviewMode.Gate) with { PriorDecisions = priors }, CancellationToken.None);

        critic.Requests.Count.ShouldBe(1, "absolution is positional — only the immediately-following decision is absolved");
    }

    [Fact]
    public void The_gate_critique_folds_rationale_and_evidence() =>
        CriticSupervisorDeciderDecorator.ComposeGateCritique(Disapproved("weak"))
            .ShouldBe("weak Issues: premature stop (evidence: two subtasks remain unfinished)");

    [Fact]
    public void The_escalation_marker_is_pinned() =>
        SupervisorGateEscalation.EscalationMarker.ShouldBe("Reply 'approve' to proceed with this decision despite the review, or describe what to do instead.");

    private static CriticVerdict Disapproved(string rationale) => new()
    {
        Mode = ReviewMode.Gate,
        Approved = false,
        Score = 30,
        Issues = new[] { new CriticIssue { Text = "premature stop", Evidence = "two subtasks remain unfinished" } },
        Rationale = rationale,
    };

    private static SupervisorPriorDecision AnsweredEscalation(string answer)
    {
        var card = SupervisorGateEscalation.IntoAskHuman(new SupervisorDecision { Kind = SupervisorDecisionKinds.Spawn, PayloadJson = "{}" }, Disapproved("blocked"));

        return new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(),
            Sequence = 1,
            Status = SupervisorDecisionStatus.Succeeded,
            DecisionKind = SupervisorDecisionKinds.AskHuman,
            PayloadJson = card.PayloadJson,
            OutcomeJson = System.Text.Json.JsonSerializer.Serialize(new { answer }),
        };
    }

    private static SupervisorPriorDecision Prior(string kind) => new() { Id = Guid.NewGuid(), Sequence = 2, Status = SupervisorDecisionStatus.Succeeded, DecisionKind = kind, PayloadJson = "{}" };

    [Fact]
    public async Task A_failed_review_falls_back_to_the_original_decision()
    {
        var inner = new FakeDecider();
        var critic = new FakeCritic { Verdict = CriticVerdict.ReviewFailed(ReviewMode.Improve, "no reviewer model") };
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic, new NoAgentPlanReviewer());

        await decorator.DecideAsync(Context(ReviewMode.Improve), CancellationToken.None);

        inner.Contexts.Count.ShouldBe(1, "a failed review does NOT re-decide — fail-open to the original");
    }

    [Fact]
    public async Task An_improve_with_a_blank_critique_falls_back_to_the_original()
    {
        var inner = new FakeDecider();
        var critic = new FakeCritic { Verdict = new CriticVerdict { Mode = ReviewMode.Improve, Critique = "   ", Rationale = "ok" } };
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic, new NoAgentPlanReviewer());

        await decorator.DecideAsync(Context(ReviewMode.Improve), CancellationToken.None);

        inner.Contexts.Count.ShouldBe(1, "a blank critique gives nothing to revise against — keep the original");
    }

    private static SupervisorTurnContext Context(ReviewMode mode) => new()
    {
        Goal = "ship the feature",
        TeamId = Guid.NewGuid(),
        DecisionReviewMode = mode,
        AcceptanceCriteria = new[] { "tests pass" },
    };

    private sealed class FakeDecider : ISupervisorDecider
    {
        public string Kind { get; set; } = SupervisorDecisionKinds.Spawn;
        public List<SupervisorTurnContext> Contexts { get; } = new();

        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            Contexts.Add(context);
            return Task.FromResult(new SupervisorDecision { Kind = Kind, PayloadJson = "{\"agents\":[]}" });
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

    private sealed class FakeCritic : IStructuredCritic
    {
        private readonly Queue<CriticVerdict> _queued = new();

        public CriticVerdict Verdict { get; set; } = new() { Mode = ReviewMode.Gate };
        public List<CriticRequest> Requests { get; } = new();
        public CriticRequest? LastRequest => Requests.Count > 0 ? Requests[^1] : null;

        /// <summary>Queue per-call verdicts (the hard-Gate ladder reviews twice); an empty queue falls back to <see cref="Verdict"/>.</summary>
        public void Queue(params CriticVerdict[] verdicts) { foreach (var v in verdicts) _queued.Enqueue(v); }

        public Task<CriticVerdict> ReviewAsync(CriticRequest request, Guid teamId, Guid? reviewerModelId, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_queued.Count > 0 ? _queued.Dequeue() : Verdict);
        }
    }

    // ── S4e: the plan-scoped critic routing (the tier-generic "plan critic" on Deep) ──

    [Theory]
    [InlineData(SupervisorDecisionKinds.Plan, ReviewMode.Improve, ReviewMode.Gate, ReviewMode.Improve)]   // plan prefers the plan critic
    [InlineData(SupervisorDecisionKinds.Plan, ReviewMode.None, ReviewMode.Gate, ReviewMode.Gate)]         // no plan critic → falls under the decision critic
    [InlineData(SupervisorDecisionKinds.Spawn, ReviewMode.Improve, ReviewMode.Gate, ReviewMode.Gate)]     // non-plan NEVER uses the plan critic
    [InlineData(SupervisorDecisionKinds.Spawn, ReviewMode.Improve, ReviewMode.None, ReviewMode.None)]     // plan-critic-only run: spawns/merges/stops go unreviewed (no per-step cost)
    [InlineData(SupervisorDecisionKinds.Plan, ReviewMode.None, ReviewMode.None, ReviewMode.None)]
    public void A_plan_decision_prefers_the_plan_critic_and_non_plans_never_use_it(string kind, ReviewMode planMode, ReviewMode decisionMode, ReviewMode expected)
    {
        var context = new SupervisorTurnContext { Goal = "g", PlanReviewMode = planMode, DecisionReviewMode = decisionMode };
        var decision = new SupervisorDecision { Kind = kind, PayloadJson = "{}" };

        CriticSupervisorDeciderDecorator.ReviewModeFor(context, decision).ShouldBe(expected);
    }

    // ── D①: the grounded plan-review ladder on the supervisor tier — real agent first → model critic → fail-open ──

    [Fact]
    public async Task A_plan_decision_with_the_grounded_opt_in_reviews_via_the_real_agent_first()
    {
        var inner = new FakeDecider { Kind = SupervisorDecisionKinds.Plan };
        var critic = new FakeCritic();
        var agent = new RecordingAgentPlanReviewer { Verdict = new CriticVerdict { Mode = ReviewMode.Gate, Approved = true, Rationale = "grounded and sound" } };
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic, agent);
        var context = GroundedContext(ReviewMode.Gate, Guid.NewGuid());

        var decision = await decorator.DecideAsync(context, CancellationToken.None);

        agent.Requests.Count.ShouldBe(1, "a PLAN decision with the opt-in reviews GROUNDED first");
        critic.Requests.ShouldBeEmpty("a usable agent verdict means the model critic is never consulted");

        var sent = agent.Requests[0];
        sent.RepositoryId.ShouldBe(context.AgentProfile!.RepositoryId!.Value, "the run's authored repository is the ground");
        sent.TeamId.ShouldBe(context.TeamId);
        sent.WorkflowRunId.ShouldBe(context.SupervisorRunId, "the reviewer run lands on the supervisor run's cell");
        sent.NodeId.ShouldBe(context.NodeId);
        sent.ReviewerModelId.ShouldBe(context.ReviewerModelId);
        sent.Goal.ShouldContain("ship the feature", customMessage: "the goal is the yardstick");
        sent.Goal.ShouldContain("tests pass", customMessage: "the acceptance criteria ride the yardstick");
        sent.PlanArtifact.ShouldContain(SupervisorDecisionKinds.Plan, customMessage: "the rendered decision is the artifact under review");

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Plan, "an approved Gate returns the decision unchanged");
        inner.Contexts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_non_plan_decision_never_consults_the_plan_reviewer()
    {
        var inner = new FakeDecider();   // Spawn
        var critic = new FakeCritic { Verdict = new CriticVerdict { Mode = ReviewMode.Gate, Approved = true, Rationale = "fine" } };
        var agent = new RecordingAgentPlanReviewer();
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic, agent);
        var context = GroundedContext(ReviewMode.None, Guid.NewGuid()) with { DecisionReviewMode = ReviewMode.Gate };

        await decorator.DecideAsync(context, CancellationToken.None);

        agent.Requests.ShouldBeEmpty("grounding is a PLAN affordance — spawns/merges/stops never stage a repo-cloning agent");
        critic.Requests.Count.ShouldBe(1, "the ordinary decision critic still reviews");
    }

    [Fact]
    public async Task A_failed_grounded_review_ladders_down_to_the_model_critic()
    {
        var inner = new FakeDecider { Kind = SupervisorDecisionKinds.Plan };
        var critic = new FakeCritic { Verdict = new CriticVerdict { Mode = ReviewMode.Gate, Approved = true, Rationale = "fine" } };
        var agent = new RecordingAgentPlanReviewer();   // fixed: a failed review
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic, agent);

        await decorator.DecideAsync(GroundedContext(ReviewMode.Gate, Guid.NewGuid()), CancellationToken.None);

        agent.Requests.Count.ShouldBe(1, "the grounded review was attempted");
        critic.Requests.Count.ShouldBe(1, "a grounded review that can't produce a verdict ladders DOWN to the text review");
        critic.Requests[0].ArtifactKind.ShouldBe("workflow plan");
    }

    [Fact]
    public async Task A_grounded_disapproval_drives_the_hard_gate_ladder_to_escalation()
    {
        var inner = new FakeDecider { Kind = SupervisorDecisionKinds.Plan };
        var critic = new FakeCritic();
        var agent = new RecordingAgentPlanReviewer { Verdict = Disapproved("grounded: the plan schedules finished work") };
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic, agent);

        var result = await decorator.DecideAsync(GroundedContext(ReviewMode.Gate, Guid.NewGuid()), CancellationToken.None);

        inner.Contexts.Count.ShouldBe(2, "one bounded re-decide against the grounded critique");
        inner.Contexts[1].ReviewerCritique.ShouldBe("grounded: the plan schedules finished work Issues: premature stop (evidence: two subtasks remain unfinished)");
        agent.Requests.Count.ShouldBe(2, "the revision earns a fresh GROUNDED review — the second pass rides the same ladder");
        critic.Requests.ShouldBeEmpty("both passes produced agent verdicts — the model critic never ran");
        result.Kind.ShouldBe(SupervisorDecisionKinds.AskHuman, "a still-disapproved plan escalates to the human — the full ladder: agent-critic → self-revision → human");
    }

    [Fact]
    public async Task An_improve_plan_with_an_agent_disapproval_re_decides_on_the_composed_critique()
    {
        var inner = new FakeDecider { Kind = SupervisorDecisionKinds.Plan };
        var critic = new FakeCritic();
        var agent = new RecordingAgentPlanReviewer { Verdict = Disapproved("weak") };
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic, agent);

        await decorator.DecideAsync(GroundedContext(ReviewMode.Improve, Guid.NewGuid()), CancellationToken.None);

        inner.Contexts.Count.ShouldBe(2, "an agent disapproval under IMPROVE buys one re-decide");
        inner.Contexts[1].ReviewerCritique.ShouldBe("weak Issues: premature stop (evidence: two subtasks remain unfinished)",
            customMessage: "the agent's Gate-shaped verdict composes rationale + evidence-attached issues into the critique");
        inner.Contexts[1].PlanReviewMode.ShouldBe(ReviewMode.None, "the re-decide can never recurse into another review");
        critic.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_improve_plan_with_an_agent_approval_keeps_the_original_decision()
    {
        var inner = new FakeDecider { Kind = SupervisorDecisionKinds.Plan };
        var critic = new FakeCritic();
        var agent = new RecordingAgentPlanReviewer { Verdict = new CriticVerdict { Mode = ReviewMode.Gate, Approved = true, Rationale = "grounded and sound" } };
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic, agent);

        await decorator.DecideAsync(GroundedContext(ReviewMode.Improve, Guid.NewGuid()), CancellationToken.None);

        inner.Contexts.Count.ShouldBe(1, "an approval yields nothing to revise against — keep the original");
    }

    // ── H1: the draft→verdict→revision chain rides the surviving decision (the adversarial middle on the tape) ──

    [Fact]
    public async Task An_improve_revision_carries_the_model_verdict_with_the_drafts_attribution()
    {
        var inner = new FakeDecider();
        var critic = new FakeCritic { Verdict = new CriticVerdict { Mode = ReviewMode.Improve, Critique = "spawn fewer agents", Rationale = "over-fanned" } };
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic, new NoAgentPlanReviewer());

        var decision = await decorator.DecideAsync(Context(ReviewMode.Improve), CancellationToken.None);

        decision.Reviews.Count.ShouldBe(1, "the once-invisible middle now rides the tape");
        decision.Reviews[0].Approved.ShouldBeFalse();
        decision.Reviews[0].Rationale.ShouldBe("spawn fewer agents", "the Improve critique IS the review's content");
        decision.Reviews[0].DraftAttribution.ShouldNotBeNull("the discarded draft is attributed — no more anonymous model call");
        decision.Reviews[0].DraftAttribution!.ShouldContain("spawn draft");
    }

    [Fact]
    public async Task An_approved_decision_carries_the_approving_verdict()
    {
        var inner = new FakeDecider();
        var critic = new FakeCritic { Verdict = new CriticVerdict { Mode = ReviewMode.Gate, Approved = true, Rationale = "sound" } };
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic, new NoAgentPlanReviewer());

        var decision = await decorator.DecideAsync(Context(ReviewMode.Gate), CancellationToken.None);

        decision.Reviews.Count.ShouldBe(1);
        decision.Reviews[0].Approved.ShouldBeTrue();
        decision.Reviews[0].DraftAttribution.ShouldBeNull("nothing was discarded — an approval attributes no draft");
    }

    [Fact]
    public async Task The_gate_ladder_carries_both_rungs_and_the_escalation_inherits_the_chain()
    {
        var inner = new FakeDecider();
        var critic = new FakeCritic();
        critic.Queue(Disapproved("first"), Disapproved("second"));
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic, new NoAgentPlanReviewer());

        var result = await decorator.DecideAsync(Context(ReviewMode.Gate), CancellationToken.None);

        result.Kind.ShouldBe(SupervisorDecisionKinds.AskHuman, "still-disapproved escalates");
        result.Reviews.Count.ShouldBe(2, "the parked ask card tells the WHOLE ladder — first flagged + still-flagged revision");
        result.Reviews[0].Rationale.ShouldContain("first");
        result.Reviews[0].DraftAttribution.ShouldNotBeNull("the first rung discarded the draft");
        result.Reviews[1].Rationale.ShouldContain("second");
        result.Reviews[1].DraftAttribution.ShouldBeNull();
    }

    [Fact]
    public async Task An_agent_verdict_is_never_folded_onto_the_decision()
    {
        // The grounded reviewer's run is ALREADY a first-class journal beat — folding its verdict here would double it.
        var inner = new FakeDecider { Kind = SupervisorDecisionKinds.Plan };
        var critic = new FakeCritic();
        var agent = new RecordingAgentPlanReviewer { Verdict = new CriticVerdict { Mode = ReviewMode.Gate, Approved = true, Rationale = "grounded ok" } };
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic, agent);

        var decision = await decorator.DecideAsync(GroundedContext(ReviewMode.Gate, Guid.NewGuid()), CancellationToken.None);

        decision.Reviews.ShouldBeEmpty("an agent verdict rides its reviewer run's own beat, not the decision outcome");
    }

    [Fact]
    public void The_reviews_fold_round_trips_and_an_empty_chain_is_byte_identical()
    {
        var reviews = new[]
        {
            new SupervisorDecisionReview { Approved = false, Rationale = "thin", Issues = new[] { "no tests (evidence: none named)" }, Scope = "plan", DraftAttribution = "plan draft · authored via m1 · 8,200 tokens" },
            new SupervisorDecisionReview { Approved = true, Rationale = "fixed", Scope = "plan" },
        };

        var outcome = SupervisorOutcome.WriteReviews("""{"outcome":"planned"}""", reviews);
        var read = SupervisorOutcome.ReadReviews(outcome);

        read.Count.ShouldBe(2);
        read[0].Approved.ShouldBeFalse();
        read[0].Rationale.ShouldBe("thin");
        read[0].Issues.ShouldBe(new[] { "no tests (evidence: none named)" });
        read[0].Scope.ShouldBe("plan");
        read[0].DraftAttribution.ShouldBe("plan draft · authored via m1 · 8,200 tokens");
        read[1].Approved.ShouldBeTrue();

        SupervisorOutcome.WriteReviews("""{"outcome":"planned"}""", Array.Empty<SupervisorDecisionReview>())
            .ShouldBe("""{"outcome":"planned"}""", "no reviews ⇒ byte-identical — every pre-chain decision replays exactly as before");
        SupervisorOutcome.ReadReviews("""{"outcome":"planned"}""").ShouldBeEmpty();
    }

    [Fact]
    public void The_draft_attribution_names_the_verb_and_the_authoring_call()
    {
        CriticSupervisorDeciderDecorator.DescribeDraft(new SupervisorDecision { Kind = SupervisorDecisionKinds.Plan, PayloadJson = "{}", Usage = new SupervisorModelUsage { Model = "metis-coder-max", InputTokens = 8000, OutputTokens = 231 } })
            .ShouldBe("plan draft · authored via metis-coder-max · 8,231 tokens", "the once-anonymous model call reads as the flagged draft");

        CriticSupervisorDeciderDecorator.DescribeDraft(new SupervisorDecision { Kind = SupervisorDecisionKinds.Spawn, PayloadJson = "{}" })
            .ShouldBe("spawn draft", "no usage ⇒ verb only");
    }

    private static SupervisorTurnContext GroundedContext(ReviewMode planMode, Guid repositoryId) => new()
    {
        Goal = "ship the feature",
        TeamId = Guid.NewGuid(),
        SupervisorRunId = Guid.NewGuid(),
        NodeId = "supervisor",
        PlanReviewMode = planMode,
        ReviewerAgent = true,
        ReviewerModelId = Guid.NewGuid(),
        AgentProfile = new CodeSpace.Messages.Dtos.Agents.SupervisorAgentProfile { RepositoryId = repositoryId },
        AcceptanceCriteria = new[] { "tests pass" },
    };
}
