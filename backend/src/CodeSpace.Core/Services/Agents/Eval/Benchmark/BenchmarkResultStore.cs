using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Messages.Agents.Benchmark;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>
/// Default <see cref="IBenchmarkResultStore"/> — one append per graded cell, priced through the SAME
/// <see cref="AgentCostPricing"/> the agent-cost plane uses so a benchmark dollar and a run dollar can never
/// disagree. CI provenance (<c>GITHUB_SHA</c> / <c>GITHUB_RUN_ID</c>) is READ, never branched on: it identifies
/// where a measurement came from, and its absence outside CI simply leaves the two columns null.
/// </summary>
public sealed class BenchmarkResultStore : IBenchmarkResultStore, IScopedDependency
{
    /// <summary>GitHub Actions' commit SHA — provenance only. Reading it changes no behaviour; a local run leaves <c>git_sha</c> null.</summary>
    public const string GitShaEnvVar = "GITHUB_SHA";

    /// <summary>GitHub Actions' run id — provenance only, so a persisted row traces back to the job that produced it.</summary>
    public const string CiRunIdEnvVar = "GITHUB_RUN_ID";

    private readonly CodeSpaceDbContext _db;

    public BenchmarkResultStore(CodeSpaceDbContext db)
    {
        _db = db;
    }

    public async Task RecordAsync(Guid teamId, string suiteVersion, BenchmarkResult result, BenchmarkAgentSelection? selection, CancellationToken cancellationToken)
    {
        var row = new BenchmarkResultRecord
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            SuiteVersion = suiteVersion,
            TaskId = result.TaskId,
            Mode = result.Mode.ToString(),
            Harness = selection?.Harness,
            Model = selection?.Model,
            AgentRunId = result.AgentRunId,
            Solved = result.Grade.Passed,
            RunStatus = result.RunStatus.ToString(),
            ReviseRounds = result.ReviseRounds,
            McpFullCatalog = result.McpFullCatalog,
            ExitReason = result.ExitReason,
            CostUsd = PriceOf(result, selection),
            DurationSeconds = result.DurationSeconds,
            GitSha = Env(GitShaEnvVar),
            CiRunId = Env(CiRunIdEnvVar),
        };

        _db.BenchmarkResultRecord.Add(row);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The caller (CorpusBenchmarkRunner) SWALLOWS this so a write fault cannot move the corpus verdict —
            // which means a failed row left Added in this SCOPED context would be re-attempted on the next cell's
            // SaveChanges and fail it too, turning one lost cell into every later cell in the run. Detach so the
            // failure stays contained to the cell that caused it.
            _db.Entry(row).State = EntityState.Detached;
            throw;
        }
    }

    /// <summary>The cell's priced spend — null when the run reported no usage (the deterministic fake CLI emits none) or the model is unknown to the pricer. Fail-open, never a silent $0.</summary>
    private static decimal? PriceOf(BenchmarkResult result, BenchmarkAgentSelection? selection) =>
        result.TokenUsage is { } usage ? AgentCostPricing.CostUsd(selection?.Model, usage.InputTokens, usage.OutputTokens) : null;

    private static string? Env(string name) => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)) ? null : Environment.GetEnvironmentVariable(name);
}
