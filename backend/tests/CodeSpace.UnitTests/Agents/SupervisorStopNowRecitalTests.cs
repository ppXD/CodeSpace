using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: pins P5-6 — the "if you stopped now" contract recital. The reducer's own mid-run verdict reaches the
/// decider prompt so the perception gap behind stop-without-shipping closes BEFORE the stop is chosen: unresolved
/// dimensions render with the settle-or-honest-stop steer, an all-clear renders the stop-now steer (anti-overwork),
/// settled-positive dimensions are omitted (the #1256 session-recital convention), and a contract-less run renders
/// nothing (byte-identical prompt). The DB-reading compose lives at rehydrate; this renderer is pure.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorStopNowRecitalTests
{
    private static CompletionAssessment Assessment(OutcomeDisposition outcome = OutcomeDisposition.Solved, VerificationDisposition verification = VerificationDisposition.Passed, ArtifactDisposition artifact = ArtifactDisposition.Captured, DeliveryDisposition delivery = DeliveryDisposition.Delivered) => new()
    {
        Basis = CompletionBasis.ContractDerived, Execution = ExecutionDisposition.Completed,
        Outcome = outcome, Verification = verification, Artifact = artifact, Delivery = delivery,
    };

    [Fact]
    public void No_assessment_renders_nothing()
    {
        SupervisorStopNowRecital.Render(null).ShouldBeNull("contract-less / pre-F0 runs pay no prompt tax");
    }

    [Fact]
    public void Unresolved_dimensions_render_with_the_settle_or_honest_stop_steer()
    {
        var block = SupervisorStopNowRecital.Render(Assessment(outcome: OutcomeDisposition.Unsolved, verification: VerificationDisposition.Failed, delivery: DeliveryDisposition.Unknown));

        block.ShouldNotBeNull();
        block!.ShouldContain("IF YOU STOPPED NOW", Case.Sensitive);
        block.ShouldContain("outcome=Unsolved", Case.Sensitive);
        block.ShouldContain("verification=Failed", Case.Sensitive);
        block.ShouldContain("delivery=Unknown", Case.Sensitive);
        block.ShouldNotContain("artifact=", Case.Sensitive, "a settled-positive dimension is omitted — the unclean ones name exactly what is owed");
        block.ShouldContain("a stop right now cannot read Solved", Case.Sensitive);
        block.ShouldContain("never stop as if done", Case.Sensitive, "the C3 stop-without-shipping steer");
    }

    [Fact]
    public void An_all_clear_renders_the_stop_now_steer()
    {
        var block = SupervisorStopNowRecital.Render(Assessment());

        block.ShouldNotBeNull("the clean direction is as decision-relevant as the dirty one");
        block!.ShouldContain("every contract dimension reads SETTLED", Case.Sensitive);
        block.ShouldContain("a clean stop now reads Solved", Case.Sensitive);
        block.ShouldContain("stop rather than spending further turns", Case.Sensitive, "the anti-overwork direction");
    }

    [Theory]
    [InlineData(VerificationDisposition.NotApplicable)]   // authorized-NA reads settled — no nag
    [InlineData(VerificationDisposition.Passed)]
    public void A_settled_verification_never_renders_as_a_concern(VerificationDisposition verification)
    {
        SupervisorStopNowRecital.Render(Assessment(verification: verification))!
            .ShouldNotContain("verification=", Case.Sensitive);
    }

    [Fact]
    public void The_header_is_pinned()
    {
        SupervisorStopNowRecital.Header.ShouldBe("IF YOU STOPPED NOW (the completion reducer's verdict on the facts so far):");
    }

    // ── The prompt wiring: prerendered at rehydrate, rendered verbatim by the pure prompt build ──

    [Fact]
    public void The_user_prompt_carries_the_prerendered_recital()
    {
        var recital = SupervisorStopNowRecital.Render(Assessment(verification: VerificationDisposition.Failed, outcome: OutcomeDisposition.Unsolved))!;

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(new SupervisorTurnContext
        {
            Goal = "ship it", TurnNumber = 3, PriorDecisions = Array.Empty<SupervisorPriorDecision>(), CompletionRecital = recital,
        });

        prompt.ShouldContain("IF YOU STOPPED NOW", Case.Sensitive);
        prompt.ShouldContain("verification=Failed", Case.Sensitive);
    }

    [Fact]
    public void A_null_recital_leaves_the_prompt_byte_identical()
    {
        LlmSupervisorDecider.BuildUserPromptForTest(new SupervisorTurnContext { Goal = "ship it", TurnNumber = 3, PriorDecisions = Array.Empty<SupervisorPriorDecision>() })
            .ShouldNotContain("IF YOU STOPPED NOW", Case.Sensitive);
    }
}
