using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the PR-E E5 governance gate — REUSING <c>AgentToolGate</c> (Rule 7, not reinvented). Pins the
/// ApprovalPolicy → autonomy-tier mapping + the per-decision verdict: a side-effecting decision (spawn/retry)
/// Allows under <c>None</c>, RequireApproves under <c>Spawns</c>, and an UNMAPPED policy fail-closed DENIES; a
/// non-side-effecting decision (plan/merge/stop/ask_human) always Allows. The approval rewrite into ask_human
/// is also pinned (the gated spawn parks for a human before any agent is created).
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorGovernanceTests
{
    // ── ApprovalPolicy → autonomy tier (the mapping the gate reads) ──────────────────

    [Theory]
    [InlineData(SupervisorApprovalPolicy.None, AgentAutonomyLevel.Unleashed)]
    [InlineData(SupervisorApprovalPolicy.Spawns, AgentAutonomyLevel.Standard)]
    public void Policy_maps_to_the_expected_tier(SupervisorApprovalPolicy policy, AgentAutonomyLevel tier)
    {
        SupervisorGovernance.ToAutonomyLevel(policy).ShouldBe(tier);
    }

    [Fact]
    public void An_unmapped_policy_maps_to_Confined_so_the_gate_denies()
    {
        // FAIL-CLOSED: a future policy value with no tier falls to Confined → AgentToolGate denies a gated tool.
        SupervisorGovernance.ToAutonomyLevel((SupervisorApprovalPolicy)999).ShouldBe(AgentAutonomyLevel.Confined);
    }

    // ── The per-decision verdict ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(SupervisorDecisionKinds.Spawn)]
    [InlineData(SupervisorDecisionKinds.Retry)]
    public void A_side_effecting_decision_is_allowed_under_None_and_gated_under_Spawns(string kind)
    {
        SupervisorGovernance.Decide(kind, SupervisorApprovalPolicy.None).ShouldBe(AgentToolGateDecision.Allow, "None → autonomous spawn");
        SupervisorGovernance.Decide(kind, SupervisorApprovalPolicy.Spawns).ShouldBe(AgentToolGateDecision.RequireApproval, "Spawns → a human approves first");
    }

    [Fact]
    public void An_unmapped_policy_denies_a_side_effecting_decision()
    {
        SupervisorGovernance.Decide(SupervisorDecisionKinds.Spawn, (SupervisorApprovalPolicy)999).ShouldBe(AgentToolGateDecision.Deny, "fail-closed");
    }

    [Theory]
    [InlineData(SupervisorDecisionKinds.Plan)]
    [InlineData(SupervisorDecisionKinds.Merge)]
    [InlineData(SupervisorDecisionKinds.Stop)]
    [InlineData(SupervisorDecisionKinds.AskHuman)]
    public void A_non_side_effecting_decision_always_allows_regardless_of_policy(string kind)
    {
        SupervisorGovernance.Decide(kind, SupervisorApprovalPolicy.Spawns).ShouldBe(AgentToolGateDecision.Allow, "nothing to gate — creates no agents");
        SupervisorGovernance.IsSideEffecting(kind).ShouldBeFalse();
    }

    [Theory]
    [InlineData(SupervisorDecisionKinds.Spawn, true)]
    [InlineData(SupervisorDecisionKinds.Retry, true)]
    [InlineData(SupervisorDecisionKinds.Plan, false)]
    [InlineData(SupervisorDecisionKinds.Merge, false)]
    public void Side_effecting_classification_is_pinned(string kind, bool sideEffecting)
    {
        SupervisorGovernance.IsSideEffecting(kind).ShouldBe(sideEffecting);
    }

    [Fact]
    public void An_irreversible_side_effect_escalates_even_under_None()
    {
        // Reserved for a future merge-PR / push: alwaysRequiresApproval makes even the most permissive tier ask.
        SupervisorGovernance.Decide(SupervisorDecisionKinds.Spawn, SupervisorApprovalPolicy.None, irreversible: true)
            .ShouldBe(AgentToolGateDecision.RequireApproval);
    }

    // ── The approval rewrite: a gated spawn becomes an ask_human park ────────────────

    [Fact]
    public void A_gated_spawn_rewrites_into_an_ask_human_approval_request()
    {
        var spawn = new SupervisorDecision
        {
            Kind = SupervisorDecisionKinds.Spawn,
            PayloadJson = """{"subtaskIds":["a","b"]}""",
        };

        var rewritten = SupervisorApprovalRequest.IntoAskHuman(spawn);

        rewritten.Kind.ShouldBe(SupervisorDecisionKinds.AskHuman, "the gated spawn parks for a human BEFORE any agent is created");
        rewritten.PayloadJson.ShouldContain("Approve spawning 2 agent");
        rewritten.IsTerminal.ShouldBeFalse();
    }
}
