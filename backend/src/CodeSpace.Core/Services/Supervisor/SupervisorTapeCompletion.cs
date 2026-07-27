using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The supervisor tape's own projection into completion envelopes — the pure core of what
/// <c>CompletionAssessmentComposer</c> does with a database.
///
/// <para><b>Why it exists.</b> The decider prompt's <c>IF YOU STOPPED NOW</c> block is composed at rehydrate from
/// the DB, so the two live-model gates — which build their own turn context and have no database — never rendered
/// it, and have been scoring a prompt production does not ship. Rather than let each gate synthesize an assessment
/// (four such divergences have already cost real signal), both call this, and this calls the same authorities
/// production calls: <see cref="SupervisorUnitContract.BuildStakedRequirements"/>,
/// <see cref="SupervisorAttemptAdapter.Project"/>, <see cref="SupervisorExecutableSet.Compute"/>,
/// <see cref="SupervisorGradedReceipts.FromTape"/>, <c>ReceiptAdmission.Admit</c>, <c>CompletionReducer.Reduce</c>.
/// Nothing here re-implements a rule; the only thing it supplies is the tape-side reading of inputs the composer
/// reads from rows.</para>
///
/// <para><b>Faithfulness boundary — read before trusting this for anything but a prompt block.</b> Three inputs the
/// composer takes from durable rows are absent here. One is provably inert; the other two are absent in the
/// CONSERVATIVE direction:</para>
/// <list type="bullet">
/// <item><b>Inert:</b> acceptance receipts carry no content hashes. <c>CompletionReducer</c>'s hash-upgrade hook is
/// reached only through the <c>Output</c> kind, and its fold filters by kind first — an Acceptance receipt never
/// meets it. Omitting them cannot move a disposition at all.</item>
/// <item><b>Conservative:</b> receipts carry no <c>WorkUnitRef</c> (the dispatch-time stamp, whose ContractHash the
/// tape cannot reconstruct). Admission flags that as a warning and still admits, so the superseded-attempt filter is
/// inactive. A stale FAILING receipt admitted beside a fresh passing one aggregates to Failed — MORE unresolved,
/// never a false all-clear.</item>
/// <item><b>Conservative:</b> no delivery receipts are minted at all. Production derives them from publish-manifest
/// rows; a supervisor result carries a produced branch but not the commit sha or patch artifact a single-repo
/// manifest records, so a partial mint would attest less than it appears to. An unminted delivery obligation reads
/// Unknown, which is owed rather than settled.</item>
/// </list>
/// <para>So this can read more unresolved than production, and cannot read settled where production reads
/// unresolved. For a block whose whole job is to stop a model stopping as-if-done, that is the safe direction — and
/// it is asserted, not assumed: one drift detector seeds a real run and requires the real composer to render the
/// IDENTICAL recital, a second requires the projection to err toward owed where a manifest is invisible to it, and a
/// third requires BOTH to stay silent over a plan that was never authorized.</para>
/// </summary>
public static class SupervisorTapeCompletion
{
    /// <summary>
    /// The reducer's verdict on "what if this run stopped cleanly right now", derived from the tape alone. Null when
    /// there is nothing to recite — no authorized wave has staked an obligation yet — which is production's own gate
    /// (<c>ComposeIfStoppedNowAsync</c> returns null on an empty requirement set), so a harness that renders this
    /// stays silent exactly where production is silent.
    /// </summary>
    public static CompletionAssessment? ProjectIfStoppedNow(IReadOnlyList<SupervisorPriorDecision> decisions)
    {
        var requirements = SupervisorUnitContract.BuildStakedRequirements(StakedUnits(decisions), ContractAuthority.ModelProposal);

        if (requirements.Count == 0) return null;

        var attempts = SupervisorAttemptAdapter.Project(decisions).Attempts;
        var receipts = SupervisorGradedReceipts.FromTape(decisions);

        var admission = Completion.ReceiptAdmission.Admit(receipts, requirements, SupervisorExecutableSet.Compute(decisions), Completion.AttemptSelectors.SelectOperationalActive(attempts));

        return Completion.CompletionReducer.Reduce(requirements, admission.Admitted, StoppedNowFacts(decisions));
    }

