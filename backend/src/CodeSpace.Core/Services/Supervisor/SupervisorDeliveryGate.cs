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
/// decision. A publish already ran AFTER every such state change and EVERY target succeeded (Opened /
/// AlreadyOpened / Skipped) → proceed unchanged (the stop's own work is done; let it through). A publish already
/// ran (with nothing state-changing since) and ANY target Failed → <c>ask_human</c> naming the diagnosis, never
/// blind-retrying — the SAME "diagnosed failure wins, don't retry forever" shape I3's own <c>attemptedMerge</c>
/// check uses. The state-change scoping mirrors I3's own <c>Sequence > frontier.Sequence</c> restriction: a
/// publish attempt's verdict describes ONLY the branch(es) that existed at the time it ran — a LATER merge/spawn
/// can genuinely move the published frontier (a new turn-scoped branch), and trusting the STALE verdict would
/// silently skip opening a PR for that new work.</para>
///
/// Pure + stateless, given only the replayed tape — a re-entry re-derives the identical substitution.
/// </summary>
public static class SupervisorDeliveryGate
{
    /// <summary>Null (proceed with the decision as authored) unless <paramref name="decision"/> is a <c>stop</c> this gate must reject-and-substitute — see the class doc for the ladder.</summary>
    public static SupervisorDecision? Validate(SupervisorTurnContext context, SupervisorDecision decision)
    {
        if (decision.Kind != SupervisorDecisionKinds.Stop) return null;

        var effective = EffectiveDelivery(context);

        if (effective?.OpenPullRequest != true) return null;

        if (!IsAuthorized(context))
            return IntoAskHuman("the delivery contract requires opening a pull request, but it was never confirmed by a human or pre-declared by the operator — approve the plan once more, or open it manually from Room");

        var latestPublish = context.PriorDecisions.LastOrDefault(d => d.DecisionKind == SupervisorDecisionKinds.Publish);

        if (latestPublish is null || StateChangedSince(context.PriorDecisions, latestPublish.Sequence))
            return ServerAuthoredPublish(decision, effective.TargetBranch);

        var failed = (SupervisorOutcome.ReadPublishResult(latestPublish.OutcomeJson)?.PullRequests ?? Array.Empty<RoomPullRequestOpened>())
            .Where(p => p.Disposition == RoomPullRequestDisposition.Failed)
            .ToList();

        if (failed.Count == 0) return null;   // every target satisfied (Opened / AlreadyOpened / Skipped) — let stop through

        return IntoAskHuman($"a pull request could not be opened ({string.Join("; ", failed.Select(f => $"{f.Alias}: {f.Error}"))}) — a human must resolve this before the run can complete");
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
        PayloadJson = JsonSerializer.Serialize(new SupervisorAskHumanPayload { Question = $"Delivery gate: {reason}" }, AgentJson.Options),
    };
}
