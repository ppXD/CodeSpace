using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The acceptance receipts a supervisor tape already attests to: every terminal spawn/retry whose folded agent result
/// carries a SERVER-graded verdict becomes one receipt against that unit's acceptance obligation.
///
/// <para>Pure over the tape. Extracted from <c>CompletionAssessmentComposer</c>'s write-through bridge so the
/// DB-backed compose and the tape-only projection (<see cref="SupervisorTapeCompletion"/>) build receipts through ONE
/// path: the composer persists what this returns, the projection reads it directly. The identity stamps the composer
/// can enrich from rows — the dispatch-time <c>WorkUnitRef</c> and the manifest content hashes — arrive as optional
/// lookups, so a caller with no rows simply passes nothing and the receipt is admitted identity-less (a warning, not
/// a drop) rather than being silently shaped differently.</para>
/// </summary>
public static class SupervisorGradedReceipts
{
    /// <summary>
    /// Build one acceptance receipt per graded unit result on the tape. <paramref name="workUnitByAttempt"/> and
    /// <paramref name="contentHashesByAttempt"/> are the row-sourced identity enrichments; both optional.
    /// <paramref name="observedAt"/> is injected so the result is a pure function of its inputs.
    /// </summary>
    public static IReadOnlyList<ReceiptEnvelope> FromTape(IReadOnlyList<SupervisorPriorDecision> decisions,
        IReadOnlyDictionary<Guid, WorkUnitRef>? workUnitByAttempt = null,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>>? contentHashesByAttempt = null,
        DateTimeOffset? observedAt = null)
    {
        var receipts = new List<ReceiptEnvelope>();
        var at = observedAt ?? DateTimeOffset.UtcNow;

        foreach (var decision in decisions)
        {
            if (decision.DecisionKind is not (SupervisorDecisionKinds.Spawn or SupervisorDecisionKinds.Retry)) continue;

            if (!SupervisorDecisionStateMachine.IsTerminal(decision.Status)) continue;

            var unitIds = UnitIds(decision);
            var results = SupervisorOutcome.ReadAgentResults(decision.OutcomeJson);

            for (var i = 0; i < results.Count && i < unitIds.Count; i++)
            {
                // B2: a WAIVED unit attests too — its receipt carries Disposition=Waived under Operator authority
                // (a human co-signed the forgo-verification; the server graded nothing, so ServerPolicy would lie),
                // and the completion kernel's existing Waived semantics apply: reducer → Abstained (never Solved),
                // TerminalDecider → NeedsReview, delivery → WaivedByPolicy → Park. Without this receipt a waived
                // unit would simply VANISH from the completion story — the zero-waive-trace hole the B0 scan named.
                var waived = SupervisorOutcome.IsWaived(results[i]);

                if (string.IsNullOrEmpty(unitIds[i]) || (!waived && results[i].AcceptancePassed is null)) continue;

                receipts.Add(new ReceiptEnvelope
                {
                    RequirementRef = $"acceptance:{unitIds[i]}",
                    Kind = ContractKinds.Acceptance,
                    AttemptId = results[i].AgentRunId,
                    WorkUnit = workUnitByAttempt?.GetValueOrDefault(results[i].AgentRunId),
                    Disposition = waived
                        ? VerificationDisposition.Waived
                        : VerificationDispositions.Classify(results[i].AcceptancePassed, results[i].AcceptanceDetail, workPresent: !string.IsNullOrEmpty(results[i].ProducedBranch)),
                    Authority = waived ? ContractAuthority.Operator : ContractAuthority.ServerPolicy,
                    EvidenceRef = results[i].AcceptanceEvidenceId,
                    EvaluatorVersion = SupervisorAcceptanceGrader.EvaluatorVersion,
                    ContentHashes = contentHashesByAttempt?.GetValueOrDefault(results[i].AgentRunId),
                    ObservedAt = at,
                });
            }
        }

        return receipts;
    }

    /// <summary>The units a staging decision answered for: a spawn's whole fan-out, a retry's single re-run unit.</summary>
    private static IReadOnlyList<string> UnitIds(SupervisorPriorDecision decision) =>
        decision.DecisionKind == SupervisorDecisionKinds.Spawn
            ? SupervisorOutcome.ReadSpawnSubtaskIds(decision.PayloadJson)
            : SupervisorOutcome.ReadRetrySubtaskId(decision.PayloadJson) is { } id ? new[] { id } : Array.Empty<string>();
}
