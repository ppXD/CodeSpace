using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public sealed class RealModelAnswerReviewDelegationVerdictTests
{
    private const string AuditToken = "source-only-68d5a241";

    [Fact]
    public void A_source_grounded_blocker_is_required_for_the_incorrect_implementation()
    {
        var verdict = Verdict(false, CriticSeverity.Blocker);
        RealModelAnswerReviewDelegationE2ETests.MeetsCase(verdict, true, AuditToken).ShouldBeTrue();
        RealModelAnswerReviewDelegationE2ETests.MeetsCase(verdict with { Approved = true }, true, AuditToken).ShouldBeFalse();
        RealModelAnswerReviewDelegationE2ETests.MeetsCase(verdict with { Failed = true }, true, AuditToken).ShouldBeFalse();
    }

    [Fact]
    public void A_clean_implementation_requires_an_independent_approval_with_source_evidence()
    {
        var verdict = new CriticVerdict { Mode = ReviewMode.Gate, Approved = true, Rationale = AuditToken + ": implementation matches the contract" };
        RealModelAnswerReviewDelegationE2ETests.MeetsCase(verdict, false, AuditToken).ShouldBeTrue();
        RealModelAnswerReviewDelegationE2ETests.MeetsCase(verdict with { Approved = false }, false, AuditToken).ShouldBeFalse();
        RealModelAnswerReviewDelegationE2ETests.MeetsCase(verdict with { Failed = true }, false, AuditToken).ShouldBeFalse();
        RealModelAnswerReviewDelegationE2ETests.MeetsCase(verdict with { Rationale = "looks good" }, false, AuditToken).ShouldBeFalse();
        RealModelAnswerReviewDelegationE2ETests.MeetsCase(Verdict(false, CriticSeverity.Blocker), false, AuditToken).ShouldBeFalse();
    }

    [Theory]
    [InlineData(CriticSeverity.Major)]
    [InlineData(CriticSeverity.Minor)]
    public void Cosmetic_feedback_does_not_satisfy_the_required_blocker(CriticSeverity severity) =>
        RealModelAnswerReviewDelegationE2ETests.MeetsCase(Verdict(false, severity), true, AuditToken).ShouldBeFalse();

    [Fact]
    public void A_guessed_or_different_source_token_does_not_count_as_observation()
    {
        RealModelAnswerReviewDelegationE2ETests.MeetsCase(Verdict(false, CriticSeverity.Blocker), true, "different-attempt-token").ShouldBeFalse();
        RealModelAnswerReviewDelegationE2ETests.MeetsCase(Verdict(false, CriticSeverity.Blocker), true, "").ShouldBeFalse();
        var noEvidence = Verdict(false, CriticSeverity.Blocker) with { Issues = [new CriticIssue { Text = "arithmetic is wrong", Severity = CriticSeverity.Blocker, Evidence = "I infer a bug" }] };
        RealModelAnswerReviewDelegationE2ETests.MeetsCase(noEvidence, true, AuditToken).ShouldBeFalse();
    }

    private static CriticVerdict Verdict(bool approved, CriticSeverity severity) => new()
    {
        Mode = ReviewMode.Gate, Approved = approved, Rationale = AuditToken + ": arithmetic violates the contract",
        Issues = [new CriticIssue { Text = "amount is halved rather than divided by ten", Severity = severity, Evidence = "pricing.py: " + AuditToken }],
    };
}
