using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CodeSpace.Core.Services.Completion;

/// <summary>One composed verdict: the assessment plus every integrity diagnostic met on the way (admission rejections, projection contract errors). Shadow consumers RECORD it; nothing mutates a terminal from it until P2b (Lock Clause 1).</summary>
public sealed record ComposedAssessment(CompletionAssessment Assessment, CompletionEnforcementMode Mode, IReadOnlyList<ReceiptRejection> Rejections, IReadOnlyList<string> ContractErrors);

public interface ICompletionAssessmentComposer
{
    /// <summary>Compose a TERMINAL run's completion assessment from durable facts only. Null for an absent/foreign/non-terminal run.</summary>
    Task<ComposedAssessment?> ComposeAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken);
}

/// <summary>
/// P2a-3: THE completion composer — the first live chain over P1's parts. Reads the run's own stamped policy
/// (null → the LegacyUnknown projection, old tape never re-derived), the durable requirement rows, and the
/// supervisor tape; projects attempts (<see cref="SupervisorAttemptAdapter"/>, decision-bound), derives acceptance
/// receipts from the folds' graded verdicts and WRITE-THROUGH persists them (the ledger's exactly-once constraint
/// makes every re-compose land on the first row — the bridge until P3's native producers take over), then
/// ReceiptAdmission → operational selector → the pure reducer. Facts are per-lane: the supervisor lane's orderly
/// terminal is a TERMINAL stop decision on the tape, its forced-stop reason and self-reported give-up come from
/// the stop classification; a non-supervisor lane reads orderly (its crash/completed split is deliberately
/// conflated at the engine — refined with the P4 lane adapters). COMPUTE + RECORD ONLY: nothing here touches
/// <c>WorkflowRunStatus</c> — production terminal mutation has exactly one owner and it is P2b (Lock Clause 1).
/// </summary>
public sealed class CompletionAssessmentComposer : ICompletionAssessmentComposer, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ICompletionContractStore _contracts;

    public CompletionAssessmentComposer(CodeSpaceDbContext db, ICompletionContractStore contracts)
    {
        _db = db;
        _contracts = contracts;
    }

    public async Task<ComposedAssessment?> ComposeAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken)
    {
        var run = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.Id == workflowRunId && r.TeamId == teamId)
            .Select(r => new { r.Status, r.CompletionPolicyVersion, r.CompletionEnforcementMode })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (run is null || run.Status is not (WorkflowRunStatus.Success or WorkflowRunStatus.Failure or WorkflowRunStatus.Cancelled)) return null;

        var mode = CompletionPolicy.ModeFor(run.CompletionEnforcementMode);
        var decisions = await LoadDecisionsAsync(workflowRunId, teamId, cancellationToken).ConfigureAwait(false);
        var facts = BuildFacts(run.Status, decisions);

        if (CompletionPolicy.BasisFor(run.CompletionPolicyVersion) == CompletionBasis.LegacyUnknown)
            return new ComposedAssessment(CompletionReducer.ReduceLegacy(facts), mode, Array.Empty<ReceiptRejection>(), Array.Empty<string>());

        var projection = SupervisorAttemptAdapter.Project(decisions, await StampedWorkUnitsAsync(workflowRunId, teamId, cancellationToken).ConfigureAwait(false));
        var executableSet = SupervisorExecutableSet.Compute(decisions);

        await WriteThroughGradedReceiptsAsync(workflowRunId, teamId, decisions, projection.Attempts, cancellationToken).ConfigureAwait(false);

        var requirements = await _contracts.ListRequirementsAsync(workflowRunId, teamId, cancellationToken).ConfigureAwait(false);
        var receipts = await _contracts.ListReceiptsAsync(workflowRunId, teamId, cancellationToken).ConfigureAwait(false);

        var admission = ReceiptAdmission.Admit(receipts, requirements, executableSet, AttemptSelectors.SelectOperationalActive(projection.Attempts));

        return new ComposedAssessment(CompletionReducer.Reduce(requirements, admission.Admitted, facts), mode, admission.Rejections, projection.ContractErrors);
    }

    private async Task<IReadOnlyList<SupervisorPriorDecision>> LoadDecisionsAsync(Guid runId, Guid teamId, CancellationToken cancellationToken) =>
        (await _db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId)
            .OrderBy(d => d.Sequence)
            .Select(d => new { d.Id, d.Sequence, d.DecisionKind, d.Status, d.PayloadJson, d.OutcomeJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false))
        .Select(d => new SupervisorPriorDecision { Id = d.Id, Sequence = d.Sequence, DecisionKind = d.DecisionKind, Status = d.Status, PayloadJson = d.PayloadJson, OutcomeJson = d.OutcomeJson })
        .ToList();

    /// <summary>The supervisor lane's facts come from its tape; a lane with no tape reads an orderly completion (the engine deliberately conflates crash/completed at <c>WorkflowRunStatus</c> — the P4 lane adapters refine this).</summary>
    private static CompletionRunFacts BuildFacts(WorkflowRunStatus status, IReadOnlyList<SupervisorPriorDecision> decisions)
    {
        var lastStop = decisions.LastOrDefault(d => d.DecisionKind == SupervisorDecisionKinds.Stop && SupervisorDecisionStateMachine.IsTerminal(d.Status));
        var classification = lastStop is null ? null : SupervisorOutcome.ClassifyStop(lastStop.PayloadJson, lastStop.OutcomeJson);

        return new CompletionRunFacts
        {
            TerminalStatus = status,
            HadOrderlyTerminal = decisions.Count == 0 || lastStop is not null,
            ForcedStopReason = classification?.Kind == SupervisorStopKind.Forced ? classification.Reason ?? "forced stop" : null,
            SelfReportedGiveUp = classification?.Kind == SupervisorStopKind.GaveUp,
        };
    }

    /// <summary>The dispatch-time WorkUnitRef stamps off the staged task rows — including ContractHash, which the tape alone cannot reconstruct.</summary>
    private async Task<IReadOnlyDictionary<Guid, WorkUnitRef>?> StampedWorkUnitsAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var rows = await _db.AgentRun.AsNoTracking()
            .Where(r => r.WorkflowRunId == runId && r.TeamId == teamId && r.TaskJson != null)
            .Select(r => new { r.Id, r.TaskJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var stamped = new Dictionary<Guid, WorkUnitRef>();

        foreach (var row in rows)
        {
            try
            {
                var root = JsonDocument.Parse(row.TaskJson!).RootElement;

                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("workUnit", out var unit) && unit.ValueKind == JsonValueKind.Object
                    && JsonSerializer.Deserialize<WorkUnitRef>(unit.GetRawText(), AgentJson.Options) is { } workUnit)
                    stamped[row.Id] = workUnit;
            }
            catch (JsonException) { /* a malformed task stamps nothing — the adapter falls back to tape reconstruction */ }
        }

        return stamped.Count == 0 ? null : stamped;
    }

    /// <summary>
    /// The tape→ledger receipt bridge: every graded settled unit's fold verdict becomes a durable acceptance
    /// receipt, exactly-once (the constraint dedupes every re-compose). Classification rides the ONE shared
    /// infra discriminator; the verdict came from a server-run oracle → ServerPolicy authority.
    /// </summary>
    private async Task WriteThroughGradedReceiptsAsync(Guid runId, Guid teamId, IReadOnlyList<SupervisorPriorDecision> decisions, IReadOnlyList<AttemptProjection> attempts, CancellationToken cancellationToken)
    {
        var workUnitByAttempt = attempts.Where(a => a.WorkUnit is not null).ToDictionary(a => a.AttemptId, a => a.WorkUnit!);

        foreach (var decision in decisions)
        {
            if (decision.DecisionKind is not (SupervisorDecisionKinds.Spawn or SupervisorDecisionKinds.Retry)) continue;

            if (!SupervisorDecisionStateMachine.IsTerminal(decision.Status)) continue;

            var unitIds = decision.DecisionKind == SupervisorDecisionKinds.Spawn
                ? SupervisorOutcome.ReadSpawnSubtaskIds(decision.PayloadJson)
                : SupervisorOutcome.ReadRetrySubtaskId(decision.PayloadJson) is { } id ? new[] { id } : Array.Empty<string>();
            var results = SupervisorOutcome.ReadAgentResults(decision.OutcomeJson);

            for (var i = 0; i < results.Count && i < unitIds.Count; i++)
            {
                if (string.IsNullOrEmpty(unitIds[i]) || results[i].AcceptancePassed is not { } passed) continue;

                await _contracts.AppendReceiptAsync(runId, teamId, new ReceiptEnvelope
                {
                    RequirementRef = $"acceptance:{unitIds[i]}",
                    Kind = ContractKinds.Acceptance,
                    AttemptId = results[i].AgentRunId,
                    WorkUnit = workUnitByAttempt.GetValueOrDefault(results[i].AgentRunId),
                    Disposition = VerificationDispositions.Classify(passed, results[i].AcceptanceDetail, workPresent: !string.IsNullOrEmpty(results[i].ProducedBranch)),
                    Authority = ContractAuthority.ServerPolicy,
                    EvidenceRef = results[i].AcceptanceEvidenceId,
                    ObservedAt = DateTimeOffset.UtcNow,
                }, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
