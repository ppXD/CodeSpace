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
/// <para><b>The substitution ladder (owner-locked):</b> a LATER merge attempt over this same frontier that actually
/// ran a real integrate step and still produced no publishable branch — a genuine, already-diagnosed conflict, a
/// publish-policy block, a missing credential — is checked FIRST and always wins: "pushed" and "cleanly integrates"
/// are independent facts about the same branch, so a diagnosed failure must never be silently overridden. Only once
/// no such diagnosed failure exists does the ladder check whether the frontier's OWN contributor(s) already have a
/// genuinely published <see cref="Messages.Agents.SupervisorTurnContext.PublishedAgentRunIds"/> entry, excluding any
/// contributor its own acceptance grade objectively rejected (P0-5) — a single already-pushed, accepted agent
/// satisfies "published" directly off the canonical <c>PublishManifest</c> ledger, with no merge required at all.
/// Only when NEITHER of those applies does the ladder fall to ONE server-authored <c>merge</c>
/// (<see cref="ServerAuthoredMerge"/>, <c>ForcedByPublishGate</c>) — integration just happens transparently this
/// turn, through the SAME publish-policy guard chain the per-agent push already respects
/// (<see cref="Executors.RealSupervisorActionExecutor"/>'s integrate path); once a merge already ran with no real
/// integrate diagnosis and STILL nothing published, the NEXT stop attempt is substituted to <c>ask_human</c> instead
/// of retrying merge forever. A stop with NO accepted work at all (nothing was ever produced) is entirely out of
/// I3's scope — a legitimately empty-handed stop (e.g. every subtask was investigate-only) is never touched by this
/// gate. An UNVERIFIED resolve is likewise out of scope: its work was never accepted (the resolver loop's own
/// withhold contract already excludes it from any merge), so I3 lets that stop through as-is rather than
/// auto-merging a recovery attempt that already failed its own verification.</para>
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

        // A frontier that IS a resolve is, by construction, an UNVERIFIED one — a verified resolve would already have
        // satisfied `published` above (SupervisorOutcome.ResolvedBranch). An unverified resolution's work was never
        // ACCEPTED in the first place (the resolver loop's own "局部綠≠整合綠" withhold contract, ResolveAgentRunIdsToMerge)
        // — auto-merging it would either re-conflict for nothing or silently integrate code that failed its own
        // verification. I3 governs PUBLISHING accepted work, not rescuing a recovery attempt that already failed.
        if (frontier.DecisionKind == SupervisorDecisionKinds.Resolve) return null;

        var frontierResults = SupervisorOutcome.ReadAgentResults(frontier.OutcomeJson);

        if (!frontierResults.Any(SupervisorOutcome.ResultShowsWork))
            return null;   // the frontier unit produced no real work (e.g. a read-only/investigate-only subtask) — I3 does not apply

        var attemptedMerge = priorDecisions
            .Where(d => d.DecisionKind == SupervisorDecisionKinds.Merge && d.Sequence > frontier.Sequence)
            .OrderByDescending(d => d.Sequence)
            .FirstOrDefault();

        // A LATER merge attempt that actually ran a real integrate step and still produced no publishable branch is
        // a definitive, ALREADY-DIAGNOSED failure (a real conflict, a publish-policy block, a missing credential —
        // ReadIntegration is non-null only once a genuine integrate attempt recorded one of these). This must be
        // checked BEFORE the raw-push shortcut below, never after: a contributor's own branch push happens at
        // agent-COMPLETION time, strictly before any later merge/integrate attempt over that SAME branch — "pushed"
        // and "cleanly integrates" are independent facts, and the diagnosed failure is the more authoritative of the
        // two. An ordinary merge that never ran a real integrate step at all (ReadIntegration null — e.g. the
        // opt-in integrate gate was off) carries no such diagnosis and falls through to the shortcut untouched.
        if (attemptedMerge is not null && SupervisorOutcome.ReadIntegration(attemptedMerge.OutcomeJson) is { } integration)
            return AskHumanForUnpublishedMerge(integration.Reason);

        // The frontier's OWN accepted contributor(s) may already have a genuinely published PublishManifest row
        // (Pushed, or an opened PR/MR) even though no SEPARATE Integration-kind manifest exists for a later merge
        // — e.g. a single-contributor accept where the model's own ordinary merge never triggered the (opt-in-gated)
        // integrate-at-stop augmentation. Recognize that DIRECTLY off the canonical ledger rather than forcing a
        // redundant server-authored re-merge, or worse, parking on a human forever with nothing that can ever change.
        // AcceptancePassed==false is excluded even when pushed: a raw push happens BEFORE the per-unit grade folds
        // (AgentRunExecutor pushes at execution time; FoldUnitAcceptanceGradeAsync grades later), so a REJECTED unit
        // can still show up as Pushed in the ledger — the same "局部綠≠整合綠" bar every other door to the head already
        // enforces (SupervisorOutcome.IsAcceptanceRejected, shared with the merge + resolver doors) must apply here too.
        if (frontierResults.Any(r => !SupervisorOutcome.IsAcceptanceRejected(r) && context.PublishedAgentRunIds.Contains(r.AgentRunId)))
            return HasSummary(decision) ? null : IntoAskHuman("the run has published work but no summary — provide one before the run can complete");

        if (attemptedMerge is null) return ServerAuthoredMerge();   // first attempt — auto-integrate-at-stop

        // A merge already ran after this frontier and STILL nothing published (and no independent raw push covers
        // it either) — never retry blindly, park instead.
        return AskHumanForUnpublishedMerge(null);
    }

    private static SupervisorDecision AskHumanForUnpublishedMerge(string? reason) =>
        IntoAskHuman($"the run has accepted work that could not be published ({reason ?? "the integration did not produce a published branch"}) — a human must resolve this before the run can complete");

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

    /// <summary>Every card THIS gate parks on carries this pinned prefix — its identity on the tape (the sibling of <see cref="SupervisorDeliveryGate.QuestionPrefix"/>). Reserved: a model-authored ask may never carry it (<see cref="SupervisorAskQuestionClamp"/>).</summary>
    public const string QuestionPrefix = "I3 publish gate: ";

    private static SupervisorDecision IntoAskHuman(string reason) => new()
    {
        Kind = SupervisorDecisionKinds.AskHuman,
        ServerAuthored = true,
        PayloadJson = JsonSerializer.Serialize(new SupervisorAskHumanPayload { Question = $"{QuestionPrefix}{reason}" }, AgentJson.Options),
    };
}
