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

        // The question text (as the next turn reads it back) carries the marker the gate recognises its OWN card by.
        var question = System.Text.Json.JsonDocument.Parse(rewritten.PayloadJson).RootElement.GetProperty("question").GetString();
        question.ShouldContain(SupervisorApprovalRequest.ApprovalMarker, customMessage: "every approval card carries the marker the next turn recognises its own approval by");
        rewritten.IsTerminal.ShouldBeFalse();
    }

    // ── Approve-then-proceed: a just-approved spawn is bound to its approval, not re-gated ──

    [Fact]
    public void A_spawn_right_after_an_approved_approval_card_is_recognised_as_just_approved()
    {
        // The immediately-preceding decided decision is THIS gate's own approval card, folded with an approving
        // answer → the re-emitted spawn is bound to the approval and proceeds (no second ask_human, no dead-end).
        var context = ContextEndingWith(ApprovalCard(), foldedAnswer: SupervisorApprovalRequest.ApproveReply);

        SupervisorApprovalRequest.WasJustApproved(context).ShouldBeTrue();
    }

    [Theory]
    [InlineData("reject")]
    [InlineData("Reject it")]
    [InlineData("")]
    [InlineData(null)]
    public void A_non_approving_answer_does_not_grant_a_pass(string? answer)
    {
        // Reject / empty / unanswered → fail-closed: the spawn stays gated.
        SupervisorApprovalRequest.WasJustApproved(ContextEndingWith(ApprovalCard(), answer)).ShouldBeFalse();
    }

    [Fact]
    public void An_approved_content_ask_human_is_not_an_approval_card_so_grants_no_pass()
    {
        // A CONTENT ask_human the decider itself raised (no approval marker) MUST NOT grant a spurious pass even
        // when its answer happens to read "approve".
        var contentAsk = new SupervisorPriorDecision
        {
            Sequence = 1,
            DecisionKind = SupervisorDecisionKinds.AskHuman,
            Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"question":"which approach: rewrite or patch?"}""",
            OutcomeJson = SupervisorOutcome.FoldAnswer("which approach: rewrite or patch?", "tok", "approve the rewrite"),
        };

        SupervisorApprovalRequest.WasJustApproved(ContextOf(contentAsk)).ShouldBeFalse();
    }

    [Fact]
    public void An_approval_card_that_is_not_the_LAST_decision_grants_no_pass()
    {
        // A fresh spawn on a LATER turn (an approval card exists earlier, but a settled spawn is the most recent
        // decision) is gated again — the approval binds to ONE re-emitted decision only.
        var context = new SupervisorTurnContext
        {
            ApprovalPolicy = SupervisorApprovalPolicy.Spawns,
            PriorDecisions = new[]
            {
                ApprovalCardWith(SupervisorApprovalRequest.ApproveReply),
                new SupervisorPriorDecision { Sequence = 2, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = """{"agentCount":2}""" },
            },
        };

        SupervisorApprovalRequest.WasJustApproved(context).ShouldBeFalse();
    }

    [Fact]
    public void No_prior_decisions_grants_no_pass()
    {
        SupervisorApprovalRequest.WasJustApproved(new SupervisorTurnContext()).ShouldBeFalse();
    }

    // ── Pins (Rule 8) — the approval-binding reply words are load-bearing ─────────────

    [Fact]
    public void The_approval_reply_word_and_marker_are_pinned()
    {
        // The gate matches the folded human answer against ApproveReply + recognises its own card by ApprovalMarker.
        // A reword silently breaks approve-then-proceed, so pin the literals (Rule 8).
        SupervisorApprovalRequest.ApproveReply.ShouldBe("approve");
        SupervisorApprovalRequest.ApprovalMarker.ShouldBe("Reply 'approve' to proceed or 'reject' to stop.");
    }

    private static SupervisorDecision ApprovalCard() =>
        SupervisorApprovalRequest.IntoAskHuman(new SupervisorDecision { Kind = SupervisorDecisionKinds.Spawn, PayloadJson = """{"subtaskIds":["a","b"]}""" });

    private static SupervisorPriorDecision ApprovalCardWith(string? foldedAnswer) => new()
    {
        Sequence = 1,
        DecisionKind = SupervisorDecisionKinds.AskHuman,
        Status = SupervisorDecisionStatus.Succeeded,
        PayloadJson = ApprovalCard().PayloadJson,
        OutcomeJson = SupervisorOutcome.FoldAnswer("q", "tok", foldedAnswer),
    };

    private static SupervisorTurnContext ContextEndingWith(SupervisorDecision approvalCard, string? foldedAnswer) =>
        ContextOf(new SupervisorPriorDecision
        {
            Sequence = 1,
            DecisionKind = approvalCard.Kind,
            Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = approvalCard.PayloadJson,
            OutcomeJson = SupervisorOutcome.FoldAnswer("q", "tok", foldedAnswer),
        });

    private static SupervisorTurnContext ContextOf(SupervisorPriorDecision last) => new()
    {
        ApprovalPolicy = SupervisorApprovalPolicy.Spawns,
        PriorDecisions = new[] { last },
    };
}
