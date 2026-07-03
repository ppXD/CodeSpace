using System.Text.Json;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
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

    /// <summary>
    /// Validate an AUTHORED spec's kind-specific completeness (triad S7) — the single rule the plan-map node applies
    /// FAIL-LOUD at staging: an operator/model authored a contract, and silently dropping half of it (a judge with no
    /// rubric, a schema check with no schema) would invert the gate's fail-closed philosophy. Null = valid; else the
    /// legible reason. The graders independently re-enforce every rule at grade time fail-closed, so a spec that
    /// bypasses authoring validation (the supervisor lane, a raw API caller) still can never silently pass.
    /// </summary>
    public static string? ValidateAuthored(SupervisorAcceptanceSpec spec)
    {
        if (spec.Command.All(string.IsNullOrWhiteSpace))
            return "acceptance requires a non-empty command — the argv for TestsPass, the deliverable paths for every other kind.";

        switch (spec.Kind)
        {
            case BenchmarkGradingKind.LlmJudge:
                if (spec.Rubric is not { Criteria.Count: > 0 }) return "kind LlmJudge requires a rubric with at least one criterion.";
                if (spec.Rubric.Criteria.Any(c => string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Requirement))) return "every rubric criterion needs a non-blank id and requirement.";
                if (spec.Rubric.Criteria.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != spec.Rubric.Criteria.Count) return "rubric criterion ids must be distinct.";
                if (spec.Rubric.Criteria.All(c => (c.Weight ?? 1) <= 0)) return "at least one rubric criterion must carry a positive weight.";
                if (spec.Rubric.Threshold is { } threshold && (threshold <= 0 || threshold > 1)) return "rubric threshold must be in (0, 1].";
                break;

            case BenchmarkGradingKind.ArtifactSchema:
                if (spec.Schema is not { ValueKind: JsonValueKind.Object }) return "kind ArtifactSchema requires a JSON-object schema.";
                break;
        }

        return null;
    }
}
