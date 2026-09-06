using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Completion;

/// <summary>
/// P4 (Lock Clause 4, the matrix's row assertions): WHICH of the protocol's UPSTREAM stages a run's durable
/// evidence shows exercised — derived from the same ledgers the composer already reads, never self-reported.
/// Deliberately covers ONLY the four stages the six-state decision does not: Contract (obligations staked),
/// Plan (an authorized plan on the tape), Execute (attempts projected), Integrate (integration work that
/// LANDED — the tape's final reviewable head (<see cref="SupervisorOutcome.ReadFinalIntegratedBranch"/>), any
/// EXECUTED merge that integrated a branch (<see cref="SupervisorOutcome.AnyMergeIntegratedABranch"/>), OR the
/// run-level <c>Integration</c> manifest row a <c>git.integrate_run</c> step records, the plan-map lane's
/// candidate fact — three ledgers, one cell; <see cref="NotApplicableIntegration"/> is the same cell's fourth
/// reading, where the repository policy put the stage out of reach and nobody owes it).
/// The completion-side six (Verify/Capture/Deliver/Handoff/Assess/Terminal) are enforced by
/// <see cref="TerminalDecider"/>'s own conjuncts — 4 by trace + 6 by decider covers the ten-stage chain
/// exactly once, nothing double-encoded. Pure, so every mapping pins without a database.
/// </summary>
public static class UpstreamStageTrace
{
    /// <summary>The trace's jurisdiction — the gate consults the profile ONLY over these; the other six stages belong to the decider.</summary>
    public static readonly IReadOnlySet<CompletionStage> Stages = new HashSet<CompletionStage>
    {
        CompletionStage.Contract, CompletionStage.Plan, CompletionStage.Execute, CompletionStage.Integrate,
    };

    public static IReadOnlySet<CompletionStage> Derive(IReadOnlyList<RequirementEnvelope> requirements, IReadOnlyList<SupervisorPriorDecision> decisions, IReadOnlyList<AttemptProjection> attempts, IReadOnlyList<PublishManifest> manifests)
    {
        var exercised = new HashSet<CompletionStage>();

        if (requirements.Count > 0) exercised.Add(CompletionStage.Contract);

        if (decisions.Any(d => d.DecisionKind == SupervisorDecisionKinds.Plan && d.Status == SupervisorDecisionStatus.Succeeded)) exercised.Add(CompletionStage.Plan);

        if (attempts.Count > 0) exercised.Add(CompletionStage.Execute);

        if (HasIntegratedCandidate(decisions, manifests)) exercised.Add(CompletionStage.Integrate);

        return exercised;
    }

    /// <summary>
    /// The Integrate cell's evidence ledgers: the supervisor tape's final reviewable head, OR an EXECUTED merge that
    /// integrated a branch at any point (<see cref="SupervisorOutcome.AnyMergeIntegratedABranch"/>), OR a PUSHED
    /// run-level Integration manifest row with its branch named (a PatchOnly/branch-less row attests no reviewable
    /// candidate and stays silent).
    ///
    /// <para>The middle ledger is deliberately BARRIER-FREE while the first is not. The final-head readers answer
    /// "which head may we ship now", so they must go silent past fresh un-integrated work; this cell asks whether the
    /// run EXERCISED the stage, which a later decision cannot un-make. Without it a run that merged cleanly and then
    /// hit an unverified resolve or a refused stop parked as though it had never integrated — attributing a decider
    /// defect to missing integration work (real-model run 33755336097). A run with no merge, a conflicted merge, or a
    /// branch-less merge still evidences nothing: this widens what counts as integration WORK, never what counts as a
    /// shippable head.</para>
    ///
    /// <para>The one thing a later decision CAN un-make is a plan that declared the earlier direction abandoned
    /// (<see cref="SupervisorMergeContributors.SinceLatestAbandonment"/>) — that head is unmergeable and unpublishable
    /// by the model's own instruction, so crediting the stage off it would let a Success claim rest on a candidate no
    /// rung of the publish ladder may deliver. Every supervisor-tape ledger reads from that line; the run-level
    /// Integration manifest belongs to no generation and is untouched.</para>
    /// </summary>
    private static bool HasIntegratedCandidate(IReadOnlyList<SupervisorPriorDecision> decisions, IReadOnlyList<PublishManifest> manifests)
    {
        var publishable = SupervisorMergeContributors.SinceLatestAbandonment(decisions);

        return SupervisorOutcome.ReadFinalIntegratedBranch(publishable) is not null
            || SupervisorOutcome.ReadFinalRepositoryBranches(publishable).Count > 0
            || SupervisorOutcome.AnyMergeIntegratedABranch(publishable)
            || manifests.Any(m => m.Kind == PublishManifestKind.Integration && m.PublishStateValue == PublishState.Pushed && m.Branch is { Length: > 0 });
    }

