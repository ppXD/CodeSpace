using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Sessions.Room;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// DC-2b (deliver-at-stop enforcement) — a <c>stop</c> whose run's EFFECTIVE delivery contract obligates opening
/// a pull request is REJECTED AND SUBSTITUTED before it can terminalize, the SAME "reject and substitute" shape
/// <see cref="SupervisorPublishGate"/> (I3) uses for publishing accepted work. Runs strictly AFTER I3 in
/// <see cref="SupervisorTurnService.ApplyPostDecisionGate"/> — I3 already guarantees any stop it lets through is
/// genuinely publishing accepted work, so this gate only ever needs to ask "does the contract ALSO want a PR,
/// and is that authorized."
///
/// <para><b>Authorization (owner-locked, DC-2a/DC-2b adjudication):</b> the effective contract's
/// <c>OpenPullRequest</c> may read TRUE from either the model's own plan proposal (unconfirmed) or a genuinely
/// authorized source — ① the LATEST plan version was CONFIRMED via the S3 card while it itself named a PR
/// (<see cref="SupervisorPlanConfirmation.LastApprovedDelivery"/>), or ② the operator's OWN launch-time
/// declaration (<see cref="SupervisorTurnContext.DeliverySpec"/>). A PURE model proposal with NEITHER never
/// auto-opens — the run parks honestly instead, naming exactly why, so a human can either confirm once more or
/// open it manually from Room.</para>
///
/// <para><b>The substitution ladder:</b> nothing wants a PR (the effective contract's <c>OpenPullRequest</c>
/// isn't true) → proceed unchanged, byte-identical to pre-DC-2b. Wants one but unauthorized → <c>ask_human</c>,
/// park. Wants one, authorized, no publish attempt has run yet SINCE the last state-changing decision (a
/// merge/spawn/retry/resolve — anything that could have moved what's published) → a server-authored <c>publish</c>
/// decision. A publish already ran AFTER every such state change and a REAL pull request exists (any Opened /
/// AlreadyOpened) with no Failed target → proceed unchanged (the contract is genuinely satisfied). A publish
/// already ran and is UNSATISFIED — ANY target Failed, an EMPTY result (nothing was ever published to open a
/// PR from), or an all-Skipped result (every repo is PatchOnly policy, which CONFLICTS with the operator's own
/// PR requirement rather than satisfying it) → <c>ask_human</c> naming exactly which; satisfaction is never by
/// absence (H1, the verified vacuous-success fix), and a diagnosed failure never blind-retries — the SAME
/// "diagnosed failure wins" shape I3's own <c>attemptedMerge</c> check uses. A human ANSWER to the gate's own
/// card (recognized by <see cref="QuestionPrefix"/>; content-blind, never parsed into an authorization) buys
/// exactly ONE fresh server-authored re-attempt — the card invites fixing the blocker and only this gate can
/// re-issue a publish, so an answer that fixed the world produces the PR, not a waiver; only when the latest
/// publish already WAS the post-adjudication re-check and is still unsatisfied does the answer stand as the
/// interim waiver and release the stop (Phase T replaces this with structured waivers carrying authority). A
/// run with NO conversation surface force-stops with the DISTINCT
/// <see cref="SupervisorStopReasons.DeliveryAdjudicationUnavailable"/> diagnosis instead of grinding
/// unanswerable parks into a misleading no-progress stop. Fresh state (a later merge/spawn) always re-arms the
/// gate. The state-change scoping mirrors I3's own <c>Sequence > frontier.Sequence</c> restriction: a
/// publish attempt's verdict describes ONLY the branch(es) that existed at the time it ran — a LATER merge/spawn
/// can genuinely move the published frontier (a new turn-scoped branch), and trusting the STALE verdict would
/// silently skip opening a PR for that new work.</para>
///
/// Pure + stateless, given only the replayed tape — a re-entry re-derives the identical substitution.
/// </summary>
public static class SupervisorDeliveryGate
{
    /// <summary>
    /// Every card this gate parks on carries this pinned question prefix — it is the gate's IDENTITY on the tape:
    /// the adjudication release (see <c>HumanAdjudicatedSince</c>) recognizes its OWN answered cards by this
    /// prefix and nothing else. Renaming it orphans every in-flight parked run's release, so it is test-pinned.
    /// </summary>
    public const string QuestionPrefix = "Delivery gate: ";

