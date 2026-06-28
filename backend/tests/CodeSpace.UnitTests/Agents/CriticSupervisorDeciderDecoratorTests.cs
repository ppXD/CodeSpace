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
/// (no recursion); GATE (v1) does NOT block — it falls through to the original decision; a FAILED review falls back to
/// the original (never worse than no review). Pure logic with a fake inner decider + fake critic — no DB / no model.
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
    public async Task Gate_does_not_block_or_re_decide_in_v1_and_keeps_the_original_decision()
    {
        var inner = new FakeDecider();
        var critic = new FakeCritic { Verdict = new CriticVerdict { Mode = ReviewMode.Gate, Approved = false, Score = 30, Issues = new[] { "premature stop" }, Rationale = "not done" } };
        var decorator = new CriticSupervisorDeciderDecorator(inner, critic);

        var decision = await decorator.DecideAsync(Context(ReviewMode.Gate), CancellationToken.None);

        inner.Contexts.Count.ShouldBe(1, "GATE does not re-decide");
        decision.Kind.ShouldBe(SupervisorDecisionKinds.Spawn, "v1 never blocks a decision mid-loop — the original stands");
    }

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
        public CriticVerdict Verdict { get; set; } = new() { Mode = ReviewMode.Gate };
        public CriticRequest? LastRequest { get; private set; }

        public Task<CriticVerdict> ReviewAsync(CriticRequest request, Guid teamId, Guid? reviewerModelId, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(Verdict);
        }
    }
}
