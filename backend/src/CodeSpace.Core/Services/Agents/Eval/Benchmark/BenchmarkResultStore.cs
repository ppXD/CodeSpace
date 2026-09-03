using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Messages.Agents.Benchmark;

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
        _db.BenchmarkResultRecord.Add(new BenchmarkResultRecord
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
        });

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The cell's priced spend — null when the run reported no usage (the deterministic fake CLI emits none) or the model is unknown to the pricer. Fail-open, never a silent $0.</summary>
    private static decimal? PriceOf(BenchmarkResult result, BenchmarkAgentSelection? selection) =>
        result.TokenUsage is { } usage ? AgentCostPricing.CostUsd(selection?.Model, usage.InputTokens, usage.OutputTokens) : null;

    private static string? Env(string name) => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)) ? null : Environment.GetEnvironmentVariable(name);
}
