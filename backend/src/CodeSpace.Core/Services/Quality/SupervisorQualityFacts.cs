using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Quality;

namespace CodeSpace.Core.Services.Quality;

/// <summary>
/// The ADAPTER that reads ONE supervisor plan unit's already-recorded facts off the durable decision tape into the
/// pure <see cref="QualityDecisionInput"/> (P22-9b). Pure + static + DB-free: everything it reads is already folded
/// onto <see cref="SupervisorTurnContext"/> by the rehydrate, so this class introduces no second source of truth
/// and no new query.
///
/// <para><b>Every mapping below is a GRAIN decision, and P22-9a refused to make them.</b> The record it fills
/// carries facts at more than one recorded grain, and pairing two grains silently mis-stops a run — so each
/// mapping states the grain it reads and what that costs. The table:</para>
///
/// <list type="table">
///   <listheader><term>Fact</term><description>Grain read here, and the cost</description></listheader>
///   <item><term><c>Attempts</c></term><description>PER UNIT — every attempt that ever staged this subtask id, oldest first (<see cref="AttemptsFor"/>). Each attempt's own recorded verdict, so <c>ConsecutiveFailedVerdicts</c> counts back from the newest.</description></item>
///   <item><term><c>SpendSoFarUsd</c> / <c>BudgetCapUsd</c></term><description>PLAN grain, both of them — the run's realized spend and the run's cap. Deliberately NOT a per-unit spend paired with a run cap, which would subtract one thing from another; the cost is that a fan-out's second sibling sees the first sibling's bill, so the affordability row reads "this RUN cannot afford another attempt", never "this unit cannot". 9c revisits it with a per-unit cap.</description></item>
///   <item><term><c>EstimatedNextAttemptCostUsd</c></term><description>The MAXIMUM priced attempt cost for THIS unit. Conservative on purpose: a mean over a cheap first attempt under-stops, and under-stopping is the failure that spends real money. Null when no attempt of this unit priced — the affordability row then cannot fire at all.</description></item>
///   <item><term><c>CheckDeclared</c></term><description>UNIT grain — the operator's <see cref="SupervisorTurnContext.AcceptanceChecks"/> floor OR this unit's own EFFECTIVE acceptance spec. See <see cref="AnObjectiveCheckIsDeclared"/>.</description></item>
///   <item><term>the review facts</term><description>NOT MAPPED — there is no per-unit work review to map. See <see cref="NoWorkReviewIsRecorded"/>.</description></item>
/// </list>
/// </summary>
public static class SupervisorQualityFacts
{
    /// <summary>
    /// The whole turn's readings — one per ATTEMPTED unit of the newest plan, in plan order. An un-attempted unit is
    /// SKIPPED rather than carried with an empty fact set: the policy's baseline row would answer "take an ordinary
    /// attempt" for it, which is a recommendation with no evidence behind it, and reciting one line of that per
    /// pending unit would bury the units the run really has evidence about.
    ///
    /// <para>The ONE fold, called by both the rehydrate that builds a live turn's context and the golden corpus that
    /// pins the prompt — a second copy in the fixture would be a mirror of production logic with nothing detecting
    /// its drift (Rule 12.5).</para>
    /// </summary>
    public static IReadOnlyList<SupervisorUnitQualityDecision> DecideAll(SupervisorTurnContext context)
    {
        var decisions = new List<SupervisorUnitQualityDecision>();
        var effectiveAcceptance = EffectiveAcceptanceFor(context).BySubtask;

        foreach (var subtask in SupervisorRecitation.LatestPlanSubtasks(context.PriorDecisions))
        {
            var facts = For(subtask.Id, context, effectiveAcceptance);

            if (facts.AttemptCount == 0) continue;

            var decided = QualityPolicy.Decide(facts);

            decisions.Add(new SupervisorUnitQualityDecision { SubtaskId = subtask.Id, Mechanism = decided.Mechanism, Reason = decided.Reason, Facts = facts });
        }

        return decisions;
    }

