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
    /// <summary>The delivery bridge's evaluator identity — the PUBLISH pipeline, not the acceptance grader. Bump in the same PR as any change to how manifests become delivery verdicts. Pinned by test.</summary>
    public const string DeliveryEvaluatorVersion = "publish-manifest/v1";

    private readonly CodeSpaceDbContext _db;
    private readonly ICompletionContractStore _contracts;
    private readonly Workflows.Artifacts.IArtifactStore _artifacts;

    public CompletionAssessmentComposer(CodeSpaceDbContext db, ICompletionContractStore contracts, Workflows.Artifacts.IArtifactStore artifacts)
    {
        _db = db;
        _contracts = contracts;
        _artifacts = artifacts;
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

        await WriteThroughDeliveryReceiptsAsync(workflowRunId, teamId, requirements, projection.Attempts, cancellationToken).ConfigureAwait(false);

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
        var hashesByAttempt = await ContentHashesByAttemptAsync(attempts.Select(a => a.AttemptId).ToList(), teamId, cancellationToken).ConfigureAwait(false);

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
                    EvaluatorVersion = SupervisorAcceptanceGrader.EvaluatorVersion,
                    ContentHashes = hashesByAttempt.GetValueOrDefault(results[i].AgentRunId),
                    ObservedAt = DateTimeOffset.UtcNow,
                }, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// P3b-1: the manifest→ledger DELIVERY bridge — each staked delivery obligation settles from the attempt's
    /// recorded publish manifests: <c>Pushed</c> attests arrival (Passed), <c>PatchOnly</c> attests a definite
    /// non-arrival (Failed — the patch exists but never ARRIVED; policy park and push failure both land here,
    /// fail-close), an empty-diff <c>None</c> attests nothing (the obligation stays Unknown and blocks Solved —
    /// an expected change that never materialized is a hole, not a pass). Only staked requirements settle
    /// (pre-P3b tapes mint nothing); the manifest snapshot goes to CAS as the receipt's evidence; an existing
    /// (ref, attempt, target) receipt is skipped so a re-compose never re-stores evidence or re-appends.
    /// </summary>
    private async Task WriteThroughDeliveryReceiptsAsync(Guid runId, Guid teamId, IReadOnlyList<RequirementEnvelope> requirements, IReadOnlyList<AttemptProjection> attempts, CancellationToken cancellationToken)
    {
        var stakedDelivery = requirements.Where(r => r.Kind == ContractKinds.Delivery).Select(r => r.RequirementRef).ToHashSet(StringComparer.Ordinal);
        var stakedOutput = requirements.Where(r => r.Kind == ContractKinds.Output).Select(r => r.RequirementRef).ToHashSet(StringComparer.Ordinal);

        if (stakedDelivery.Count == 0 && stakedOutput.Count == 0) return;

        var attemptsWithUnits = attempts.Where(a => a.WorkUnit?.UnitId is { Length: > 0 }).ToList();

        if (attemptsWithUnits.Count == 0) return;

        var ids = attemptsWithUnits.Select(a => a.AttemptId).ToList();
        var manifests = await _db.PublishManifest.AsNoTracking()
            .Where(m => m.TeamId == teamId && m.AgentRunId != null && ids.Contains(m.AgentRunId.Value))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var existing = (await _contracts.ListReceiptsAsync(runId, teamId, cancellationToken).ConfigureAwait(false))
            .Where(r => r.Kind is ContractKinds.Delivery or ContractKinds.Output)
            .Select(r => (r.Kind, r.RequirementRef, r.AttemptId, r.TargetRef))
            .ToHashSet();

        foreach (var attempt in attemptsWithUnits)
        {
            var deliveryRef = $"delivery:{attempt.WorkUnit!.UnitId}";
            var outputRef = $"output:{attempt.WorkUnit!.UnitId}";

            foreach (var manifest in manifests.Where(m => m.AgentRunId == attempt.AttemptId && m.PublishStateValue != PublishState.None))
            {
                var targetRef = manifest.RepositoryId?.ToString() ?? manifest.RepositoryAlias;

                var mintDelivery = stakedDelivery.Contains(deliveryRef) && !existing.Contains((ContractKinds.Delivery, deliveryRef, attempt.AttemptId, targetRef));

                // P3b-3: the output receipt attests CAPTURED BYTES only — the produced content's own hashes (the
                // recorded patch artifact, the remote-confirmed candidate sha). A manifest with neither proves no
                // capture, so it mints nothing and the obligation honestly stays Unknown; the kernel's
                // hash-upgrade hook (verdict-less Unknown + ContentHashes → Captured) is the ONLY lift.
                var capturedHashes = new[]
                {
                    manifest.PatchArtifactId is null ? null : $"patch:{manifest.PatchArtifactId}",
                    manifest.CommitSha is null ? null : $"candidate:{manifest.CommitSha}",
                }.Where(h => h is not null).Cast<string>().ToList();
                var mintOutput = stakedOutput.Contains(outputRef) && capturedHashes.Count > 0 && !existing.Contains((ContractKinds.Output, outputRef, attempt.AttemptId, targetRef));

                if (!mintDelivery && !mintOutput) continue;

                var evidence = JsonSerializer.Serialize(new
                {
                    publishState = manifest.PublishStateValue.ToString(),
                    repositoryAlias = manifest.RepositoryAlias,
                    branch = manifest.Branch,
                    commitSha = manifest.CommitSha,
                    baseSha = manifest.BaseSha,
                    patchArtifactId = manifest.PatchArtifactId,
                    publishError = manifest.PublishError,
                }, AgentJson.Options);
                var evidenceRef = await _artifacts.PutAsync(teamId, System.Text.Encoding.UTF8.GetBytes(evidence), "application/json", cancellationToken).ConfigureAwait(false);

                if (mintDelivery)
                    await _contracts.AppendReceiptAsync(runId, teamId, new ReceiptEnvelope
                    {
                        RequirementRef = deliveryRef,
                        Kind = ContractKinds.Delivery,
                        AttemptId = attempt.AttemptId,
                        WorkUnit = attempt.WorkUnit,
                        TargetRef = targetRef,
                        Disposition = manifest.PublishStateValue == PublishState.Pushed ? VerificationDisposition.Passed : VerificationDisposition.Failed,
                        Authority = ContractAuthority.ServerPolicy,
                        EvidenceRef = evidenceRef,
                        EvaluatorVersion = DeliveryEvaluatorVersion,
                        ContentHashes = new[] { manifest.BaseSha is null ? null : $"base:{manifest.BaseSha}", manifest.CommitSha is null ? null : $"candidate:{manifest.CommitSha}" }.Where(h => h is not null).Cast<string>().ToList() is { Count: > 0 } hashes ? hashes : null,
                        ObservedAt = DateTimeOffset.UtcNow,
                    }, cancellationToken).ConfigureAwait(false);

                if (mintOutput)
                    await _contracts.AppendReceiptAsync(runId, teamId, new ReceiptEnvelope
                    {
                        RequirementRef = outputRef,
                        Kind = ContractKinds.Output,
                        AttemptId = attempt.AttemptId,
                        WorkUnit = attempt.WorkUnit,
                        TargetRef = targetRef,
                        Disposition = VerificationDisposition.Unknown,
                        Authority = ContractAuthority.ServerPolicy,
                        EvidenceRef = evidenceRef,
                        EvaluatorVersion = DeliveryEvaluatorVersion,
                        ContentHashes = capturedHashes,
                        ObservedAt = DateTimeOffset.UtcNow,
                    }, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// P3a-3: WHICH bytes each attempt's verdict was minted against — the attempt's recorded manifests give the
    /// immutable base (<c>base:&lt;sha&gt;</c>) and the candidate commit (<c>candidate:&lt;sha&gt;</c>), labeled so a
    /// reader never has to guess which is which. One batched read for the whole run; an attempt with no manifest
    /// (or a manifest with no shas) binds nothing — absence stays honest, never a fabricated hash.
    /// </summary>
    private async Task<Dictionary<Guid, IReadOnlyList<string>>> ContentHashesByAttemptAsync(IReadOnlyList<Guid> attemptIds, Guid teamId, CancellationToken cancellationToken)
    {
        var manifests = await _db.PublishManifest.AsNoTracking()
            .Where(m => m.TeamId == teamId && m.AgentRunId != null && attemptIds.Contains(m.AgentRunId.Value))
            .Select(m => new { m.AgentRunId, m.RepositoryAlias, m.BaseSha, m.CommitSha })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return manifests
            .GroupBy(m => m.AgentRunId!.Value)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.OrderBy(m => m.RepositoryAlias, StringComparer.Ordinal)
                    .SelectMany(m => new[] { m.BaseSha is null ? null : $"base:{m.BaseSha}", m.CommitSha is null ? null : $"candidate:{m.CommitSha}" })
                    .Where(h => h is not null).Cast<string>().Distinct().ToList())
            .Where(kv => kv.Value.Count > 0)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }
}
