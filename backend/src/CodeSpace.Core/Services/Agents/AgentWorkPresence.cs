using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The definition of "this attempt produced WORK" that every reader of an ACCEPTANCE grade shares — git ground
/// truth (changed files, a pushed branch, or either on any repo of a multi-repo workspace) — over BOTH shapes an
/// attempt's result reaches such a reader in: the durable <see cref="AgentRunResult"/> the harness wrote, and the
/// <see cref="SupervisorAgentResult"/> compact the tape folds from it.
///
/// <para>It exists because "work exists" is the second argument of
/// <see cref="AgentAcceptanceContract.IsInfraFailure(string?, bool)"/> — the arm that decides whether a
/// <c>no-branch-or-repo</c> grade means "the publish failed with work in hand" (INFRA: unmeasured, a retry cannot
/// fix it) or "the agent produced nothing" (GENUINE: the fix is to do the work). Under a PATCH-ONLY repository
/// nothing is ever pushed, and the two halves of that read had drifted: the supervisor decider asked the whole
/// compact and recited "acceptance UNVERIFIED", while both RECEIPT-minting sites asked only
/// <c>ProducedBranch is not null</c> and wrote the same grade into the completion ledger as a hard
/// <see cref="Messages.Contracts.VerificationDisposition.Failed"/> — folding to Unsolved and an honest-failure
/// terminal over work that really existed.</para>
///
/// <para><b>What it unifies:</b> the <c>no-branch-or-repo</c> work-present read, everywhere that grade is
/// classified — <see cref="Supervisor.SupervisorOutcome.ResultShowsWork"/> (the decider, the evidence fold, the
/// recitation, the baseline-capture spend gate), <c>SupervisorGradedReceipts</c> (the supervisor tape's receipts),
/// and <c>CompletionAssessmentComposer</c> (the workflow-agents lane's receipts). A publish policy cannot make
/// those disagree about the same unit.</para>
///
/// <para><b>What it deliberately does NOT unify:</b> <c>AgentRunExecutor</c> keeps its own narrower reads, and they
/// are different ON PURPOSE — widening either would silently reclassify a pre-existing revise verdict, which is a
/// behaviour change wearing a refactor's clothes. <c>AgentRunExecutor.WorkPresent</c> reads changed files or an
/// inline patch and deliberately omits the branch disjunct (the lane it serves never publishes a branch for a
/// Failed run, so the disjunct could only change verdicts, never inform one); <c>AnyWorkPresent</c> is that plus
/// per-repo work, used only where D4b itself decides; <c>EscalationWorkPresent</c> is the model-escalation trigger's
/// own. For the same reason <see cref="ShowsWork(AgentRunResult)"/> omits <see cref="AgentRunResult.Patch"/> and
/// per-repo <c>Patch</c>: it exists to be BYTE-IDENTICAL to the compact overload, so the two result shapes of one
/// attempt can never grade differently. A reader that wants patch bytes counted as work wants a different fact.</para>
/// </summary>
public static class AgentWorkPresence
{
    /// <inheritdoc cref="ShowsWork(SupervisorAgentResult)"/>
    public static bool ShowsWork(AgentRunResult result) => ShowsWork(result.ChangedFiles, result.ProducedBranch, result.RepositoryResults);

    /// <summary>Whether the result shows produced WORK: changed files or a pushed branch, on the primary repo or on any repo of a multi-repo workspace.</summary>
    public static bool ShowsWork(SupervisorAgentResult result) => ShowsWork(result.ChangedFiles, result.ProducedBranch, result.RepositoryResults);

    private static bool ShowsWork(IReadOnlyList<string> changedFiles, string? producedBranch, IReadOnlyList<RepositoryRunResult> repositoryResults) =>
        changedFiles.Count > 0
        || !string.IsNullOrEmpty(producedBranch)
        || repositoryResults.Any(repo => !string.IsNullOrEmpty(repo.ProducedBranch) || repo.ChangedFiles.Count > 0);
}