    /// <summary>
    /// The CLEAN-STOP-NOW world: had the model chosen an orderly stop this turn, the tape WOULD carry a terminal
    /// stop — so the missing-stop degradation and any stale forced/give-up classification must not leak into the
    /// what-if. Mirrors the override the composer applies to the same shared facts reading.
    /// </summary>
    private static CompletionRunFacts StoppedNowFacts(IReadOnlyList<SupervisorPriorDecision> decisions) =>
        SupervisorCompletionFacts.FromTape(WorkflowRunStatus.Success, decisions)
            with { HadOrderlyTerminal = true, ForcedStopReason = null, SelfReportedGiveUp = false, SelfReportedAbstention = false };

    /// <summary>
    /// Every unit an authorized wave has staked an obligation for, read the way the spawn executor computes it at
    /// staking time: the unit's contract hash over its PLANNED spec plus that unit's dispatch overrides, and its
    /// delivery obligation off the same planned spec. Both sit on the tape — the plan decision's payload and the
    /// spawn decision's payload — which is why this needs no rows. A spawn naming a unit the plan never declared
    /// stakes nothing, exactly as production skips a unit with no planned spec.
    /// </summary>
    private static IEnumerable<(string SubtaskId, string ContractHash, bool OwesDelivery)> StakedUnits(IReadOnlyList<SupervisorPriorDecision> decisions)
    {
        // Production stakes only under an AUTHORIZED plan: the executor reads the last ref-bearing plan decision's
        // own recorded workPlanId off its OUTCOME and stakes nothing without one. A tape whose plans carry no ref
        // (a pre-P1a run) therefore has no obligations at all, and reciting a verdict over one would invent a
        // contract the run does not have — the opposite error from the missing block, and a worse one.
        if (!decisions.Any(d => d.DecisionKind == SupervisorDecisionKinds.Plan && SupervisorOutcome.ReadPlanRef(d.OutcomeJson) is not null)) yield break;

        var planned = PlannedSubtasks(decisions);

        if (planned.Count == 0) yield break;

        var staked = new HashSet<string>(StringComparer.Ordinal);

        foreach (var decision in decisions)
        {
            if (decision.DecisionKind != SupervisorDecisionKinds.Spawn) continue;

            var overrides = SupervisorOutcome.ReadSpawnContractOverrides(decision.PayloadJson);

            foreach (var subtaskId in SupervisorOutcome.ReadSpawnSubtaskIds(decision.PayloadJson))
            {
                if (!planned.TryGetValue(subtaskId, out var spec) || !staked.Add(subtaskId)) continue;

                var (goalOverride, repositoryId) = overrides.GetValueOrDefault(subtaskId);

                yield return (subtaskId, SupervisorUnitContract.Hash(spec, goalOverride, repositoryId), SupervisorUnitContract.OwesDelivery(spec));
            }
        }
    }

    /// <summary>The run's planned units by id, from its LATEST plan decision — a replan supersedes, matching the way the executor reads the current plan.</summary>
    private static Dictionary<string, SupervisorPlannedSubtask> PlannedSubtasks(IReadOnlyList<SupervisorPriorDecision> decisions)
    {
        var byId = new Dictionary<string, SupervisorPlannedSubtask>(StringComparer.Ordinal);

        var plan = decisions.LastOrDefault(d => d.DecisionKind == SupervisorDecisionKinds.Plan);

        if (plan is null) return byId;

        foreach (var subtask in SupervisorOutcome.ReadPlanSubtasks(plan.PayloadJson))
            byId[subtask.Id] = subtask;

        return byId;
    }
}
