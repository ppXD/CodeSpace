using CodeSpace.Core.Services.Decisions;
using CodeSpace.Messages.Decisions;
using Shouldly;

namespace CodeSpace.UnitTests.Decisions;

/// <summary>
/// Pins the fail-closed Decision policy FLOOR (D4) — the single raise-only clamp that decides who may answer a decision.
/// It can only force a declaration UP to human_required, never down: a high-stakes ask (approve_action / a side-effecting
/// option / high risk / a missing recommendation or blocking reason) is human-only regardless of the declared policy;
/// past the floor, a declared auto_allowed / supervisor_first survives, and anything blank/unknown fails closed to human.
/// </summary>
[Trait("Category", "Unit")]
public class DecisionPolicyFloorTests
{
    // A baseline that is SAFE past the floor — low risk, a recommendation + blocking reason, no side-effecting option.
    private static DecisionRequest Safe(string policy, string risk = DecisionRiskLevels.Low, string decisionType = DecisionTypes.ChooseOne, bool sideEffecting = false) => new()
    {
        Id = Guid.NewGuid(),
        RootTraceId = Guid.NewGuid(),
        Scope = DecisionScopes.Agent,
        RequesterType = DecisionRequesterTypes.Agent,
        DecisionType = decisionType,
        Question = "ship?",
        Options = new[] { new DecisionOption { Id = "a", Label = "A" }, new DecisionOption { Id = "b", Label = "B", IsSideEffecting = sideEffecting } },
        RecommendedOption = "a",
        BlockingReason = "the build is green",
        RiskLevel = risk,
        Policy = policy,
        TimeoutAt = DateTimeOffset.UnixEpoch,
        DedupeKey = "k",
        ResumeBackend = DecisionResumeBackends.ToolLedger,
    };

    [Theory]
    [InlineData(DecisionPolicies.AutoAllowed, DecisionPolicies.AutoAllowed)]          // past the floor → survives
    [InlineData(DecisionPolicies.SupervisorFirst, DecisionPolicies.SupervisorFirst)]  // past the floor → survives
    [InlineData(DecisionPolicies.HumanRequired, DecisionPolicies.HumanRequired)]      // already human
    [InlineData("", DecisionPolicies.HumanRequired)]                                  // blank → fail-closed
    [InlineData("something_made_up", DecisionPolicies.HumanRequired)]                 // unknown → fail-closed
    public void A_safe_decision_keeps_a_known_auto_policy_and_fails_closed_otherwise(string declared, string expected)
    {
        DecisionPolicyFloor.Effective(Safe(declared)).ShouldBe(expected);
    }

    [Theory]
    [InlineData(DecisionPolicies.AutoAllowed)]
    [InlineData(DecisionPolicies.SupervisorFirst)]
    public void High_risk_floors_to_human_regardless_of_the_declared_policy(string declared)
    {
        DecisionPolicyFloor.Effective(Safe(declared, risk: DecisionRiskLevels.High)).ShouldBe(DecisionPolicies.HumanRequired);
    }

    [Theory]
    [InlineData(DecisionPolicies.AutoAllowed)]
    [InlineData(DecisionPolicies.SupervisorFirst)]
    public void A_side_effecting_option_floors_to_human(string declared)
    {
        DecisionPolicyFloor.Effective(Safe(declared, sideEffecting: true)).ShouldBe(DecisionPolicies.HumanRequired);
    }

    [Theory]
    [InlineData(DecisionPolicies.AutoAllowed)]
    [InlineData(DecisionPolicies.SupervisorFirst)]
    public void An_approve_action_permission_gate_floors_to_human(string declared)
    {
        DecisionPolicyFloor.Effective(Safe(declared, decisionType: DecisionTypes.ApproveAction)).ShouldBe(DecisionPolicies.HumanRequired);
    }

    [Fact]
    public void A_missing_recommendation_or_blocking_reason_floors_to_human()
    {
        DecisionPolicyFloor.Effective(Safe(DecisionPolicies.SupervisorFirst) with { RecommendedOption = null }).ShouldBe(DecisionPolicies.HumanRequired);
        DecisionPolicyFloor.Effective(Safe(DecisionPolicies.SupervisorFirst) with { RecommendedOption = "   " }).ShouldBe(DecisionPolicies.HumanRequired);
        DecisionPolicyFloor.Effective(Safe(DecisionPolicies.AutoAllowed) with { BlockingReason = null }).ShouldBe(DecisionPolicies.HumanRequired);
    }

    [Fact]
    public void FloorsToHuman_is_the_arbiter_precondition()
    {
        DecisionPolicyFloor.FloorsToHuman(Safe(DecisionPolicies.SupervisorFirst)).ShouldBeFalse("a clean supervisor_first decision is arbitratable");
        DecisionPolicyFloor.FloorsToHuman(Safe(DecisionPolicies.SupervisorFirst, risk: DecisionRiskLevels.High)).ShouldBeTrue("high risk is human-only");
    }

    [Theory]
    // FAIL-CLOSED on the danger signals: a casing slip or hallucinated synonym for risk/type must NOT bypass the floor.
    [InlineData("HIGH")]
    [InlineData("High")]
    [InlineData("critical")]      // unknown synonym → fail-closed
    [InlineData("severe")]
    [InlineData("")]              // blank risk → fail-closed
    [InlineData("urgent")]
    public void A_non_low_or_medium_risk_floors_to_human_even_cased_or_unknown(string risk)
    {
        DecisionPolicyFloor.Effective(Safe(DecisionPolicies.AutoAllowed) with { RiskLevel = risk })
            .ShouldBe(DecisionPolicies.HumanRequired, "any risk that isn't a recognized low/medium fails closed — a relabeled high-risk decision can never auto-resolve");
    }

    [Theory]
    [InlineData("Approve_Action")]
    [InlineData("APPROVE_ACTION")]
    public void A_cased_approve_action_still_floors_to_human(string decisionType)
    {
        DecisionPolicyFloor.Effective(Safe(DecisionPolicies.SupervisorFirst) with { DecisionType = decisionType })
            .ShouldBe(DecisionPolicies.HumanRequired);
    }

    [Theory]
    // A casing slip on the POLICY resolves to the canonical value (the floor above already caught every danger).
    [InlineData("Low", "MEDIUM")]   // both recognized risk casings are safe
    [InlineData("low", "medium")]
    public void Recognized_risk_casings_do_not_floor(string low, string medium)
    {
        DecisionPolicyFloor.FloorsToHuman(Safe(DecisionPolicies.AutoAllowed) with { RiskLevel = low }).ShouldBeFalse();
        DecisionPolicyFloor.FloorsToHuman(Safe(DecisionPolicies.AutoAllowed) with { RiskLevel = medium }).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Auto_Allowed")]     // a CASING slip of the canonical "auto_allowed" — recognized case-insensitively
    [InlineData("AUTO_ALLOWED")]
    [InlineData("auto_allowed")]
    public void A_cased_auto_policy_on_a_safe_decision_is_recognized(string policy)
    {
        DecisionPolicyFloor.Effective(Safe(policy)).ShouldBe(DecisionPolicies.AutoAllowed, "a casing slip on a SAFE decision's policy resolves to the canonical auto_allowed, not an over-clamp");
    }
}
