using CodeSpace.Core.Services.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the pure contradiction classifier (P4-1) — the ONE shared seam every self-report/gate pair in the codebase
/// folds through. Exhaustive over every reachable (selfReportedSuccess, acceptancePassed) combination: both
/// agreement cases, both disagreement (contradiction) cases, and the no-grade case for each self-report.
/// </summary>
[Trait("Category", "Unit")]
public class AgentContradictionTests
{
    [Fact]
    public void A_true_self_report_with_a_passing_grade_agrees()
    {
        AgentContradiction.Detect(selfReportedSuccess: true, acceptancePassed: true).ShouldBeNull();
    }

    [Fact]
    public void A_false_self_report_with_a_failing_grade_agrees()
    {
        AgentContradiction.Detect(selfReportedSuccess: false, acceptancePassed: false).ShouldBeNull();
    }

    [Fact]
    public void A_true_self_report_with_a_failing_grade_is_an_over_claim()
    {
        AgentContradiction.Detect(selfReportedSuccess: true, acceptancePassed: false).ShouldBe(AgentContradiction.OverClaim);
    }

    [Fact]
    public void A_false_self_report_with_a_passing_grade_is_an_under_claim()
    {
        AgentContradiction.Detect(selfReportedSuccess: false, acceptancePassed: true).ShouldBe(AgentContradiction.UnderClaim);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void No_grade_at_all_is_nothing_to_compare_regardless_of_the_self_report(bool selfReportedSuccess)
    {
        AgentContradiction.Detect(selfReportedSuccess, acceptancePassed: null).ShouldBeNull("no oracle authored, or the grade was deferred — there is nothing to contradict");
    }
}
