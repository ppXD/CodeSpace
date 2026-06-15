using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// Rewrites a governance-gated side-effecting decision into an <c>ask_human</c> APPROVAL request (PR-E E5).
/// When the run's approval policy RequireApproves a spawn/retry, the turn loop substitutes this ask_human BEFORE
/// the side effect — REUSING E4's durable HITL park (the executor posts a question card, the node parks on a
/// single Action wait, the human's answer resumes the next turn) rather than building a second approval ledger.
/// So NO agent is created until a human answers; the gate is fail-closed by construction (a degraded no-surface
/// ask_human self-advances to a no-op rather than auto-spawning).
///
/// <para>The substitution is DETERMINISTIC given (decision) — the turn loop re-derives the same ask_human on a
/// replay, so the per-turn idempotency key is stable. The question names the gated action + carries the gated
/// decision's kind for the operator's context; binding the approval to a re-emitted spawn is the decider's job
/// next turn (the LLM sees "you asked to spawn N, the human said …"), which keeps E5 free of a two-phase ledger
/// state change (no migration).</para>
/// </summary>
public static class SupervisorApprovalRequest
{
    /// <summary>Rewrite a gated decision into an ask_human approval question. The question describes the gated action so the human can approve / reject; the decider re-decides on the next turn with the folded answer in context.</summary>
    public static SupervisorDecision IntoAskHuman(SupervisorDecision gated) => new()
    {
        Kind = SupervisorDecisionKinds.AskHuman,
        PayloadJson = JsonSerializer.Serialize(new SupervisorAskHumanPayload { Question = QuestionFor(gated) }, AgentJson.Options),
    };

    /// <summary>The approval question naming the gated action — a spawn names its fan-out count, a retry names a single re-run; everything else (defensive) names the kind.</summary>
    private static string QuestionFor(SupervisorDecision gated) => gated.Kind switch
    {
        SupervisorDecisionKinds.Spawn => $"Approve spawning {SupervisorBounds.SpawnCount(gated)} agent(s)? Reply 'approve' to proceed or 'reject' to stop.",
        SupervisorDecisionKinds.Retry => "Approve retrying a subtask as a fresh agent run? Reply 'approve' to proceed or 'reject' to stop.",
        _ => $"Approve the '{gated.Kind}' action? Reply 'approve' to proceed or 'reject' to stop.",
    };
}
