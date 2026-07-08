using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// I3 (publish-or-park): a <c>stop</c> may terminalize accepted work ONLY once it is PUBLISHED (an integrated /
/// unit branch — never a silent patch-only close) with a non-empty summary; otherwise the stop never reaches the
/// side-effect executor at all — it is REJECTED AND SUBSTITUTED, the same "reject and substitute" shape every other
/// <see cref="SupervisorTurnService.ApplyPostDecisionGate"/> rung already uses (<c>SupervisorPlanValidator</c> →
/// <c>ForcedStop</c>; governance Deny/RequireApproval → <c>ForcedStop</c> / <c>SupervisorApprovalRequest.IntoAskHuman</c>).
/// Pure + stateless, given only the replayed tape — a re-entry re-derives the identical substitution.
///
/// <para><b>The substitution ladder (owner-locked):</b> a stop with accepted-but-unpublished work first gets
/// auto-substituted to ONE server-authored <c>merge</c> (<see cref="ServerAuthoredMerge"/>, <c>ForcedByPublishGate</c>)
/// — integration just happens transparently this turn, through the SAME publish-policy guard chain the per-agent
/// push already respects (<see cref="Executors.RealSupervisorActionExecutor"/>'s integrate path). If that merge (or
/// an earlier one) still did not yield a clean published branch — a real conflict, a publish-policy block, a missing
/// credential, or any other reason — the NEXT stop attempt is substituted to <c>ask_human</c> instead of retrying
/// merge forever, naming what is missing. A stop with NO accepted work at all (nothing was ever produced) is
/// entirely out of I3's scope — a legitimately empty-handed stop (e.g. every subtask was investigate-only) is never
/// touched by this gate.</para>
/// </summary>
public static class SupervisorPublishGate
{
    /// <summary>Null (proceed with the decision as authored) unless <paramref name="decision"/> is a <c>stop</c> I3 must reject-and-substitute — see the class doc for the ladder.</summary>
    public static SupervisorDecision? Validate(SupervisorTurnContext context, SupervisorDecision decision)
    {
        if (decision.Kind != SupervisorDecisionKinds.Stop) return null;

        var priorDecisions = context.PriorDecisions;

        var published = !string.IsNullOrEmpty(SupervisorOutcome.ReadFinalIntegratedBranch(priorDecisions))
            || SupervisorOutcome.ReadFinalRepositoryBranches(priorDecisions).Count > 0;

        if (published)
            return HasSummary(decision)
                ? null
                : IntoAskHuman("the run has published work but no summary — provide one before the run can complete");

        var frontier = SupervisorOutcome.FindUnpublishedFrontier(priorDecisions);

        if (frontier is null) return null;   // nothing was ever produced — I3 does not apply to an empty-handed stop

        if (!SupervisorOutcome.ReadAgentResults(frontier.OutcomeJson).Any(SupervisorOutcome.ResultShowsWork))
            return null;   // the frontier unit produced no real work (e.g. a read-only/investigate-only subtask) — I3 does not apply

        var attemptedMerge = priorDecisions
            .Where(d => d.DecisionKind == SupervisorDecisionKinds.Merge && d.Sequence > frontier.Sequence)
            .OrderByDescending(d => d.Sequence)
            .FirstOrDefault();

        if (attemptedMerge is null) return ServerAuthoredMerge();   // first attempt — auto-integrate-at-stop

        // A merge already ran after this frontier and STILL nothing published — never retry blindly, park instead.
        var reason = SupervisorOutcome.ReadIntegration(attemptedMerge.OutcomeJson)?.Reason ?? "the integration did not produce a published branch";

        return IntoAskHuman($"the run has accepted work that could not be published ({reason}) — a human must resolve this before the run can complete");
    }

    private static bool HasSummary(SupervisorDecision decision) => !string.IsNullOrWhiteSpace(ReadStopSummaryFromPayload(decision.PayloadJson));

    /// <summary>The model's OWN authored summary off a stop decision's payload — best-effort (null when absent/malformed), read from the PAYLOAD (pre-execution), never the folded outcome (<see cref="SupervisorOutcome.ReadStopSummary"/> reads that, post-execution).</summary>
    private static string? ReadStopSummaryFromPayload(string payloadJson)
    {
        try { return JsonSerializer.Deserialize<SupervisorStopPayload>(payloadJson, AgentJson.Options)?.Summary; }
        catch (JsonException) { return null; }
    }

    private static SupervisorDecision ServerAuthoredMerge() => new()
    {
        Kind = SupervisorDecisionKinds.Merge,
        PayloadJson = JsonSerializer.Serialize(new SupervisorMergePayload { ForcedByPublishGate = true }, AgentJson.Options),
    };

    private static SupervisorDecision IntoAskHuman(string reason) => new()
    {
        Kind = SupervisorDecisionKinds.AskHuman,
        PayloadJson = JsonSerializer.Serialize(new SupervisorAskHumanPayload { Question = $"I3 publish gate: {reason}" }, AgentJson.Options),
    };
}
