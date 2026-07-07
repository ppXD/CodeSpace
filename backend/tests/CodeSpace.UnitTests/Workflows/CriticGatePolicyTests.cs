using CodeSpace.Core.Services.Review;
using CodeSpace.Messages.Review;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The PLATFORM'S severity policy over a critic verdict (P1) — the pure rule every gate site shares: a gate halts iff
/// an issue is a Blocker; a revision round is worth it iff at least one issue is a Blocker or Major. This is the fix
/// for adversarial review that blocks on every nitpick — a Minor issue no longer carries a fatal issue's halting power.
/// </summary>
[Trait("Category", "Unit")]
public class CriticGatePolicyTests
{
    private static CriticIssue Issue(CriticSeverity severity) => new() { Text = "x", Evidence = "y", Severity = severity };

    [Fact]
    public void No_issues_approves() =>
        CriticGatePolicy.Approves(Array.Empty<CriticIssue>()).ShouldBeTrue("nothing wrong ⇒ nothing to halt on");

    [Fact]
    public void A_blocker_halts_the_gate() =>
        CriticGatePolicy.Approves(new[] { Issue(CriticSeverity.Major), Issue(CriticSeverity.Blocker), Issue(CriticSeverity.Minor) })
            .ShouldBeFalse("one Blocker among any others halts");

    [Fact]
    public void A_major_or_minor_only_flag_does_not_halt() =>
        CriticGatePolicy.Approves(new[] { Issue(CriticSeverity.Major), Issue(CriticSeverity.Major), Issue(CriticSeverity.Minor) })
            .ShouldBeTrue("no Blocker ⇒ surfaced but not halted — the calibration fix");

    [Fact]
    public void A_blocker_or_major_warrants_a_revision_round()
    {
        CriticGatePolicy.WarrantsRevision(new[] { Issue(CriticSeverity.Blocker) }).ShouldBeTrue();
        CriticGatePolicy.WarrantsRevision(new[] { Issue(CriticSeverity.Major), Issue(CriticSeverity.Minor) }).ShouldBeTrue("a Major is worth a pass");
    }

    [Fact]
    public void A_minor_only_flag_does_not_warrant_a_revision() =>
        CriticGatePolicy.WarrantsRevision(new[] { Issue(CriticSeverity.Minor), Issue(CriticSeverity.Minor) })
            .ShouldBeFalse("nitpick-only ⇒ not worth a round");

    [Fact]
    public void No_structured_issues_warrants_a_revision() =>
        CriticGatePolicy.WarrantsRevision(Array.Empty<CriticIssue>())
            .ShouldBeTrue("a free-text critique with no structured issues keeps its revision — unknown severity is not silently dropped");
}
