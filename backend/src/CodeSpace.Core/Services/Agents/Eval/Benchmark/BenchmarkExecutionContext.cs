using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

public sealed record BenchmarkExecutionContext
{
    public required string WorkspaceDirectory { get; init; }
    public required Guid TeamId { get; init; }
    public BenchmarkAgentSelection? Selection { get; init; }
    public IBenchmarkFixtureStager? FixtureStager { get; init; }
}

public sealed record CorpusBenchmarkRequest
{
    public required IReadOnlyList<BenchmarkTask> Tasks { get; init; }
    public required Guid TeamId { get; init; }
    public BenchmarkAgentSelection? Selection { get; init; }
    public IBenchmarkFixtureStager? FixtureStager { get; init; }
    public string? SuiteContentHash { get; init; }
}
