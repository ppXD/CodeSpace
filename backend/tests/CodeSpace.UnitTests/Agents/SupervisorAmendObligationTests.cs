using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: B5's retry-after-amend obligation (MAJOR-5) — an APPROVED spec amendment commits the run to re-grade
/// its target under the co-signed oracle. Pins the walk: outstanding while no staging decision follows the card;
/// consumed by ANY later staging of the target (the new attempt folds under the overlay's effective oracle,
/// whatever its outcome); a waive owes nothing; an unapproved card binds nothing (the same
/// <see cref="SupervisorAmendAcceptance.IsApprovedAmendCard"/> authority the overlay applies); a re-plan
/// invalidates the amendment and with it the obligation (MAJOR-8).
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorAmendObligationTests
{
    private static SupervisorPriorDecision Plan(long seq) =>
        new() { Id = Guid.NewGuid(), Sequence = seq, Status = SupervisorDecisionStatus.Succeeded, DecisionKind = SupervisorDecisionKinds.Plan, PayloadJson = "{}", OutcomeJson = "{}" };

    private static SupervisorPriorDecision Card(long seq, string subtaskId, bool waive, string? answer)
    {
        var card = SupervisorAmendAcceptance.IntoAskHuman(new SupervisorAmendAcceptancePayload
        {
            SubtaskId = subtaskId, Waive = waive, Reason = "r",
            Acceptance = waive ? null : new SupervisorAcceptanceSpec { Command = new[] { "sh", "verify.sh" } },
        });
        var outcome = answer is null ? "{}" : JsonSerializer.Serialize(new { question = "q", answer }, AgentJson.Options);

        return new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = seq, Status = SupervisorDecisionStatus.Succeeded, DecisionKind = SupervisorDecisionKinds.AskHuman, PayloadJson = card.PayloadJson, OutcomeJson = outcome };
    }

    private static SupervisorPriorDecision Spawn(long seq, params string[] subtaskIds) =>
        new() { Id = Guid.NewGuid(), Sequence = seq, Status = SupervisorDecisionStatus.Succeeded, DecisionKind = SupervisorDecisionKinds.Spawn, PayloadJson = JsonSerializer.Serialize(new { subtaskIds }, AgentJson.Options), OutcomeJson = "{}" };

    private static SupervisorPriorDecision Retry(long seq, string subtaskId) =>
        new() { Id = Guid.NewGuid(), Sequence = seq, Status = SupervisorDecisionStatus.Succeeded, DecisionKind = SupervisorDecisionKinds.Retry, PayloadJson = JsonSerializer.Serialize(new { subtaskId }, AgentJson.Options), OutcomeJson = "{}" };

    private static SupervisorTurnContext Context(params SupervisorPriorDecision[] prior) => new() { Goal = "g", PriorDecisions = prior };

    [Fact]
    public void An_approved_replacement_with_no_later_attempt_is_outstanding()
    {
        var ctx = Context(Plan(1), Spawn(2, "s1"), Card(3, "s1", waive: false, answer: "approve"));

        SupervisorAmendObligation.FirstOutstanding(ctx).ShouldBe("s1");
        SupervisorAmendObligation.IsOutstanding(ctx, "s1").ShouldBeTrue();
    }

    [Fact]
    public void A_staging_after_the_card_consumes_the_obligation_whatever_its_outcome()
    {
        SupervisorAmendObligation.FirstOutstanding(Context(Plan(1), Spawn(2, "s1"), Card(3, "s1", waive: false, answer: "approve"), Retry(4, "s1")))
            .ShouldBeNull("the retry folds its grade under the overlay's effective oracle — the obligation is consumed at staging");

        SupervisorAmendObligation.FirstOutstanding(Context(Plan(1), Spawn(2, "s1"), Card(3, "s1", waive: false, answer: "approve"), Spawn(4, "s1", "s2")))
            .ShouldBeNull("a spawn naming the target consumes it too");
    }

    [Fact]
    public void A_waive_owes_nothing()
    {
        SupervisorAmendObligation.FirstOutstanding(Context(Plan(1), Spawn(2, "s1"), Card(3, "s1", waive: true, answer: "approve")))
            .ShouldBeNull("a waived unit settles as Waived at the next fold — no retry is involved");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("reject")]
    [InlineData("keep the original check")]
    public void An_unapproved_card_binds_nothing(string? answer)
    {
        SupervisorAmendObligation.FirstOutstanding(Context(Plan(1), Spawn(2, "s1"), Card(3, "s1", waive: false, answer)))
            .ShouldBeNull("only the card's OWN approving answer carries authority — the overlay's exact rule");
    }

    [Fact]
    public void A_re_plan_invalidates_the_obligation()
    {
        SupervisorAmendObligation.FirstOutstanding(Context(Plan(1), Spawn(2, "s1"), Card(3, "s1", waive: false, answer: "approve"), Plan(4)))
            .ShouldBeNull("MAJOR-8: the amendment died with its plan; so did the retry it implied");
    }

    [Fact]
    public void Obligations_resolve_in_sequence_order_and_per_subtask()
    {
        var ctx = Context(Plan(1), Spawn(2, "s1", "s2"),
            Card(3, "s2", waive: false, answer: "approve"),
            Card(4, "s1", waive: false, answer: "approve"),
            Retry(5, "s2"));

        SupervisorAmendObligation.FirstOutstanding(ctx).ShouldBe("s1", "s2's obligation was consumed by its retry; s1's still stands");
        SupervisorAmendObligation.IsOutstanding(ctx, "s2").ShouldBeFalse();
        SupervisorAmendObligation.IsOutstanding(ctx, null).ShouldBeFalse();
    }
}
