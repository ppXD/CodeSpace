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
/// PR from), or an all-Skipped result (PatchOnly policy on every repository the attempt REACHED, which CONFLICTS
/// with the operator's own PR requirement rather than satisfying it) → <c>ask_human</c> naming exactly which —
/// including WHICH repositories were skipped, never a blanket "every repository"; satisfaction is never by
/// absence (H1, the verified vacuous-success fix), and a diagnosed failure never blind-retries — the SAME
/// "diagnosed failure wins" shape I3's own <c>attemptedMerge</c> check uses. A human ANSWER to the gate's own
/// card (recognized by <see cref="QuestionPrefix"/> through <see cref="SupervisorGateAdjudication"/>, the surface
/// I3 shares; content-blind, never parsed into an authorization) buys
/// exactly ONE fresh server-authored re-attempt — the card invites fixing the blocker and only this gate can
/// re-issue a publish, so an answer that fixed the world produces the PR, not a waiver; only when the latest
/// publish already WAS the post-adjudication re-check and is STILL unsatisfied for the SAME BLOCKER the card
/// recorded does the answer stand as the interim waiver and release the stop (Phase T replaces this with
/// structured waivers carrying authority). A run with NO conversation surface force-stops with the DISTINCT
/// <see cref="SupervisorStopReasons.DeliveryAdjudicationUnavailable"/> diagnosis instead of grinding
/// unanswerable parks into a misleading no-progress stop. Fresh state (a later merge/spawn) always re-arms the
/// gate. The state-change scoping mirrors I3's own <c>Sequence > frontier.Sequence</c> restriction: a
/// publish attempt's verdict describes ONLY the branch(es) that existed at the time it ran — a LATER merge/spawn
/// can genuinely move the published frontier (a new turn-scoped branch), and trusting the STALE verdict would
/// silently skip opening a PR for that new work.</para>
///
/// <para><b>The release is anchored on WHAT the human adjudicated, not WHEN</b> — the blocker identity the card
/// recorded (a structured <see cref="SupervisorDeliveryGateReason"/>, never the card's prose, which carries
/// volatile provider errors) compared against the one the latest attempt reports. Intervening spawns/merges do
/// NOT invalidate an adjudication: live run 34001620515 answered a PatchOnly policy card, the brain then kept
/// working, and a freshness clamp anchored on the newest state-CHANGING decision re-minted the IDENTICAL card
/// forever — a dead end, because an immutable policy can never become satisfiable and re-asking it buys nothing.
/// Freshness is still enforced, by the rung ABOVE: any state change forces a FRESH publish first, so a release
/// only ever rides a verdict produced AFTER the human ruled. A genuinely NEW blocker — a <c>Failed</c> target, a
/// different repository, a different reason — is a question they have never seen, so it earns its own card.</para>
///
/// Pure + stateless, given only the replayed tape — a re-entry re-derives the identical substitution.
/// </summary>
public static class SupervisorDeliveryGate
{
    /// <summary>
    /// Every card this gate parks on carries this pinned question prefix — it is the gate's IDENTITY on the tape:
    /// the adjudication release (see <c>AdjudicatedSameBlocker</c>) recognizes its OWN answered cards by this
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
            return SupervisorGateAdjudication.AnsweredCardExists(context.PriorDecisions, QuestionPrefix, after: LastStateChangeSequence(context.PriorDecisions), before: long.MaxValue)
                ? null
                : ParkOrForceStop(context, new SupervisorDeliveryGateReason { Kind = SupervisorDeliveryGateReason.Unauthorized }, "the delivery contract requires opening a pull request, but it was never confirmed by a human or pre-declared by the operator — approve the plan once more, or open it manually from Room");

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

        // The latest publish is UNSATISFIED (failed / empty / policy-skipped) — WHICH of those is the blocker the
        // card records and a later turn adjudicates against.
        var reason = UnsatisfiedReason(pullRequests, failed);

        // An answer to THIS gate's own card buys exactly ONE fresh server re-attempt — never a direct release:
        // the card invites the human to fix the blocker (flip the publish mode, restore the provider) and the
        // ONLY actor that can re-issue a publish is this gate, so an answer that fixed the world must produce
        // the PR, not a waiver. Content-blind by design: the answer's text is never parsed into an authorization
        // (that would be prefix-laundering); Phase T replaces this interim mechanism with structured waivers.
        if (SupervisorGateAdjudication.AnsweredCardExists(context.PriorDecisions, QuestionPrefix, after: latestPublish.Sequence, before: long.MaxValue))
            return ServerAuthoredPublish(decision, effective.TargetBranch);

        // The re-check already ran and reports the SAME blocker a human has ALREADY ruled on — their answer
        // stands as the interim waiver and the stop is released. Anchored on WHAT they adjudicated, never on
        // WHERE the answer sits relative to later work: see the class doc for the live dead end that produced.
        if (SupervisorGateAdjudication.AdjudicatedSameBlocker(context.PriorDecisions, QuestionPrefix, reason, before: latestPublish.Sequence))
            return null;

