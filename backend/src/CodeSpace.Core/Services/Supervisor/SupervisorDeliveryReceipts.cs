using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The tape-side mint of DELIVERY and OUTPUT receipts — the same attestations <c>CompletionAssessmentComposer</c>
/// mints from publish-manifest rows, sourced from the facts the compact result carries: the produced branch, the
/// provider-confirmed pushed tip, the diff base, and the publish-evidence artifact minted where the push was
/// observed. Field-for-field with the composer's mint; the settled-parity drift detector holds the two together.
///
/// <para><b>What honestly cannot be mirrored.</b> A single-repo compact carries no patch artifact id (the tape strips
/// diffs), so its OUTPUT hashes carry only the candidate sha — a patch-only single-repo outcome mints no output
/// receipt and its artifact obligation stays Unknown (owed), never fabricated. And an old tape whose results predate
/// the publish-evidence mint yields delivery passes with no evidence ref, which admission caps at InfraUnknown —
/// owed again. Both gaps err toward MORE unresolved, never a false all-clear.</para>
/// </summary>
public static class SupervisorDeliveryReceipts
{
    /// <summary>The evaluator identity stamped on tape-minted delivery/output receipts — the composer's own, shared so the two mints can never claim different authorities for the same attestation.</summary>
    public const string EvaluatorVersion = Completion.CompletionAssessmentComposer.DeliveryEvaluatorVersion;

    /// <summary>
    /// Mint delivery + output receipts for every staked obligation the tape can attest. Mirrors the composer's own
    /// skip conditions: only terminal staging decisions, only units the requirements actually staked, one receipt
    /// per (kind, requirement, attempt, target).
    /// </summary>
    public static IReadOnlyList<ReceiptEnvelope> FromTape(IReadOnlyList<SupervisorPriorDecision> decisions, IReadOnlyList<RequirementEnvelope> requirements, IReadOnlyDictionary<Guid, WorkUnitRef>? workUnitByAttempt = null, DateTimeOffset? observedAt = null)
    {
        var stakedDelivery = requirements.Where(r => r.Kind == ContractKinds.Delivery).Select(r => r.RequirementRef).ToHashSet(StringComparer.Ordinal);
        var stakedOutput = requirements.Where(r => r.Kind == ContractKinds.Output).Select(r => r.RequirementRef).ToHashSet(StringComparer.Ordinal);

        var receipts = new List<ReceiptEnvelope>();
        var minted = new HashSet<(string Kind, string Ref, Guid Attempt, string Target)>();
        var at = observedAt ?? DateTimeOffset.UtcNow;

        foreach (var decision in decisions)
        {
            if (decision.DecisionKind is not (SupervisorDecisionKinds.Spawn or SupervisorDecisionKinds.Retry)) continue;

            if (!SupervisorDecisionStateMachine.IsTerminal(decision.Status)) continue;

            var unitIds = UnitIds(decision);
            var results = SupervisorOutcome.ReadAgentResults(decision.OutcomeJson);

            for (var i = 0; i < results.Count && i < unitIds.Count; i++)
            {
                if (string.IsNullOrEmpty(unitIds[i])) continue;

                foreach (var capture in Captures(results[i]))
                    MintForCapture(receipts, minted, stakedDelivery, stakedOutput, unitIds[i], results[i].AgentRunId, workUnitByAttempt?.GetValueOrDefault(results[i].AgentRunId), capture, at);
            }
        }

        return receipts;
    }