    /// <summary>Null (proceed with the decision as authored) unless <paramref name="decision"/> is a <c>stop</c> this gate must reject-and-substitute — see the class doc for the ladder.</summary>
    public static SupervisorDecision? Validate(SupervisorTurnContext context, SupervisorDecision decision)
    {
        if (decision.Kind != SupervisorDecisionKinds.Stop) return null;

        var effective = EffectiveDelivery(context);

        if (effective?.OpenPullRequest != true) return null;

        if (!IsAuthorized(context))
            return AnsweredGateCardExists(context.PriorDecisions, after: LastStateChangeSequence(context.PriorDecisions), before: long.MaxValue)
                ? null
                : ParkOrForceStop(context, "the delivery contract requires opening a pull request, but it was never confirmed by a human or pre-declared by the operator — approve the plan once more, or open it manually from Room");

        var latestPublish = context.PriorDecisions.LastOrDefault(d => d.DecisionKind == SupervisorDecisionKinds.Publish);

        if (latestPublish is null || StateChangedSince(context.PriorDecisions, latestPublish.Sequence))
            return ServerAuthoredPublish(decision, effective.TargetBranch);

        var pullRequests = SupervisorOutcome.ReadPublishResult(latestPublish.OutcomeJson)?.PullRequests ?? Array.Empty<RoomPullRequestOpened>();
        var failed = pullRequests.Where(p => p.Disposition == RoomPullRequestDisposition.Failed).ToList();

        // Satisfied means a REAL pull request exists (Opened / AlreadyOpened) on every non-failed attempt path —
        // never by absence. H1 fix (verified false-green): an EMPTY result (the resolver found nothing to open
        // a PR from) used to pass the Failed-only filter vacuously, and an all-Skipped result (PatchOnly policy)
        // was silently equated with satisfaction even though the operator ALSO required a PR.
        if (failed.Count == 0 && pullRequests.Any(p => p.Disposition is RoomPullRequestDisposition.Opened or RoomPullRequestDisposition.AlreadyOpened))
            return null;

        // The latest publish is UNSATISFIED (failed / empty / policy-skipped). An answer to THIS gate's own card
        // buys exactly ONE fresh server re-attempt — never a direct release: the card invites the human to fix
        // the blocker (flip the publish mode, restore the provider) and the ONLY actor that can re-issue a
        // publish is this gate, so an answer that fixed the world must produce the PR, not a waiver. Only when
        // the latest publish ALREADY ran after an answered card (the post-adjudication re-check) and is STILL
        // unsatisfied does the answer stand as a waiver and release the stop. Content-blind by design: the
        // answer's text is never parsed into an authorization (that would be prefix-laundering); Phase T
        // replaces this interim mechanism with structured waivers carrying recorded authority.
        if (AnsweredGateCardExists(context.PriorDecisions, after: latestPublish.Sequence, before: long.MaxValue))
            return ServerAuthoredPublish(decision, effective.TargetBranch);

        if (AnsweredGateCardExists(context.PriorDecisions, after: LastStateChangeSequence(context.PriorDecisions), before: latestPublish.Sequence))
            return null;

        if (failed.Count > 0)
            return ParkOrForceStop(context, $"a pull request could not be opened ({string.Join("; ", failed.Select(f => $"{f.Alias}: {f.Error}"))}) — fix the cause and answer to re-attempt once; if it still fails, the run completes without the pull request (it can still be opened from Room afterwards)");

        return pullRequests.Count == 0
            ? ParkOrForceStop(context, "the delivery contract requires a pull request, but the publish attempt found no published branch to open one from — answering re-attempts the publish once; if there is still nothing to open, the run completes without it")
            : ParkOrForceStop(context, "every repository here is configured patch-only, so the required pull request was skipped by policy — change the publish mode and answer to re-attempt once; if still blocked, the run completes without the pull request");
    }