    /// <summary>
    /// The run's EFFECTIVE oracle view for the newest plan — resolved ONCE per fold through the same
    /// <see cref="SupervisorAcceptanceOverlay"/> the decider's plan block and the fold's per-unit grade read, and
    /// handed to every <see cref="For"/> below rather than re-resolved per unit. Exposed (internal) so a test can
    /// reach one unit's facts through the SAME resolution production uses instead of restating those two lines,
    /// which would be a mirror of production logic with nothing detecting its drift (Rule 12.5).
    /// </summary>
    internal static SupervisorAcceptanceOverlay.EffectiveAcceptance EffectiveAcceptanceFor(SupervisorTurnContext context)
    {
        var subtasks = SupervisorRecitation.LatestPlanSubtasks(context.PriorDecisions);

        return SupervisorAcceptanceOverlay.Resolve(context.PriorDecisions, subtasks.Where(s => s.Acceptance is not null).ToDictionary(s => s.Id, s => s.Acceptance!, StringComparer.Ordinal));
    }

    /// <summary>One unit's recorded facts, against the run's already-resolved effective oracle view (<see cref="EffectiveAcceptanceFor"/>). Never throws and never queries — a unit with no attempts yields an empty <c>Attempts</c> list, which is what makes the policy fall to its baseline rather than recommend repairing a check nothing has run yet.</summary>
    public static QualityDecisionInput For(string subtaskId, SupervisorTurnContext context, IReadOnlyDictionary<string, SupervisorAcceptanceSpec> effectiveAcceptance)
    {
        var attempts = AttemptsFor(subtaskId, context.PriorDecisions);
        var latest = attempts.Count == 0 ? null : attempts[^1];

        return new QualityDecisionInput
        {
            // PLAN grain on BOTH sides of the subtraction — see the class table.
            SpendSoFarUsd = context.RunSpendUsd,
            BudgetCapUsd = context.MaxCostUsd,
            EstimatedNextAttemptCostUsd = MostExpensivePricedAttemptUsd(attempts, context.ModelPrices),
            SpendIsUndercounted = context.UnpricedSpendModel is not null,
            Attempts = attempts.Select(Fact).ToList(),
            NoProgressDecisions = context.NoProgressDecisions,
            MaxNoProgressDecisions = context.MaxNoProgressDecisions,
            CheckDeclared = AnObjectiveCheckIsDeclared(subtaskId, context, effectiveAcceptance),
            SelfClaimContradictedTheCheck = AnOverClaimStillStands(latest, subtaskId, context),
            ChangedFileCount = latest?.TotalChangedFiles ?? latest?.ChangedFiles.Count ?? 0,
            WorkspaceUnitCount = latest?.RepositoryResults.Count ?? 0,

            // The three review facts stay at their defaults DELIBERATELY, and explicitly rather than by omission —
            // see NoWorkReviewIsRecorded for why there is nothing honest to map them from.
            IndependentReviewRecorded = NoWorkReviewIsRecorded,
            IndependentReviewDisapproved = NoWorkReviewIsRecorded,
            RecordedReviewScore = null,
        };
    }

    /// <summary>
    /// There is NO per-unit independent review of the WORK anywhere in the supervisor lane today, so the review
    /// facts are false/null — and that absence is stated here rather than papered over with the nearest-looking
    /// record. The one critic that exists (<c>CriticSupervisorDeciderDecorator</c>) reviews the DECIDER'S DRAFT
    /// DECISION, at decision scope, before its side effect: it answers "is this the right next move for the run",
    /// never "is this unit's produced work good". Mapping a <c>SupervisorDecisionReview</c> onto these members
    /// would fabricate a verdict about work no reviewer ever read — and it would then be free to dispute a passing
    /// acceptance check (the disputed row) or to settle ungraded work forever (the approving row) on evidence that
    /// does not exist. Pinned by test.
    ///
    /// <para>The consequence is honest and visible: ungraded work reaches the "nothing objective can grade this
    /// yet" row and stays there, which is why the recitation renders that mechanism as a STATEMENT OF ABSENCE and
    /// offers no verb for it. Wiring a real per-unit work critic is what makes these facts mappable, and it is a
    /// separate PR — explicitly out of 9b's scope.</para>
    /// </summary>
    internal const bool NoWorkReviewIsRecorded = false;

