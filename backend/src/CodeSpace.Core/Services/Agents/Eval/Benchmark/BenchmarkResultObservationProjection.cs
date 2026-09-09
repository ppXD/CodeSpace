using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>The exact result-derived columns a paired benchmark observation may append after its terminal result is sealed.</summary>
public sealed record BenchmarkResultObservationProjection
{
    public string? ObservedModel { get; init; }
    public required string OutcomeState { get; init; }
    public required string OutcomeDetail { get; init; }
    public Guid? AgentRunId { get; init; }
    public bool Solved { get; init; }
    public required string RunStatus { get; init; }
    public int ReviseRounds { get; init; }
    public bool McpFullCatalog { get; init; }
    public string? ExitReason { get; init; }
    public decimal? CostUsd { get; init; }
    public bool CostIndeterminate { get; init; }
    public double? DurationSeconds { get; init; }

    public static BenchmarkResultObservationProjection FromPaired(BenchmarkResult result)
    {
        var costIndeterminate = result.CostIndeterminate || result.CostUsd is null;
        return new BenchmarkResultObservationProjection
        {
            ObservedModel = result.ObservedModel, OutcomeState = EvalSuite.ClassifyResult(result).ToString(), OutcomeDetail = result.Grade.Detail,
            AgentRunId = result.AgentRunId, Solved = result.Grade.Passed, RunStatus = result.RunStatus.ToString(), ReviseRounds = result.ReviseRounds,
            McpFullCatalog = result.McpFullCatalog, ExitReason = result.ExitReason, CostUsd = costIndeterminate ? null : result.CostUsd,
            CostIndeterminate = costIndeterminate, DurationSeconds = result.DurationSeconds,
        };
    }
}
