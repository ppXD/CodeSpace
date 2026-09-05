using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The tape mirror's outputs — the same pair <c>ComposedAssessment</c> carries out of the database: the reducer's
/// verdict, and P4's upstream stage trace. Both are needed to render the decider's stopped-now block the way
/// production renders it; a mirror that projected only the assessment showed the harness's brain four contract
/// dimensions while production's prompt also named the stage the terminal authority objects to.
/// </summary>
public sealed record TapeStoppedNow(CompletionAssessment Assessment, IReadOnlySet<CompletionStage> ExercisedUpstreamStages);

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
/// <para><b>Faithfulness boundary — read before trusting this for anything but a prompt block.</b> Delivery and
/// output receipts ARE minted here (<see cref="SupervisorDeliveryReceipts"/>), from the pushed tip, base sha and
/// publish-evidence artifact the compact now carries — the tape's mirror of the manifest row, minted where the push
/// was observed. What still cannot be mirrored, and errs conservative in every case:</para>
/// <list type="bullet">
/// <item><b>Inert:</b> acceptance receipts carry no content hashes. The reducer's hash-upgrade hook is reached only
/// through the <c>Output</c> kind, and its fold filters by kind first — an Acceptance receipt never meets it.</item>
/// <item><b>Closed, not conservative:</b> receipts DO carry a <c>WorkUnitRef</c> — the adapter reconstructs it from
/// the tape's plan ref, which the staking gate guarantees exists whenever anything is staked — so admission's
/// superseded-attempt filter is ACTIVE and a retried unit's stale failing receipt is dropped, exactly as in
/// production. Pinned by the retry arc of the direction test.</item>
/// <item><b>Conservative:</b> a single-repo compact strips the patch, so a patch-only single-repo outcome mints no
/// output receipt (artifact stays owed); and a pre-attestation tape (no pushed sha, no publish evidence) yields a
/// delivery pass with no evidence, which admission caps at InfraUnknown — owed again, never fabricated.</item>
/// </list>
/// <para>So this can read more unresolved than production, and cannot read settled where production reads
/// unresolved. Asserted, not assumed, by four drift detectors: settled-parity (a seeded published+accepted run
/// renders the IDENTICAL settled recital on both paths), owed-parity (both owe over an unpublished run), silence
/// (both silent over an unauthorized plan), and the conservative arm (a manifest invisible to the tape reads owed
/// here while production settles).</para>
/// </summary>
public static class SupervisorTapeCompletion
{
    /// <summary>
    /// The reducer's verdict on "what if this run stopped cleanly right now", plus the upstream stages the tape
    /// evidences, derived from the tape alone. Null when there is nothing to recite — no authorized wave has staked
    /// an obligation yet — which is production's own gate (<c>ComposeIfStoppedNowAsync</c> returns null on an empty
    /// requirement set), so a harness that renders this stays silent exactly where production is silent.
    /// </summary>
    public static TapeStoppedNow? ProjectIfStoppedNow(IReadOnlyList<SupervisorPriorDecision> decisions)
    {
        // The mirror stamps the LATEST ref-bearing plan's identity — the same ref production's staging chokepoint
        // read at its own stake time (the guard below already requires one to exist before anything is staked).
        var planRef = decisions.Where(d => d.DecisionKind == SupervisorDecisionKinds.Plan).Select(d => SupervisorOutcome.ReadPlanRef(d.OutcomeJson)).LastOrDefault(r => r is not null);

        var requirements = SupervisorUnitContract.BuildStakedRequirements(StakedUnits(decisions), ContractAuthority.ModelProposal, planRef);

        if (requirements.Count == 0) return null;

        var attempts = SupervisorAttemptAdapter.Project(decisions).Attempts;

        // The dispatch-time work-unit stamp, reconstructed by the adapter from the tape's own plan ref — and the
        // plan-ref staking gate above guarantees it exists whenever anything is staked. Without it on the receipts,
        // admission's superseded-attempt filter is inactive and a retried unit's STALE failing receipt aggregates
        // beside the fresh passing one — an obligation that stays owed after the very action that answered it.
        var workUnitByAttempt = attempts.Where(a => a.WorkUnit is not null).ToDictionary(a => a.AttemptId, a => a.WorkUnit!);

        var receipts = SupervisorGradedReceipts.FromTape(decisions, workUnitByAttempt)
            .Concat(SupervisorDeliveryReceipts.FromTape(decisions, requirements, workUnitByAttempt))
            .ToList();

        var admission = Completion.ReceiptAdmission.Admit(receipts, requirements, SupervisorExecutableSet.Compute(decisions), Completion.AttemptSelectors.SelectOperationalActive(attempts));

        // The SAME reader the composer feeds from rows, over the tape's own inputs. No integration manifests exist
        // off a tape, so the Integrate cell rests on the two supervisor-tape ledgers alone — conservative in the
        // direction this class already declares: it can read a stage unevidenced where production evidences it,
        // never the reverse.
        var stages = Completion.UpstreamStageTrace.Derive(requirements, decisions, attempts, []);

        return new TapeStoppedNow(Completion.CompletionReducer.Reduce(requirements, admission.Admitted, StoppedNowFacts(decisions)), stages);
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
    private static IEnumerable<(string SubtaskId, string ContractHash, bool OwesAcceptance, bool OwesDelivery)> StakedUnits(IReadOnlyList<SupervisorPriorDecision> decisions)
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

                yield return (subtaskId, SupervisorUnitContract.Hash(spec, goalOverride, repositoryId), SupervisorUnitContract.OwesAcceptance(spec), SupervisorUnitContract.OwesDelivery(spec));
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