    /// <summary>
    /// The profile's Required upstream stages the trace does NOT evidence — non-empty means the Success claim
    /// skipped a declared stage and must park. A null trace (never derived — a legacy compose) evidences nothing:
    /// fail-close. <paramref name="notApplicable"/> (<see cref="NotApplicableIntegration"/>) removes the ONE stage
    /// the run's own repository policy put out of reach: unevidenced and unowed are different verdicts, and only
    /// the first is a park.
    /// </summary>
    public static IReadOnlyList<CompletionStage> MissingRequired(ModeProfile profile, IReadOnlySet<CompletionStage>? exercised, UpstreamStageNotApplicable? notApplicable = null) =>
        profile.Stages
            .Where(s => s.Value == StageRequiredness.Required && Stages.Contains(s.Key) && exercised?.Contains(s.Key) != true && notApplicable?.Stage != s.Key)
            .Select(s => s.Key)
            .OrderBy(s => s)
            .ToList();

    /// <summary>
    /// Integrate read as NOT APPLICABLE because the run's repository policy forbids the push an integrated branch
    /// would need — null whenever the stage is genuinely owed. The dead end this closes (audit D nail 1): under a
    /// PATCH-ONLY repository the work is captured as branchless patch manifests BY POLICY, so no merge head and no
    /// run-level Integration row can ever exist; the Integrate cell read that as "missing" and parked every such
    /// run's <c>completed</c> stop forever, while <see cref="Supervisor.SupervisorPublishGate"/> one layer up
    /// deliberately RELEASES a single pushed accepted contributor with no merge at all — two authorities
    /// disagreeing about the same run.
    ///
    /// <para><b>Never for a publish-permitting repository</b>, which is what the first two clauses buy: the run
    /// must carry at least one BY-CHOICE patch-only agent row (<c>PublishState.PatchOnly</c> with no
    /// <c>PublishError</c> — the guard chain's own "we did not push, by policy" record, as against a push that was
    /// attempted and failed), and NOTHING of this run may have reached a branch. A mixed multi-repo run with a
    /// publish-permitting sibling therefore still owes the stage.</para>
    ///
    /// <para>The third clause demands that the policy block was actually ADJUDICATED or actually COVERS the work:
    /// either a human answered the delivery gate's own policy-skip card (the <see cref="SupervisorGateAdjudication"/>
    /// record both stop gates already release on), or EVERY unit on the unpublished frontier that produced
    /// head-eligible work captured a patch. Without it, a run that produced work the capture pipeline silently
    /// swallowed would read "not applicable" off one unrelated patch row.</para>
    /// </summary>
    public static UpstreamStageNotApplicable? NotApplicableIntegration(IReadOnlyList<SupervisorPriorDecision> decisions, IReadOnlyList<PublishManifest> manifests)
    {
        if (HasIntegratedCandidate(decisions, manifests)) return null;   // the stage was exercised — "not applicable" would be a lie about work that happened

        var captured = manifests.Where(m => m.Kind == PublishManifestKind.Agent && m.PublishStateValue == PublishState.PatchOnly && m.PublishError is null).ToList();

        if (captured.Count == 0 || manifests.Any(m => m.PublishStateValue == PublishState.Pushed)) return null;

        if (!AdjudicatedPolicySkip(decisions) && !EveryFrontierUnitCaptured(decisions, captured)) return null;

        return new UpstreamStageNotApplicable
        {
            Stage = CompletionStage.Integrate,
            Reason = $"integration not applicable — patch-only policy; {captured.Count} patch{(captured.Count == 1 ? string.Empty : "es")} delivered",
        };
    }

    /// <summary>Whether a human ANSWERED the delivery gate's own card about a publish-policy skip — the ONE durable record that a person was shown this repository's policy conflict and ruled on it. Read through the gates' shared adjudication surface, never re-derived, so what releases the stop and what excuses the stage can never disagree.</summary>
    private static bool AdjudicatedPolicySkip(IReadOnlyList<SupervisorPriorDecision> decisions) =>
        SupervisorGateAdjudication.AnsweredCardBlockers(decisions, SupervisorDeliveryGate.QuestionPrefix)
            .Any(blocker => blocker?.Kind == SupervisorDeliveryGateReason.PolicySkipped);

    /// <summary>Whether EVERY unit on the unpublished frontier that produced head-eligible work has a captured patch of its own — the no-contract half: nobody ruled on a policy card because no pull request was ever contracted, so the evidence has to be that the policy caught all of the work, not some of it. The frontier and the withheld bar are I3's own readers (<see cref="Supervisor.SupervisorPublishGate"/>), so "which units owed a branch" cannot drift between the gate and this cell.</summary>
    private static bool EveryFrontierUnitCaptured(IReadOnlyList<SupervisorPriorDecision> decisions, IReadOnlyList<PublishManifest> captured)
    {
        if (SupervisorOutcome.FindUnpublishedFrontier(decisions) is not { } frontier) return false;

        var owed = SupervisorOutcome.ReadAgentResults(frontier.OutcomeJson)
            .Where(r => SupervisorOutcome.ResultShowsWork(r) && !SupervisorOutcome.IsWithheldFromHead(r))
            .Select(r => r.AgentRunId)
            .ToList();

        var capturedAgentRunIds = captured.Where(m => m.AgentRunId is not null).Select(m => m.AgentRunId!.Value).ToHashSet();

        return owed.Count > 0 && owed.All(capturedAgentRunIds.Contains);
    }
}
