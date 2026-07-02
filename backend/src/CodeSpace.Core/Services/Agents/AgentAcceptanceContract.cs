using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The S5 acceptance contract's shared invariants — the <see cref="AgentCompletionContract"/> sibling, so EVERY
/// terminal write path (the executor's completion, the re-attach fold, the reconciler's spool recovery) applies
/// the same rule: a run whose task carries an objective contract can never land Succeeded ungraded. The
/// executor grades for real; the crash paths — where the workspace and branch died with the worker — fail
/// CLOSED with a legible detail rather than letting crash timing decide whether the oracle applied.
/// </summary>
public static class AgentAcceptanceContract
{
    /// <summary>Whether the task carries a gradable contract (a non-blank command).</summary>
    public static bool RequiresGrade(AgentTask task) =>
        task.Acceptance is { } spec && spec.Command.Any(c => !string.IsNullOrWhiteSpace(c));

    /// <summary>The fail-closed re-grade: the work (branch, diff, transcript) is preserved — the STATUS tells the truth, the contract was not met (or could not be verified).</summary>
    public static AgentRunResult FailClosed(AgentRunResult result, string? detail) => result with
    {
        Status = AgentRunStatus.Failed,
        CompletionDisposition = CompletionDisposition.Completed,
        ExitReason = "acceptance-failed",
        Error = $"The acceptance check did not pass: {detail}",
        AcceptancePassed = false,
        AcceptanceDetail = detail,
    };
}
