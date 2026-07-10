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
    /// <summary>The marker phrase EVERY approval question carries — the stable, load-bearing tail the next turn matches on to recognise its OWN approval card (vs a content ask_human). Pinned by a unit test so a reword is a visible decision.</summary>
    public const string ApprovalMarker = "Reply 'approve' to proceed or 'reject' to stop.";

    /// <summary>Rewrite a gated decision into an ask_human approval question. The question describes the gated action so the human can approve / reject; the decider re-decides on the next turn with the folded answer in context.</summary>
    public static SupervisorDecision IntoAskHuman(SupervisorDecision gated) => new()
    {
        Kind = SupervisorDecisionKinds.AskHuman,
        ServerAuthored = true,
        PayloadJson = JsonSerializer.Serialize(new SupervisorAskHumanPayload { Question = QuestionFor(gated) }, AgentJson.Options),
    };

    /// <summary>
    /// Whether THIS side-effecting decision was just APPROVED by a human — the binding that turns the gate from a
    /// permanent block into approve-then-proceed. True when the immediately-preceding decided decision is one of
    /// THIS gate's own approval-request ask_human cards (its question carries <see cref="ApprovalMarker"/>) AND the
    /// folded human answer approves. So a re-emitted spawn right after an approve passes the gate ONCE; a fresh
    /// spawn on a later turn (no approval card immediately before it) is gated again. A reject / no answer / a
    /// content ask_human does NOT count, so the gate stays fail-closed.
    /// </summary>
    public static bool WasJustApproved(SupervisorTurnContext context)
    {
        var last = context.PriorDecisions.Count > 0 ? context.PriorDecisions[^1] : null;

        if (last?.DecisionKind != SupervisorDecisionKinds.AskHuman) return false;

        if (!IsApprovalCard(last.PayloadJson)) return false;

        return Approves(SupervisorOutcome.ReadAskHumanAnswer(last.OutcomeJson));
    }

    /// <summary>An ask_human is one of THIS gate's approval cards iff its question carries the approval marker — a content ask_human (the decider's own question) never does, so it never grants a spurious pass.</summary>
    private static bool IsApprovalCard(string askHumanPayloadJson)
    {
        try
        {
            var root = JsonDocument.Parse(askHumanPayloadJson).RootElement;

            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("question", out var q)
                && q.ValueKind == JsonValueKind.String && (q.GetString() ?? "").Contains(ApprovalMarker, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The human's free-text answer approves iff it begins with the approve reply word (case-insensitive, trimmed). Anything else — reject, a question, an empty/absent answer — is NOT an approval (fail-closed).</summary>
    private static bool Approves(string? answer) =>
        answer != null && answer.TrimStart().StartsWith(ApproveReply, StringComparison.OrdinalIgnoreCase);

    /// <summary>The reply word a human types to approve a gated side effect. Load-bearing — the gate matches the folded answer against it; pinned by a unit test.</summary>
    public const string ApproveReply = "approve";

    /// <summary>The approval question naming the gated action — a spawn names its fan-out count, a retry names a single re-run; everything else (defensive) names the kind.</summary>
    private static string QuestionFor(SupervisorDecision gated) => gated.Kind switch
    {
        SupervisorDecisionKinds.Spawn => $"Approve spawning {SupervisorBounds.SpawnCount(gated)} agent(s)? {ApprovalMarker}",
        SupervisorDecisionKinds.Retry => $"Approve retrying a subtask as a fresh agent run? {ApprovalMarker}",
        _ => $"Approve the '{gated.Kind}' action? {ApprovalMarker}",
    };
}
