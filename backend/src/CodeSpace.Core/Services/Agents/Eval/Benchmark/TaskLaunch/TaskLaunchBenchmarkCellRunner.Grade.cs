using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Commands.Tasks;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark.TaskLaunch;

public sealed partial class TaskLaunchBenchmarkCellRunner
{
    private Task<List<AgentRun>> LoadAgentRunsAsync(Guid runId, CancellationToken cancellationToken) =>
        InFreshScopeAsync(scope =>
            scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId)
                .OrderBy(r => r.CreatedDate).ToListAsync(cancellationToken));

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

    /// <summary>
    /// Bring the pristine fixture directory forward to the state the Launch run actually produced: every
    /// attempt's captured unified diff, applied in dispatch order. A Launch cell's agent edits a SEPARATE cloned
    /// workspace (the real clone <c>LocalGitWorkspaceProvider</c> makes from the fixture repository this instrument
    /// staged) — never <paramref name="workspaceDirectory"/> itself — so this is what makes the SAME independent
    /// oracle the direct instrument uses gradeable here too, without a second real clone. A fan-out whose branches
    /// conflict on the same lines cannot both apply; <see cref="RunGitAsync"/> then throws, which the caller lets
    /// propagate to the corpus loop's own infra-fault handling (a grader fault, never a false solve/unsolved verdict).
    /// </summary>
    private async Task ReconstructWorkspaceAsync(string workspaceDirectory, IReadOnlyList<AgentRun> attempts, CancellationToken cancellationToken)
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

    private static BenchmarkResult BuildResult(BenchmarkTask task, BenchmarkMode mode, LaunchTaskResult launched, IReadOnlyList<AgentRun> attempts, BenchmarkGrade grade, string? observedModel)
    {
        var graded = attempts[^1];
        var gradedResult = ParseResult(graded);

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
            ReviseRounds = attempts.Sum(a => ParseResult(a)?.ReviseRounds ?? 0),
            ExitReason = gradedResult?.ExitReason,
            ObservedModel = observedModel,
            RouteEffortMode = launched.Route.EffortMode,
            RouteProjectionKind = launched.Route.ProjectionKind,
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
