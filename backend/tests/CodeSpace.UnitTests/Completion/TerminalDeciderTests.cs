using CodeSpace.Core.Services.Completion;
using CodeSpace.Messages.Contracts;
using Shouldly;

namespace CodeSpace.UnitTests.Completion;

/// <summary>
/// 🟢 Unit: PINS the sealed six-state transition table (v4.2-FINAL Lock Clause 5) — the ONE pure mapping from the
/// five-dimensional assessment + handoff/capability facts to <see cref="TerminalDecision"/>. The states are
/// sealed; any arm change must break a pin here and be argued in its PR. Exactly one state is VDS-eligible.
/// </summary>
[Trait("Category", "Unit")]
public class TerminalDeciderTests
{
    private static CompletionAssessment Clean() => new()
    {
        Basis = CompletionBasis.ContractDerived,
        Execution = ExecutionDisposition.Completed,
        Outcome = OutcomeDisposition.Solved,
        Verification = VerificationDisposition.Passed,
        Artifact = ArtifactDisposition.Captured,
        Delivery = DeliveryDisposition.Delivered,
    };

    [Fact]
    public void The_full_predicate_and_only_the_full_predicate_is_CleanSuccess()
    {
        TerminalDecider.Decide(Clean(), handoffReachable: true).ShouldBe(TerminalDecision.CleanSuccess);
        TerminalDecider.Decide(Clean() with { Verification = VerificationDisposition.NotApplicable }, true).ShouldBe(TerminalDecision.CleanSuccess, "an AUTHORIZED not-applicable verification satisfies the conjunct");
        TerminalDecider.Decide(Clean() with { Artifact = ArtifactDisposition.NothingExpected }, true).ShouldBe(TerminalDecision.CleanSuccess);
        TerminalDecider.Decide(Clean() with { Delivery = DeliveryDisposition.NotRequired }, true).ShouldBe(TerminalDecision.CleanSuccess, "a genuinely not-required delivery satisfies the conjunct");
    }

    [Fact]
    public void Only_CleanSuccess_is_VDS_eligible()
    {
        foreach (var decision in Enum.GetValues<TerminalDecision>())
            TerminalDecider.IsVdsEligible(decision).ShouldBe(decision == TerminalDecision.CleanSuccess);
    }

    [Theory]
    [InlineData(OutcomeDisposition.Unsolved, TerminalDecision.HonestFailure)]
    [InlineData(OutcomeDisposition.Abstained, TerminalDecision.NeedsClarification)]
    [InlineData(OutcomeDisposition.Unknown, TerminalDecision.NeedsReview)]
    public void The_outcome_arms(OutcomeDisposition outcome, TerminalDecision expected)
    {
        TerminalDecider.Decide(Clean() with { Outcome = outcome }, true).ShouldBe(expected);
    }

    [Theory]
    [InlineData(VerificationDisposition.Waived)]              // never VDS — its artifact semantics wait for the amend arc
    [InlineData(VerificationDisposition.HumanReviewRequired)]
    [InlineData(VerificationDisposition.InfraUnknown)]
    [InlineData(VerificationDisposition.Failed)]              // contradictory with Solved — defensive, a human looks
    [InlineData(VerificationDisposition.Unknown)]
    public void A_non_positive_verification_needs_review(VerificationDisposition verification)
    {
        TerminalDecider.Decide(Clean() with { Verification = verification }, true).ShouldBe(TerminalDecision.NeedsReview);
    }

    [Fact]
    public void The_artifact_arms()
    {
        TerminalDecider.Decide(Clean() with { Artifact = ArtifactDisposition.CaptureFailed }, true).ShouldBe(TerminalDecision.HonestFailure, "lost work is a failure, never a success and never silent");
        TerminalDecider.Decide(Clean() with { Artifact = ArtifactDisposition.Unknown }, true).ShouldBe(TerminalDecision.NeedsReview);
    }

    [Theory]
    [InlineData(DeliveryDisposition.PolicyBlocked, TerminalDecision.Park)]   // solved-but-parked — park, don't die
    [InlineData(DeliveryDisposition.WaivedByPolicy, TerminalDecision.Park)]  // a waiver is not a delivery (never VDS)
    [InlineData(DeliveryDisposition.Unknown, TerminalDecision.NeedsReview)]
    public void The_delivery_arms(DeliveryDisposition delivery, TerminalDecision expected)
    {
        TerminalDecider.Decide(Clean() with { Delivery = delivery }, true).ShouldBe(expected);
    }

    [Fact]
    public void An_unreachable_handoff_parks_a_would_be_clean_success()
    {
        TerminalDecider.Decide(Clean(), handoffReachable: false).ShouldBe(TerminalDecision.Park, "the work stands but the user cannot reach it — the LAST conjunct of the predicate");
    }

    [Fact]
    public void The_execution_arms()
    {
        TerminalDecider.Decide(Clean() with { Execution = ExecutionDisposition.ForcedStop }, true).ShouldBe(TerminalDecision.HonestFailure);
        TerminalDecider.Decide(Clean() with { Execution = ExecutionDisposition.Cancelled }, true).ShouldBe(TerminalDecision.HonestFailure);
        TerminalDecider.Decide(Clean() with { Execution = ExecutionDisposition.Crashed }, true).ShouldBe(TerminalDecision.NeedsReview, "disorderly execution cannot be honestly decided");
    }

    [Fact]
    public void An_unsupported_capability_and_legacy_tape_never_reach_the_predicate()
    {
        TerminalDecider.Decide(Clean(), true, capabilitySupported: false).ShouldBe(TerminalDecision.Unsupported);
        TerminalDecider.Decide(Clean() with { Basis = CompletionBasis.LegacyUnknown }, true).ShouldBe(TerminalDecision.NeedsReview, "old tape is never re-derived into a terminal claim");
    }
}
