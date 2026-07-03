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
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic);

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
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic);

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
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic);

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
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic);

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
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic);

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
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic);

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
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic);

        var context = Context(ReviewMode.Gate) with { PriorDecisions = new[] { AnsweredEscalation("split it into two smaller spawns") } };
        await decorator.DecideAsync(context, CancellationToken.None);

        critic.Requests.Count.ShouldBe(1, "guidance is not absolution — the redirected decision earns a fresh review");
    }

    [Fact]
    public async Task An_old_absolution_never_leaks_past_the_next_decision()
    {
        var inner = new FakeDecider();
        var critic = new FakeCritic { Verdict = new CriticVerdict { Mode = ReviewMode.Gate, Approved = true, Rationale = "ok" } };
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic);

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
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic);

        await decorator.DecideAsync(Context(ReviewMode.Improve), CancellationToken.None);

        inner.Contexts.Count.ShouldBe(1, "a failed review does NOT re-decide — fail-open to the original");
    }

    [Fact]
    public async Task An_improve_with_a_blank_critique_falls_back_to_the_original()
    {
        var inner = new FakeDecider();
        var critic = new FakeCritic { Verdict = new CriticVerdict { Mode = ReviewMode.Improve, Critique = "   ", Rationale = "ok" } };
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic);

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
        public List<SupervisorTurnContext> Contexts { get; } = new();

        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            Contexts.Add(context);
            return Task.FromResult(new SupervisorDecision { Kind = SupervisorDecisionKinds.Spawn, PayloadJson = "{\"agents\":[]}" });
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
}