    /// <summary>
    /// Whether one of THIS gate's own cards (question pinned to <see cref="QuestionPrefix"/>) was ANSWERED at a
    /// sequence in (<paramref name="after"/>, <paramref name="before"/>) — the human adjudicated the delivery
    /// state that window describes. See the satisfaction rung for why an answer AFTER the latest publish re-arms
    /// one attempt while an answer BEFORE it (the already-re-checked case) releases.
    /// </summary>
    private static bool AnsweredGateCardExists(IReadOnlyList<SupervisorPriorDecision> priorDecisions, long after, long before) =>
        priorDecisions.Any(d => d.Sequence > after && d.Sequence < before
            && d.DecisionKind == SupervisorDecisionKinds.AskHuman
            && ReadQuestion(d.PayloadJson)?.StartsWith(QuestionPrefix, StringComparison.Ordinal) == true
            && SupervisorOutcome.ReadAskHumanAnswer(d.OutcomeJson) is not null);

    /// <summary>
    /// Park on the gate's own ask card — or, when the run has NO conversation surface to answer on (an ask would
    /// degrade to an unanswerable no-card self-advance and the run would grind no-progress turns to a misleading
    /// <c>NoProgress</c> stop), force-stop immediately with the DISTINCT delivery diagnosis instead — the exact
    /// fail-closed shape <c>SupervisorTurnService.GatePlanConfirmationAsync</c> uses for the identical situation.
    /// </summary>
    private static SupervisorDecision ParkOrForceStop(SupervisorTurnContext context, string reason) =>
        context.ConversationId is null
            ? new SupervisorDecision
            {
                Kind = SupervisorDecisionKinds.Stop,
                PayloadJson = JsonSerializer.Serialize(new { reason = SupervisorStopReasons.DeliveryAdjudicationUnavailable, detail = reason }, AgentJson.Options),
            }
            : IntoAskHuman(reason);

    /// <summary>The newest state-changing decision's sequence (0 when none) — the freshness anchor for adjudicating an UNAUTHORIZED park, where no publish attempt exists to anchor on.</summary>
    private static long LastStateChangeSequence(IReadOnlyList<SupervisorPriorDecision> priorDecisions) =>
        priorDecisions.Where(d => d.DecisionKind == SupervisorDecisionKinds.Merge || SupervisorDecisionKinds.StagesAgents(d.DecisionKind))
            .Select(d => d.Sequence).DefaultIfEmpty(0).Max();

    private static string? ReadQuestion(string? payloadJson)
    {
        if (payloadJson is null) return null;
        try { return JsonSerializer.Deserialize<SupervisorAskHumanPayload>(payloadJson, AgentJson.Options)?.Question; }
        catch (JsonException) { return null; }
    }

    /// <summary>Whether a merge/spawn/retry/resolve landed AFTER the given sequence — any of these could have moved what's published, making an earlier publish attempt's verdict stale (adversarial-sweep finding: an unscoped lookup let a SECOND round's genuinely new work silently skip its own PR).</summary>
    private static bool StateChangedSince(IReadOnlyList<SupervisorPriorDecision> priorDecisions, long sequence) =>
        priorDecisions.Any(d => d.Sequence > sequence && (d.DecisionKind == SupervisorDecisionKinds.Merge || SupervisorDecisionKinds.StagesAgents(d.DecisionKind)));

