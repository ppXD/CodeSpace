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
/// Plan (an authorized plan on the tape), Execute (attempts projected), Integrate (a final reviewable
/// integrated head — the tape walk (<see cref="SupervisorOutcome.ReadFinalIntegratedBranch"/>, whose stale
/// barrier means fresh un-merged work reads as NOT integrated) OR the run-level <c>Integration</c> manifest
/// row a <c>git.integrate_run</c> step records, the plan-map lane's candidate fact — two ledgers, one cell).
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

    public static IReadOnlySet<CompletionStage> Derive(IReadOnlyList<RequirementEnvelope> requirements, IReadOnlyList<SupervisorPriorDecision> decisions, IReadOnlyList<AttemptProjection> attempts, IReadOnlyList<PublishManifest> integrationManifests)
    {
        var exercised = new HashSet<CompletionStage>();

        if (requirements.Count > 0) exercised.Add(CompletionStage.Contract);

        if (decisions.Any(d => d.DecisionKind == SupervisorDecisionKinds.Plan && d.Status == SupervisorDecisionStatus.Succeeded)) exercised.Add(CompletionStage.Plan);

        if (attempts.Count > 0) exercised.Add(CompletionStage.Execute);

        if (HasIntegratedCandidate(decisions, integrationManifests)) exercised.Add(CompletionStage.Integrate);

        return exercised;
    }

    /// <summary>The Integrate cell's two evidence ledgers: the supervisor tape's final reviewable head, OR a PUSHED run-level Integration manifest row with its branch named (a PatchOnly/branch-less row attests no reviewable candidate and stays silent).</summary>
    private static bool HasIntegratedCandidate(IReadOnlyList<SupervisorPriorDecision> decisions, IReadOnlyList<PublishManifest> integrationManifests) =>
        SupervisorOutcome.ReadFinalIntegratedBranch(decisions) is not null
        || SupervisorOutcome.ReadFinalRepositoryBranches(decisions).Count > 0
        || integrationManifests.Any(m => m.Kind == PublishManifestKind.Integration && m.PublishStateValue == PublishState.Pushed && m.Branch is { Length: > 0 });

    /// <summary>The profile's Required upstream stages the trace does NOT evidence — non-empty means the Success claim skipped a declared stage and must park. A null trace (never derived — a legacy compose) evidences nothing: fail-close.</summary>
    public static IReadOnlyList<CompletionStage> MissingRequired(ModeProfile profile, IReadOnlySet<CompletionStage>? exercised) =>
        profile.Stages
            .Where(s => s.Value == StageRequiredness.Required && Stages.Contains(s.Key) && exercised?.Contains(s.Key) != true)
            .Select(s => s.Key)
            .OrderBy(s => s)
            .ToList();
}