    private static void MintForCapture(List<ReceiptEnvelope> receipts, HashSet<(string, string, Guid, string)> minted, HashSet<string> stakedDelivery, HashSet<string> stakedOutput, string unitId, Guid attemptId, WorkUnitRef? workUnit, PublishCapture capture, DateTimeOffset at)
    {
        var deliveryRef = $"delivery:{unitId}";
        var outputRef = $"output:{unitId}";

        var deliveryHashes = new[]
        {
            capture.BaseSha is null ? null : $"base:{capture.BaseSha}",
            capture.CommitSha is null ? null : $"candidate:{capture.CommitSha}",
        }.Where(h => h is not null).Cast<string>().ToList();

        if (stakedDelivery.Contains(deliveryRef) && minted.Add((ContractKinds.Delivery, deliveryRef, attemptId, capture.TargetRef)))
            receipts.Add(new ReceiptEnvelope
            {
                RequirementRef = deliveryRef,
                Kind = ContractKinds.Delivery,
                AttemptId = attemptId,
                WorkUnit = workUnit,
                TargetRef = capture.TargetRef,
                Disposition = capture.Pushed ? VerificationDisposition.Passed : VerificationDisposition.Failed,
                Authority = ContractAuthority.ServerPolicy,
                EvidenceRef = capture.PublishEvidenceId,
                EvaluatorVersion = EvaluatorVersion,
                ContentHashes = deliveryHashes.Count > 0 ? deliveryHashes : null,
                ObservedAt = at,
            });

        // The output receipt attests CAPTURED BYTES only (the composer's own rule): no hashes, no receipt — the
        // obligation honestly stays Unknown, and the kernel's hash-upgrade hook is the ONLY lift.
        var capturedHashes = new[]
        {
            capture.PatchArtifactId is null ? null : $"patch:{capture.PatchArtifactId}",
            capture.CommitSha is null ? null : $"candidate:{capture.CommitSha}",
        }.Where(h => h is not null).Cast<string>().ToList();

        if (stakedOutput.Contains(outputRef) && capturedHashes.Count > 0 && minted.Add((ContractKinds.Output, outputRef, attemptId, capture.TargetRef)))
            receipts.Add(new ReceiptEnvelope
            {
                RequirementRef = outputRef,
                Kind = ContractKinds.Output,
                AttemptId = attemptId,
                WorkUnit = workUnit,
                TargetRef = capture.TargetRef,
                Disposition = VerificationDisposition.Unknown,
                Authority = ContractAuthority.ServerPolicy,
                EvidenceRef = capture.PublishEvidenceId,
                EvaluatorVersion = EvaluatorVersion,
                ContentHashes = capturedHashes,
                ObservedAt = at,
            });
    }

    /// <summary>
    /// The publish captures one compact result attests — the tape's mirror of "this attempt's manifest rows". A
    /// multi-repo result yields one per repository entry; a single-repo result yields its top-level outcome when
    /// anything was captured (the same gate the executor's manifest persist applies).
    /// </summary>
    private static IEnumerable<PublishCapture> Captures(SupervisorAgentResult result)
    {
        if (result.RepositoryResults.Count > 0)
        {
            foreach (var repo in result.RepositoryResults)
            {
                if (repo.ChangedFiles.Count == 0 && repo.PatchArtifactId is null && repo.ProducedBranch is not { Length: > 0 }) continue;

                yield return new PublishCapture(
                    repo.RepositoryId?.ToString() ?? repo.Alias,
                    repo.ProducedBranch is { Length: > 0 },
                    repo.PushedCommitSha, repo.BaseSha, repo.PatchArtifactId, repo.PublishEvidenceId);
            }

            yield break;
        }

        if (result.ChangedFiles.Count == 0 && result.ProducedBranch is not { Length: > 0 }) yield break;

        // "primary" mirrors the executor's own single-repo manifest alias. The compact strips the patch, so a
        // single-repo capture carries no patch hash — see the class doc's honesty note.
        yield return new PublishCapture("primary", result.ProducedBranch is { Length: > 0 }, result.PushedCommitSha, result.BaseSha, null, result.PublishEvidenceId);
    }

    private sealed record PublishCapture(string TargetRef, bool Pushed, string? CommitSha, string? BaseSha, Guid? PatchArtifactId, Guid? PublishEvidenceId);

    /// <summary>The units a staging decision answered for — identical to <see cref="SupervisorGradedReceipts"/>'s reading, kept in lockstep by the shared positional convention.</summary>
    private static IReadOnlyList<string> UnitIds(SupervisorPriorDecision decision) =>
        decision.DecisionKind == SupervisorDecisionKinds.Retry
            ? SupervisorOutcome.ReadRetrySubtaskId(decision.PayloadJson) is { } one ? new[] { one } : Array.Empty<string>()
            : SupervisorOutcome.ReadSpawnSubtaskIds(decision.PayloadJson);
}