    /// <summary>
    /// EVERY attempt that ever staged this subtask id, OLDEST FIRST — the same positional
    /// <c>subtaskIds[i] ↔ agentResults[i]</c> join <c>SupervisorRecitation.LatestAttemptFor</c> and the dependency
    /// gate use, off the SAME shared key reader (<see cref="SupervisorDependencyGate.SubtaskIdsOf"/>), so this walk
    /// can never disagree with them about WHICH result belongs to a unit — it only keeps all of them where they
    /// keep the newest. Order is load-bearing: <c>ConsecutiveFailedVerdicts</c> counts back from the LAST element,
    /// so a reversed list would read a fixed unit's old failures as a live streak.
    /// </summary>
    internal static IReadOnlyList<SupervisorAgentResult> AttemptsFor(string subtaskId, IReadOnlyList<SupervisorPriorDecision> priors)
    {
        var attempts = new List<SupervisorAgentResult>();

        foreach (var prior in priors.Where(d => SupervisorDecisionKinds.StagesAgents(d.DecisionKind)))
        {
            var index = IndexOf(SupervisorDependencyGate.SubtaskIdsOf(prior), subtaskId);

            if (index < 0) continue;

            var results = SupervisorOutcome.ReadAgentResults(prior.OutcomeJson);

            if (index < results.Count) attempts.Add(results[index]);
        }

        return attempts;
    }

    /// <summary>
    /// One attempt reduced to its recorded verdict: the EXPLICIT <c>AcceptanceVerdict</c> when the fold wrote one
    /// (its only near-term producer is the co-sign overlay's WAIVE — a human authorized forgoing verification, and
    /// the policy's own <c>Waived</c> row is what keeps that from re-reading as a pass), else the shared
    /// <c>Classify</c> over the legacy signals — the exact expression <c>SupervisorGradedReceipts</c> already
    /// builds its receipts from, work-presence read included, so a receipt and a quality fact can never classify
    /// the same row differently.
    /// </summary>
    private static QualityAttemptFact Fact(SupervisorAgentResult result) => new()
    {
        Disposition = result.AcceptanceVerdict
            ?? VerificationDispositions.Classify(result.AcceptancePassed, result.AcceptanceDetail, workPresent: AgentWorkPresence.ShowsWork(result)),
    };

    /// <summary>
    /// What one more attempt at this unit costs, estimated as the MAXIMUM over the unit's own PRICED attempts —
    /// priced meaning the attempt reported usage AND every model in it resolves to a price, the same predicate
    /// <c>SupervisorBudgetRecitation</c>'s per-unit ledger uses. Null when no attempt of this unit priced, so the
    /// affordability row cannot fire on a guess.
    /// <para>The max, not the mean, because the two fail in opposite directions and only one of them costs money: a
    /// mean over a cheap first attempt under-estimates the next one and lets the run spend past its cap, while the
    /// max over an expensive outlier merely stops early. 9c's ablation is what replaces this with a measured
    /// estimator.</para>
    /// </summary>
    private static decimal? MostExpensivePricedAttemptUsd(IReadOnlyList<SupervisorAgentResult> attempts, IReadOnlyDictionary<string, ModelPrice> modelPrices)
    {
        var priced = attempts.Where(a => IsPriced(a, modelPrices)).Select(a => SupervisorOutcome.SpendUsd(new[] { a }, modelPrices)).ToList();

        return priced.Count == 0 ? null : priced.Max();
    }

