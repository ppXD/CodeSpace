using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Commands.Tasks;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark.TaskLaunch;

public sealed partial class TaskLaunchBenchmarkCellRunner
{
    private Task<List<AgentRun>> LoadAgentRunsAsync(Guid runId, CancellationToken cancellationToken) =>
        InFreshScopeAsync(scope =>
            scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId)
                .OrderBy(r => r.CreatedDate).ToListAsync(cancellationToken));

    /// <summary>The ACTUAL <c>WorkflowRun.CompletionEnforcementMode</c> this run was stamped with — read off the persisted row for the census, never assumed from what <see cref="LaunchAsync"/> requested (see <see cref="BenchmarkResult.CompletionMode"/>).</summary>
    private Task<string?> LoadCompletionEnforcementModeAsync(Guid runId, CancellationToken cancellationToken) =>
        InFreshScopeAsync(scope =>
            scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().Where(r => r.Id == runId)
                .Select(r => r.CompletionEnforcementMode).SingleOrDefaultAsync(cancellationToken));

    private static AgentRunResult? ParseResult(AgentRun run) =>
        string.IsNullOrWhiteSpace(run.ResultJson) ? null : JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options);

    /// <summary>
    /// The census's harness-reported observed model: the GRADED attempt's model (<see cref="BuildResult"/>'s own
    /// <c>attempts[^1]</c> pick — the SAME run whose status/exit-reason/etc. the result reads off) when it reported
    /// one, else the first EARLIER attempt (a map/spawn branch) that did. Null (unknown) when NONE reported one —
    /// never backfilled from what was requested. Internal (not private) so the graded-first-then-fallback
    /// preference is unit-pinned directly (InternalsVisibleTo) with no DB/engine needed.
    /// </summary>
    internal static string? ObservedModelOf(IReadOnlyList<AgentRun> attempts) =>
        ParseResult(attempts[^1])?.Model ?? attempts.Select(ParseResult).Select(r => r?.Model).FirstOrDefault(model => model is not null);

    internal static ReviewModelIdentity ProducerModelOf(BenchmarkAgentSelection? selection, IReadOnlyList<AgentRun> attempts)
    {
        var observed = attempts.Select(ParseResult).Select(result => result?.Model).Where(model => !string.IsNullOrWhiteSpace(model)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new ReviewModelIdentity { ModelCredentialModelId = selection?.ModelCredentialModelId, ConfiguredModel = selection?.Model, ObservedModel = observed.Count == 1 ? observed[0] : null };
    }

    /// <summary>
    /// Bring the pristine fixture directory forward to the state the Launch run actually produced: every
    /// attempt's captured unified diff, applied in dispatch order. A Launch cell's agent edits a SEPARATE cloned
    /// workspace (the real clone <c>LocalGitWorkspaceProvider</c> makes from the fixture repository this instrument
    /// staged) — never <paramref name="workspaceDirectory"/> itself — so this is what makes the SAME independent
    /// oracle the direct instrument uses gradeable here too, without a second real clone. A fan-out whose branches
    /// conflict on the same lines cannot both apply; <see cref="RunGitAsync"/> then throws, which the caller lets
    /// propagate to the corpus loop's own infra-fault handling (a grader fault, never a false solve/unsolved verdict).
    /// Internal (not private) so applying TWO real, distinct branch patches onto one fixture is integration-pinned
    /// directly (InternalsVisibleTo) without driving the whole launch/route/project/git-integrate pipeline — this
    /// method's own contract is "every attempt's diff lands", independent of whatever the production supervisor's
    /// OWN branch-integration step later does with those same branches.
    /// </summary>
    internal async Task ReconstructWorkspaceAsync(string workspaceDirectory, IReadOnlyList<AgentRun> attempts, CancellationToken cancellationToken)
    {
        foreach (var attempt in attempts)
        {
            var patch = ParseResult(attempt)?.Patch;
            if (string.IsNullOrWhiteSpace(patch)) continue;

            await ApplyPatchAsync(workspaceDirectory, patch, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ApplyPatchAsync(string workspaceDirectory, string patch, CancellationToken cancellationToken)
    {
        var patchFile = Path.Combine(Path.GetTempPath(), "cs-launch-cell-patch-" + Guid.NewGuid().ToString("N") + ".diff");

        await File.WriteAllTextAsync(patchFile, patch, cancellationToken).ConfigureAwait(false);

        try { await RunGitAsync(new[] { "apply", "--whitespace=nowarn", patchFile }, workspaceDirectory, cancellationToken).ConfigureAwait(false); }
        finally { try { File.Delete(patchFile); } catch { /* best-effort */ } }
    }

    /// <summary>
    /// Fold every attempt this cell drove + the grade into one result row.
    /// <para><b>NOTE for a multi-branch fan-out (Standard/Deep):</b> <paramref name="attempts"/> is every branch's
    /// <c>AgentRun</c>, ordered by <c>CreatedDate</c> — <c>attempts[^1]</c> (the LAST-CREATED branch) is what
    /// <see cref="BenchmarkResult.RunStatus"/>, <see cref="BenchmarkResult.AgentRunId"/> and
    /// <see cref="BenchmarkResult.ExitReason"/> describe, mirroring the direct-harness <c>BenchmarkRunner.BuildResult</c>'s
    /// own "graded attempt" convention — it does NOT mean only that branch was graded. <see cref="BenchmarkResult.Grade"/>
    /// is the objective oracle's verdict over the UNION of every branch's reconstructed patch
    /// (<see cref="ReconstructWorkspaceAsync"/> applies ALL of them), so a reader must never infer "which branch solved
    /// it" from <c>RunStatus</c> alone — a task solved by an EARLIER branch's edit still reports the LAST branch's status here.</para>
    /// </summary>
    private static BenchmarkResult BuildResult(BenchmarkTask task, BenchmarkMode mode, LaunchTaskResult launched, IReadOnlyList<AgentRun> attempts, BenchmarkGrade grade, string? observedModel, string? completionMode)
    {
        var graded = attempts[^1];
        var gradedResult = ParseResult(graded);
        var cost = BenchmarkResultCost.Sum(attempts.Select(ParseResult), launched.Route.Caps.MaxCostUsd is not null);

        return new BenchmarkResult
        {
            TaskId = task.Id,
            Mode = mode,
            AgentRunId = graded.Id,
            RunStatus = graded.Status,
            DurationSeconds = SumDuration(attempts),
            Grade = grade,
            McpFullCatalog = false,
            FormatFaultRespawns = 0,
            TokenUsage = SumTokenUsage(attempts),
            CostUsd = cost.CostUsd,
            CostIndeterminate = cost.Indeterminate,
            ReviseRounds = attempts.Sum(a => ParseResult(a)?.ReviseRounds ?? 0),
            ExitReason = gradedResult?.ExitReason,
            ObservedModel = observedModel,
            RouteEffortMode = launched.Route.EffortMode,
            RouteProjectionKind = launched.Route.ProjectionKind,
            CompletionMode = completionMode,
        };
    }

    private static double? SumDuration(IReadOnlyList<AgentRun> attempts)
    {
        var timed = attempts.Where(a => a.StartedAt is not null && a.CompletedAt is not null).ToList();

        return timed.Count == 0 ? null : timed.Sum(a => (a.CompletedAt!.Value - a.StartedAt!.Value).TotalSeconds);
    }

    private static AgentTokenUsage? SumTokenUsage(IReadOnlyList<AgentRun> attempts) =>
        attempts.Select(a => ParseResult(a)?.TokenUsage).Aggregate((AgentTokenUsage?)null, AgentRunExecutor.SumTokenUsage);
}
