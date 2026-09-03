using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// D3 — the quick lane's OWN escalation trigger: the pure predicate that decides whether a failed round is evidence
/// the MODEL was insufficient (so the next round should reach for a stronger one) or evidence of something a stronger
/// model can never fix (a grader fault, a broken environment, a gateway wire fault). Representation-agnostic like
/// <see cref="AgentContradiction"/>: both lanes (the executor's revise loop over an <c>AgentRunResult</c>, the
/// agent.run node's respawn over a flat resume payload) project their own shape into these five primitives.
/// </summary>
[Trait("Category", "Unit")]
public class AgentModelEscalationTriggerTests
{
    [Fact]
    public void An_over_claim_escalates_and_names_the_contradiction()
    {
        var reason = AgentModelEscalationTrigger.Reason(AgentContradiction.OverClaim, acceptanceFailed: true, "tests-failed-exit-1", workPresent: true, error: null);

        reason.ShouldNotBeNull();
        reason!.ShouldContain("claimed success");
        reason.ShouldContain("tests-failed-exit-1");
    }

    [Fact]
    public void An_over_claim_with_no_produced_work_still_escalates() =>
        AgentModelEscalationTrigger.Reason(AgentContradiction.OverClaim, acceptanceFailed: true, "no-branch-or-repo", workPresent: false, error: null)
            .ShouldNotBeNull("the agent declared it was done and produced nothing the check could pass — that IS the model being insufficient");

    [Fact]
    public void A_failed_check_with_work_present_escalates_even_without_a_contradiction()
    {
        var reason = AgentModelEscalationTrigger.Reason(contradiction: null, acceptanceFailed: true, "tests-failed-exit-1", workPresent: true, error: null);

        reason.ShouldNotBeNull();
        reason!.ShouldContain("produced work");
    }

    [Fact]
    public void A_failed_check_with_NO_work_and_no_contradiction_never_escalates() =>
        AgentModelEscalationTrigger.Reason(contradiction: null, acceptanceFailed: true, "tests-failed-exit-1", workPresent: false, error: null)
            .ShouldBeNull("nothing was produced and the agent never claimed otherwise — the round has no evidence about the model at all");

    [Theory]
    [InlineData("grade-error: the grader threw")]
    [InlineData("clone-failed: auth")]
    [InlineData("setup-failed: apt")]
    [InlineData("oracle-restore-failed: missing artifact")]
    [InlineData("no-rubric")]
    [InlineData("no-schema")]
    [InlineData("tests-timed-out")]
    [InlineData("setup-timed-out")]
    [InlineData("tests-failed-exit-127")]
    [InlineData("tests-failed-exit-126")]
    [InlineData("grade-skipped-budget-exhausted")]
    [InlineData("repo 'api': grade-error: the grader threw")]
    public void An_INFRA_classed_failure_never_escalates(string detail) =>
        AgentModelEscalationTrigger.Reason(AgentContradiction.OverClaim, acceptanceFailed: true, detail, workPresent: true, error: null)
            .ShouldBeNull("the check itself could not run — a stronger model can NEVER change that verdict, and spending a pricier tier on it is pure waste");

    [Fact]
    public void A_gateway_format_fault_never_escalates() =>
        AgentModelEscalationTrigger.Reason(AgentContradiction.OverClaim, acceptanceFailed: true, "tests-failed-exit-1", workPresent: true, error: "400 messages.1.content.0.type: is not a thinking block")
            .ShouldBeNull("the gateway mangled the wire FORMAT — the cause-aware retry already handles it by starting fresh with thinking disabled; a pricier model would hit the same gateway");

    [Fact]
    public void A_passing_check_never_escalates() =>
        AgentModelEscalationTrigger.Reason(contradiction: null, acceptanceFailed: false, acceptanceDetail: null, workPresent: true, error: null)
            .ShouldBeNull();
}
