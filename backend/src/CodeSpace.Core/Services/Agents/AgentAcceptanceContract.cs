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

    /// <summary>Whether the task is expected to produce a diff/branch at all (S2). Null (the caller didn't say) defaults to <c>true</c> — byte-identical to before this field existed, so an unmarked contract still fails closed on a missing branch/repo.</summary>
    public static bool ExpectsChanges(AgentTask task) => task.ExpectsChanges ?? true;

    /// <summary>
    /// P4-U1: the single-agent lane's EFFECTIVE contract hash — what this run owes (its goal, its oracle, its
    /// change expectation), canonically hashed like the supervisor lane's <c>SupervisorUnitContract.Hash</c>.
    /// Deterministic across staking and compose (both derive it from the SAME durable TaskJson).
    /// </summary>
    public static string Hash(AgentTask task) =>
        Messages.Contracts.ContractHashing.Hash(new { instruction = task.Goal, acceptance = task.Acceptance, expectsChanges = task.ExpectsChanges }, AgentJson.Options);

    /// <summary>The <see cref="AgentRunResult.ExitReason"/> a fail-closed acceptance re-grade stamps — the machine-readable marker consumers (the agent.run node's retry verdict) key on to tell a DETERMINISTIC verdict failure from a transient death. Pinned by a unit test (Rule 8) so producer + consumer can't drift.</summary>
    public const string FailClosedExitReason = "acceptance-failed";

    /// <summary>
    /// The fail-closed re-grade: the work (branch, diff, transcript) is preserved — the STATUS tells the truth, the
    /// contract was not met (or could not be verified). Every call site guards on the result already being a
    /// would-be <see cref="AgentRunStatus.Succeeded"/> before reaching here (the grading gate returns early on any
    /// other self-reported status) — so THIS is unconditionally the over-claim correction site (P4-1): the agent
    /// believed it was done; the check disagreed. <see cref="AgentRunResult.Contradiction"/> records that fact
    /// durably, at the SAME instant the status is corrected — never re-derived ad-hoc downstream.
    /// </summary>
    public static AgentRunResult FailClosed(AgentRunResult result, string? detail) => result with
    {
        Status = AgentRunStatus.Failed,
        CompletionDisposition = CompletionDisposition.Completed,
        ExitReason = FailClosedExitReason,
        Error = $"The acceptance check did not pass: {detail}",
        AcceptancePassed = false,
        AcceptanceDetail = detail,
        Contradiction = AgentContradiction.OverClaim,
    };

    /// <summary>
    /// S2's vacuous pass: the contract's own OWNER declared no diff was expected, and none was produced — so the
    /// missing branch/repo is NOT a failure, it is the correctly-predicted outcome. Unlike <see cref="FailClosed"/>
    /// this touches ONLY the acceptance fields — the run's own terminal Status/ExitReason/Error are untouched (a
    /// Succeeded run stays Succeeded), because nothing about the run itself went wrong.
    /// </summary>
    public static AgentRunResult NotApplicable(AgentRunResult result, string detail) => result with
    {
        AcceptancePassed = true,
        AcceptanceDetail = detail,
    };

    /// <summary>
    /// Whether an acceptance-failure DETAIL is INFRASTRUCTURE-classed — the check itself could not run (or could not
    /// finish), so another agent pass can NEVER change the verdict by fixing code: a <c>grade-error:</c> is the
    /// grader's own failure; <c>clone-failed:</c> is the grading CLONE's failure (auth/network/timeout — the
    /// supervisor grader's <c>WorkspaceException</c> arm, which never wears the grade-error prefix); <c>no-rubric</c>
    /// / <c>no-schema</c> mean the SPEC was authored incomplete (an agent cannot author the missing rubric/schema);
    /// <c>tests-timed-out</c> (P3.1) is the grader's OWN wall-clock firing — a legitimately slow suite / cold-cache
    /// install hitting the timeout ceiling is an environment/workload fact, not a code defect, so it is infra
    /// regardless of <paramref name="workPresent"/> (same reasoning as the other grader-side prefixes); <c>setup-
    /// failed:</c> / <c>setup-timed-out</c> (P3.1 part 2) mean the contract's OWN setup step (installing deps, a
    /// build) never let the check run at all — the verdict was never reached, so it is infra by the same
    /// "the check machinery itself didn't function" reasoning as <c>grade-error:</c>/<c>clone-failed:</c>, not a
    /// genuine "the check ran and failed" verdict like <c>tests-failed-exit-N</c>; <c>no-branch-or-repo</c> is infra
    /// only when work EXISTS (the publish failed — with NO work the fix is to do the work, which an agent pass CAN
    /// do). The ONE classification the executor's revise loop, the supervisor's decider prompt, the recitation, the
    /// no-progress evidence fold, and the workflow node's respawn verdict all share — so "retry the agent" is never
    /// spent on a failure class a retry cannot fix, at any tier.
    /// </summary>
    /// <summary>
    /// The TYPED overload (P2a-3b): a grade whose arm minted a <see cref="Messages.Agents.Benchmark.GradeFailureClass"/>
    /// classifies by TYPE — the detail string is display. A grade without one (an arm not yet minting, a tape-stored
    /// detail) falls back to the pinned string conventions below, which retire as the remaining arms and the tape
    /// learn the type.
    /// </summary>
    public static bool IsInfraFailure(Messages.Agents.Benchmark.BenchmarkGrade grade, bool workPresent) => grade.Class switch
    {
        Messages.Agents.Benchmark.GradeFailureClass.Genuine => false,
        Messages.Agents.Benchmark.GradeFailureClass.GraderFault or Messages.Agents.Benchmark.GradeFailureClass.Environment or Messages.Agents.Benchmark.GradeFailureClass.SpecIncomplete => true,
        _ => IsInfraFailure(grade.Detail, workPresent),
    };

    public static bool IsInfraFailure(string? detail, bool workPresent)
    {
        var effective = StripRepoTag(detail);

        return effective is not null
               && (effective.StartsWith("grade-error:", StringComparison.Ordinal)
                   || effective.StartsWith("clone-failed:", StringComparison.Ordinal)
                   || effective.StartsWith("setup-failed:", StringComparison.Ordinal)
                   || effective.StartsWith("oracle-restore-failed:", StringComparison.Ordinal)
                   || effective is "no-rubric" or "no-schema" or "tests-timed-out" or "setup-timed-out"
                   || (effective == "no-branch-or-repo" && workPresent));
    }

    /// <summary>
    /// The multi-repo grade paths wrap a classifiable detail in a uniform machine-authored display tag
    /// (<c>repo 'alias': </c>) — classification must see through it, or a grader crash on one repo of a
    /// multi-repo unit reads as a genuine test failure and buys retries no retry can fix. Strips ONLY that
    /// exact tag shape (at most the leading occurrences); every other prefix stays significant.
    /// </summary>
    private static string? StripRepoTag(string? detail)
    {
        while (detail is not null && detail.StartsWith("repo '", StringComparison.Ordinal))
        {
            var end = detail.IndexOf("': ", StringComparison.Ordinal);

            if (end < 0) return detail;

            detail = detail[(end + 3)..];
        }

        return detail;
    }

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