    /// <summary>
    /// Whether an objective check is DECLARED for this work, read at the UNIT grain: the operator's run-wide
    /// <see cref="SupervisorTurnContext.AcceptanceChecks"/> floor is declared, OR this unit's OWN effective
    /// acceptance spec exists — resolved through <see cref="SupervisorAcceptanceOverlay"/> (so a co-signed
    /// amendment's replacement counts, a superseded plan's does not, and a waiver — which removes the spec — reads
    /// as no declared check, where the policy's own <c>Waived</c> row is what stops the work).
    ///
    /// <para>The disjunction is the grain, and either side alone is a prompt that disagrees with itself. The FLOOR
    /// alone is what shipped first: on a run that declares none — the common case — every unit read <c>false</c>,
    /// including one whose own per-subtask oracle ran and PASSED, so the prompt carried "acceptance PASSED — this
    /// unit's definition-of-done check ran green" one screen from "no objective check can grade this work
    /// (declared: False, verdict Passed)", and the recommendation was <c>IndependentCritic</c> for work the graded
    /// receipts above it had already settled. The unit SPEC alone drops the run whose terminal head is graded only
    /// by the operator's floor. Read together, a unit is gradable when anything that can actually grade it is
    /// declared, which is the reading the receipts one block above recite.</para>
    /// </summary>
    private static bool AnObjectiveCheckIsDeclared(string subtaskId, SupervisorTurnContext context, IReadOnlyDictionary<string, SupervisorAcceptanceSpec> effectiveAcceptance) =>
        context.AcceptanceChecks is { Count: > 0 } || effectiveAcceptance.ContainsKey(subtaskId);

    /// <summary>An attempt whose cost is really known: it reported token usage and nothing in it is unpriceable. Mirrors <c>SupervisorBudgetRecitation.UnitSpend.Add</c>'s own unpriced-usage test, so the two agree about which attempts have a real figure.</summary>
    private static bool IsPriced(SupervisorAgentResult result, IReadOnlyDictionary<string, ModelPrice> modelPrices) =>
        (result.InputTokens > 0 || result.OutputTokens > 0) && SupervisorOutcome.FirstUnpricedModel(new[] { result }, modelPrices) is null;

    /// <summary>
    /// Whether the producer's self-claim contradicted the check in the ONE direction that is live evidence about
    /// this unit's next increment — an OVER-claim (the agent reported success; the check failed) — and whether that
    /// contradiction still stands.
    ///
    /// <para>NULLED by an outstanding amendment, for the identical reason the retry escalation nulls it
    /// (<c>RealSupervisorActionExecutor.ApplyRetryEscalationAsync</c>): a contradiction graded by an oracle a human
    /// has since amended is stale — the self-report never disagreed with the CO-SIGNED check, only with the dead
    /// one — so spending a stronger model on it spends real money on a verdict everyone agrees was wrong.</para>
    ///
    /// <para>UNDER-claims are deliberately NOT mapped here, though the record's own doc allows either direction. An
    /// under-claim's check PASSED, so it would satisfy the policy's disputed-pass row and recommend buying an
    /// independent critic — for a unit the plan recitation, one screen above in the same prompt, states is
    /// "objectively fine; do not retry, merge it". Two blocks of one prompt disagreeing about one row is the
    /// specific failure this lane keeps paying for, so the narrower mapping is the one that ships; widening it is
    /// additive once a critic can actually re-grade the work.</para>
    /// </summary>
    private static bool AnOverClaimStillStands(SupervisorAgentResult? latest, string subtaskId, SupervisorTurnContext context) =>
        latest?.Contradiction == AgentContradiction.OverClaim && !SupervisorAmendObligation.IsOutstanding(context, subtaskId);

    private static int IndexOf(IReadOnlyList<string> ids, string subtaskId)
    {
        for (var i = 0; i < ids.Count; i++)
            if (string.Equals(ids[i], subtaskId, StringComparison.Ordinal)) return i;

        return -1;
    }
}
