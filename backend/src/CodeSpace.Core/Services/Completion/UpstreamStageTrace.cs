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
/// candidate fact — three ledgers, one cell).
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
    /// candidate and stays silent), OR DC-3's ledger-direct head (<see cref="IsLedgerDirectHead"/>).
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
            || manifests.Any(m => m.Kind == PublishManifestKind.Integration && m.PublishStateValue == PublishState.Pushed && m.Branch is { Length: > 0 })
            || IsLedgerDirectHead(publishable, manifests);
    }

    /// <summary>
    /// DC-3's LEDGER-DIRECT head: the run's ONE accepted contributor published its own branch and no merge decision
    /// ever ran. This is a DESIGNED terminal shape, not a degenerate one — <see cref="SupervisorPublishGate"/> lets
    /// exactly it terminalize with no merge at all ("a single already-pushed, accepted agent satisfies published
    /// directly off the canonical ledger"), and <see cref="SupervisorPublishedBranchResolver"/>'s ledger-direct rung
    /// resolves that same branch as the run's publish target. With ONE contributor its branch IS the run's head:
    /// there was nothing left to combine, so the stage was exercised. Without this cell such a run parks under the
    /// Enforced default for skipping work it never had — the shape the first complete supervisor-arcs window
    /// (real-model run 29215356358) already had to teach the I3 auditor to read.
    ///
    /// <para>STRICTLY one. A contributor branch is otherwise a PARTIAL head (#1762): two accepted contributors with
    /// nothing merging them means the head omits somebody's work, so crediting either would let a run claim an
    /// integration it never performed. That case stays unevidenced and parks — which is exactly why "some Agent-kind
    /// row is Pushed" is NOT the predicate here.</para>
    ///
    /// <para>Published is <see cref="SupervisorTurnService.FoldPublishedAgentRunIds"/> — the SAME predicate the I3
    /// gate's own context is folded with, all-or-nothing per agent run, so a multi-repo contributor that published
    /// only some of its repos counts as nothing. Contributors come off the PUBLISHABLE tape (past the abandonment
    /// line, like every ledger above) minus anything <see cref="SupervisorOutcome.IsWithheldFromHead"/> rejected or
    /// waived — the same 局部綠≠整合綠 bar every other door to the head enforces.</para>
    /// </summary>
    private static bool IsLedgerDirectHead(IReadOnlyList<SupervisorPriorDecision> publishable, IReadOnlyList<PublishManifest> manifests)
    {
        var accepted = publishable
            .Where(d => SupervisorDecisionKinds.StagesAgents(d.DecisionKind))
            .SelectMany(d => SupervisorOutcome.ReadAgentResults(d.OutcomeJson))
            .Where(r => !SupervisorOutcome.IsWithheldFromHead(r))
            .Select(r => r.AgentRunId)
            .ToHashSet();

        return SupervisorTurnService.FoldPublishedAgentRunIds(manifests).Count(accepted.Contains) == 1;
    }

    /// <summary>The profile's Required upstream stages the trace does NOT evidence — non-empty means the Success claim skipped a declared stage and must park. A null trace (never derived — a legacy compose) evidences nothing: fail-close.</summary>
    public static IReadOnlyList<CompletionStage> MissingRequired(ModeProfile profile, IReadOnlySet<CompletionStage>? exercised) =>
        profile.Stages
            .Where(s => s.Value == StageRequiredness.Required && Stages.Contains(s.Key) && exercised?.Contains(s.Key) != true)
            .Select(s => s.Key)
            .OrderBy(s => s)
            .ToList();
}
