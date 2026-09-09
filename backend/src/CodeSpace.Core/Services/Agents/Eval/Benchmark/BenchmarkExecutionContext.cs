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
    public Guid? ObservationGroupId { get; init; }
    public string? ObservationArm { get; init; }
    public int? ObservationSession { get; init; }
    public bool RequireDurableObservation { get; init; }
    public string? CodeRevision { get; init; }
}

/// <summary>Two agent selections measured cell-by-cell against one frozen corpus. Ordering is content-derived from <see cref="OrderingSeed"/> so provider-time effects are balanced without task-name rules.</summary>
public sealed record PairedCorpusBenchmarkRequest
{
    public required IReadOnlyList<BenchmarkTask> Tasks { get; init; }
    public required Guid TeamId { get; init; }
    public required BenchmarkAgentSelection Control { get; init; }
    public required BenchmarkAgentSelection Candidate { get; init; }
    public required Guid ObservationGroupId { get; init; }
    public required int ObservationSession { get; init; }
    public required string OrderingSeed { get; init; }
    public required string CodeRevision { get; init; }
    public IBenchmarkFixtureStager? FixtureStager { get; init; }
    public string? SuiteContentHash { get; init; }
    public IReadOnlyList<PairedCorpusBenchmarkCell>? SelectedCells { get; init; }
}

public sealed record PairedCorpusBenchmarkCell
{
    public required string TaskId { get; init; }
    public required BenchmarkMode Mode { get; init; }
    public required string Arm { get; init; }
}