        // A LEGACY answered card (parked before blocker tracking existed) can never satisfy AdjudicatedSameBlocker
        // above — what it adjudicated is unknowable — so the fresh card below must say WHY it looks like the same
        // question again, or it silently re-mints the identical words as a question nobody has ever answered.
        var reAskNotice = AnsweredLegacyCardExists(context.PriorDecisions, before: latestPublish.Sequence)
            ? $" (your earlier answer predates blocker tracking — confirm once more for: {reason.Kind})"
            : "";

        if (failed.Count > 0)
            return ParkOrForceStop(context, reason, $"a pull request could not be opened ({string.Join("; ", failed.Select(f => $"{f.Alias}: {f.Error}"))}) — fix the cause and answer to re-attempt once; if it still fails, the run completes without the pull request (it can still be opened from Room afterwards){reAskNotice}");

        // NAME the skipped repositories rather than claim "every repository here is configured patch-only" — a claim
        // this gate can never see the evidence for. A skip entry only ever describes a repository the publish
        // attempt actually reached: a publish-permitting sibling with nothing to open contributes NO entry at all,
        // so under a multi-repo run one patch-only repo's skip used to speak for repositories it knows nothing about.
        return pullRequests.Count == 0
            ? ParkOrForceStop(context, reason, $"the delivery contract requires a pull request, but the publish attempt found no published branch to open one from — answering re-attempts the publish once; if there is still nothing to open, the run completes without it{reAskNotice}")
            : ParkOrForceStop(context, reason, $"the required pull request was skipped by policy ({string.Join("; ", pullRequests.Select(p => $"{p.Alias}: {p.Error}"))}) — change the publish mode there and answer to re-attempt once; if still blocked, the run completes without the pull request{reAskNotice}");
    }

    /// <summary>
    /// WHICH blocker the latest UNSATISFIED publish attempt reports — the identity the card records and a later
    /// turn compares against. Its three arms mirror the three park arms above one-for-one (a <c>Failed</c> target
    /// outranks everything, then an empty result, then the all-skipped policy conflict), so the reason a card
    /// carries can never describe a different blocker than the sentence printed on it.
    /// </summary>
    private static SupervisorDeliveryGateReason UnsatisfiedReason(IReadOnlyList<RoomPullRequestOpened> pullRequests, IReadOnlyList<RoomPullRequestOpened> failed) =>
        failed.Count > 0 ? BlockerOn(SupervisorDeliveryGateReason.PublishFailed, failed)
            : pullRequests.Count == 0 ? new SupervisorDeliveryGateReason { Kind = SupervisorDeliveryGateReason.NothingToOpen }
            : BlockerOn(SupervisorDeliveryGateReason.PolicySkipped, pullRequests);

    /// <summary>A blocker naming the repositories <paramref name="entries"/> describe — ordinal-sorted so two attempts reporting the same repositories in a different order are the SAME blocker, not a new question.</summary>
    private static SupervisorDeliveryGateReason BlockerOn(string kind, IEnumerable<RoomPullRequestOpened> entries) => new()
    {
        Kind = kind,
        Aliases = entries.Select(p => p.Alias).OrderBy(alias => alias, StringComparer.Ordinal).ToArray(),
    };

    /// <summary>Whether an answered gate card before <paramref name="before"/> recorded NO blocker at all — a run parked before the structured blocker existed. <see cref="SupervisorGateAdjudication.AdjudicatedSameBlocker"/> can never match it (what it adjudicated is unknowable), so the fresh card minted below must tell the human why they are asked again instead of silently repeating the first card's exact words.</summary>
    private static bool AnsweredLegacyCardExists(IReadOnlyList<SupervisorPriorDecision> priorDecisions, long before) =>
        SupervisorGateAdjudication.AnsweredCardBlockers(priorDecisions, QuestionPrefix, before).Any(blocker => blocker is null);

    /// <summary>
    /// Park on the gate's own ask card — or, when the run has NO conversation surface to answer on (an ask would
    /// degrade to an unanswerable no-card self-advance and the run would grind no-progress turns to a misleading
    /// <c>NoProgress</c> stop), force-stop immediately with the DISTINCT delivery diagnosis instead — the exact
    /// fail-closed shape <c>SupervisorTurnService.GatePlanConfirmationAsync</c> uses for the identical situation.
    /// </summary>
    private static SupervisorDecision ParkOrForceStop(SupervisorTurnContext context, SupervisorDeliveryGateReason reason, string detail) =>
        context.ConversationId is null
            ? new SupervisorDecision
            {
                Kind = SupervisorDecisionKinds.Stop,
                PayloadJson = JsonSerializer.Serialize(new { reason = SupervisorStopReasons.DeliveryAdjudicationUnavailable, detail }, AgentJson.Options),
            }
            : SupervisorGateAdjudication.IntoAskHuman(QuestionPrefix, reason, detail);

    /// <summary>The newest state-changing decision's sequence (0 when none) — the freshness anchor for adjudicating an UNAUTHORIZED park, where no publish attempt exists to anchor on.</summary>
    private static long LastStateChangeSequence(IReadOnlyList<SupervisorPriorDecision> priorDecisions) =>
        priorDecisions.Where(d => d.DecisionKind == SupervisorDecisionKinds.Merge || SupervisorDecisionKinds.StagesAgents(d.DecisionKind))
            .Select(d => d.Sequence).DefaultIfEmpty(0).Max();

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
}