    /// <summary>
    /// The EFFECTIVE delivery contract, read PER FIELD from a DIFFERENT source per field — deliberately, NOT the
    /// same single read for both:
    /// <list type="bullet">
    ///   <item><c>OpenPullRequest</c> keeps ITS pre-existing "does the contract want one" semantics — the
    ///   LATEST plan's own already-clamped proposal (<see cref="SupervisorOutcome.ReadPlanDelivery"/>), even
    ///   UNCONFIRMED, falling back to the operator's own declaration when no plan exists at all. This is
    ///   intentionally permissive: <see cref="IsAuthorized"/> separately gates whether that "want" is
    ///   ACTIONABLE, so a bare unapproved proposal here only ever routes to the ask_human park below, never to a
    ///   PR.</item>
    ///   <item><c>TargetBranch</c> is sourced from WHICHEVER of <see cref="IsAuthorized"/>'s two paths actually
    ///   authorized this publish — SAME precedence, ① checked first. Path ② (the operator's OWN declaration
    ///   authorizes regardless of plan-confirmation state) trusts the operator's own declared branch first,
    ///   falling back to the LATEST plan's proposal — the operator already agreed to auto-open a PR without a
    ///   human separately confirming a branch, so the model's current plan preference stands. Path ① (a plan was
    ///   actually CONFIRMED) must use THAT approved plan's own branch specifically
    ///   (<see cref="SupervisorPlanConfirmation.LastApprovedDelivery"/>) — NEVER the tape's latest plan directly.
    ///   Adversarial-sweep finding (DC-2d, post-merge review): an earlier draft read TargetBranch off the LATEST
    ///   plan regardless of approval, which let a REJECTED later plan revision's proposed branch leak into the
    ///   opened PR even while authorization rested on an OLDER, genuinely approved plan naming a DIFFERENT
    ///   branch — landing work against a branch nobody ever approved.</item>
    /// </list>
    /// Null (no contract on either side, on either field) ⇒ null, never a fabricated value.
    /// </summary>
    private static DeliverySpec? EffectiveDelivery(SupervisorTurnContext context)
    {
        var latestPlan = SupervisorPlanConfirmation.LatestPlanDecision(context.PriorDecisions);
        var latestPlanDelivery = SupervisorOutcome.ReadPlanDelivery(latestPlan?.PayloadJson);

        var openPullRequest = latestPlanDelivery?.OpenPullRequest ?? context.DeliverySpec?.OpenPullRequest;

        var targetBranch = context.DeliverySpec?.OpenPullRequest == true
            ? context.DeliverySpec?.TargetBranch ?? latestPlanDelivery?.TargetBranch
            : SupervisorPlanConfirmation.LastApprovedDelivery(context.PriorDecisions)?.TargetBranch;

        return openPullRequest is null && targetBranch is null ? null : new DeliverySpec { OpenPullRequest = openPullRequest, TargetBranch = targetBranch };
    }

    /// <summary>Path ① (a plan naming a PR was actually CONFIRMED) OR path ② (the operator pre-declared it themselves) — a pure model proposal with neither satisfies nothing.</summary>
    private static bool IsAuthorized(SupervisorTurnContext context) =>
        context.DeliverySpec?.OpenPullRequest == true
        || SupervisorPlanConfirmation.LastApprovedDelivery(context.PriorDecisions)?.OpenPullRequest == true;

    /// <summary>Carries the REJECTED stop's own summary forward — see <see cref="SupervisorPublishPayload.StopSummary"/>'s doc for why the executor can't otherwise recover it — plus the SAME effective <paramref name="targetBranch"/> the card showed, so the executor never re-derives it independently.</summary>
    private static SupervisorDecision ServerAuthoredPublish(SupervisorDecision rejectedStop, string? targetBranch) => new()
    {
        Kind = SupervisorDecisionKinds.Publish,
        PayloadJson = JsonSerializer.Serialize(new SupervisorPublishPayload { StopSummary = ReadStopSummaryFromPayload(rejectedStop.PayloadJson), TargetBranch = targetBranch }, AgentJson.Options),
    };

    /// <summary>The model's OWN authored summary off a stop decision's payload — best-effort (null when absent/malformed), mirroring <see cref="SupervisorPublishGate"/>'s identical private reader (I3 reads the SAME field for its own summary-required check).</summary>
    private static string? ReadStopSummaryFromPayload(string payloadJson)
    {
        try { return JsonSerializer.Deserialize<SupervisorStopPayload>(payloadJson, AgentJson.Options)?.Summary; }
        catch (JsonException) { return null; }
    }

    private static SupervisorDecision IntoAskHuman(string reason) => new()
    {
        Kind = SupervisorDecisionKinds.AskHuman,
        ServerAuthored = true,
        PayloadJson = JsonSerializer.Serialize(new SupervisorAskHumanPayload { Question = $"{QuestionPrefix}{reason}" }, AgentJson.Options),
    };
}
