using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Quality;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: pins P22-9b's QUALITY POLICY block — the pure render only. The policy's own reasoning is pinned by
/// <c>QualityPolicyTests</c> and the grain it reads by <c>SupervisorQualityFactsTests</c>; what is pinned HERE is
/// the one thing neither of those can see: whether the recommendation reaches the model as a RECOMMENDATION.
/// Three properties carry that — the header says it may be rejected, every mechanism is translated into a verb this
/// lane actually has (the enum is the policy's vocabulary, not the supervisor's), and the policy's own reason is
/// recited verbatim so the model has the evidence it would need in order to reject the line.
/// <para>The prompt PLACEMENT (after the run bounds, before the verb roster) is pinned in
/// <c>SupervisorDeciderTests</c>, against the real assembled prompt rather than against this render.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class SupervisorQualityRecitationTests
{
    [Fact]
    public void Nothing_attempted_renders_nothing() =>
        SupervisorQualityRecitation.Render(Array.Empty<SupervisorUnitQualityDecision>())
            .ShouldBeNull("the fold emits no reading for an un-attempted unit → a pre-spawn turn's prompt stays byte-identical");

    [Fact]
    public void The_header_is_pinned_and_says_the_recommendation_may_be_rejected()
    {
        // The header is the ONLY place the block's non-binding status is stated, and the block is placed one screen
        // above the verb roster — so a header that read as an instruction would present a mechanism as the turn's
        // decision, which is exactly the authority this block must not have. Pinned as a literal: a reword that
        // drops "may reject" is a change of meaning, not of prose.
        SupervisorQualityRecitation.Header.ShouldBe(
            "QUALITY POLICY (per unit, derived from recorded evidence — a recommendation you may reject by naming the evidence it missed):");

        SupervisorQualityRecitation.Render(new[] { Reading("s1", QualityMechanism.SingleAgent, "no blocking evidence") })
            .ShouldStartWith(SupervisorQualityRecitation.Header, Case.Sensitive);
    }

    [Fact]
    public void One_line_per_unit_in_the_folds_own_order()
    {
        var block = SupervisorQualityRecitation.Render(new[]
        {
            Reading("s1", QualityMechanism.EscalateModel, "the check failed twice in a row on a localized diff"),
            Reading("s2", QualityMechanism.BoundedRepair, "the declared check never ran"),
        });

        var lines = block!.Split('\n');

        lines.Length.ShouldBe(3, "the header plus exactly one line per unit — a second line for one unit buries the units beside it");
        lines[1].ShouldBe("- [s1] EscalateModel → retry (stronger model) — the check failed twice in a row on a localized diff");
        lines[2].ShouldStartWith("- [s2] BoundedRepair → ");
    }

    [Fact]
    public void The_policys_own_reason_is_recited_verbatim()
    {
        // Verbatim because the reason IS the evidence: the header invites the model to reject the recommendation by
        // naming the evidence it missed, and a re-worded summary is not something a rejection can be checked against.
        const string reason = "2 consecutive failed verdicts on 3 changed files across 1 workspace unit";

        SupervisorQualityRecitation.Render(new[] { Reading("s1", QualityMechanism.EscalateModel, reason) })!
            .ShouldContain(reason, Case.Sensitive);
    }

    [Theory]
    [InlineData(QualityMechanism.EscalateModel, "retry (stronger model)")]
    [InlineData(QualityMechanism.SplitIntoSubtasks, "plan/replan")]
    [InlineData(QualityMechanism.BoundedRepair, "retry (same model; the machinery failed, not the work)")]
    [InlineData(QualityMechanism.AskHuman, "ask_human")]
    [InlineData(QualityMechanism.Stop, "close")]
    [InlineData(QualityMechanism.SingleAgent, "retry")]
    public void Each_mechanism_is_steered_to_a_verb_this_lane_actually_has(QualityMechanism mechanism, string expectedSteer) =>
        SupervisorQualityRecitation.Render(new[] { Reading("s1", mechanism, "because the evidence said so") })!
            .ShouldContain($"{mechanism} → {expectedSteer} —", Case.Sensitive);

    [Fact]
    public void The_ungradable_mechanism_is_a_statement_of_absence_and_offers_no_verb()
    {
        // IndependentCritic is the one mechanism this lane cannot spend on — no per-unit work review exists
        // (SupervisorQualityFacts.NoWorkReviewIsRecorded), so inventing "spawn a reviewer" would point the model at
        // a move the run cannot make, on a turn whose roster does not offer it either.
        var line = SupervisorQualityRecitation.Render(new[] { Reading("s1", QualityMechanism.IndependentCritic, "no objective check can grade this work") })!;

        line.ShouldContain("ungraded; nothing recorded can grade this", Case.Sensitive);
        line.ShouldNotContain("spawn", Case.Insensitive, "a steer must never name a move this lane cannot actually spend on");
    }

    [Fact]
    public void Every_mechanism_the_enum_has_is_mapped()
    {
        // A new mechanism (Rule 7: a new member plus a new ordered policy row) otherwise reaches the prompt as a
        // bare enum name with no verb beside it. The fallback says so honestly rather than guessing a verb — and
        // this test is what turns "somebody will notice" into a red build.
        foreach (var mechanism in Enum.GetValues<QualityMechanism>())
            SupervisorQualityRecitation.Render(new[] { Reading("s1", mechanism, "r") })!
                .ShouldNotContain("no verb mapped", Case.Sensitive, $"{mechanism} reached the prompt with no verb mapped to it");
    }

    private static SupervisorUnitQualityDecision Reading(string subtaskId, QualityMechanism mechanism, string reason) =>
        new() { SubtaskId = subtaskId, Mechanism = mechanism, Reason = reason, Facts = new QualityDecisionInput() };
}
