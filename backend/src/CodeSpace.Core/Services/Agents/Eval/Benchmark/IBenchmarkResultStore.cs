using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>
/// Persists ONE graded benchmark cell. Until now a corpus run's per-cell results reached the CI step summary and
/// nowhere else, so a solve rate was re-derived from scratch every run and never comparable across runs, commits,
/// or model bundles.
///
/// <para>ADDITIVE and OBSERVATION-ONLY: legacy corpus reports remain independent of this store. Paired qualification
/// requires every cell to append successfully before it may produce a claim, because an unauditable comparison is
/// not qualification evidence.</para>
/// </summary>
public interface IBenchmarkResultStore
{
    /// <summary>Append one row for <paramref name="result"/>, identified by its suite's content-derived <paramref name="suiteVersion"/> and attributed to the agent <paramref name="selection"/> that attempted it (null ⇒ the deterministic fake CLI).</summary>
    Task RecordAsync(Guid teamId, string suiteVersion, BenchmarkResult result, BenchmarkAgentSelection? selection, CancellationToken cancellationToken);

    Task RecordAsync(BenchmarkObservationWrite request, CancellationToken cancellationToken) => RecordAsync(request.TeamId, request.SuiteVersion, request.Result, request.Selection, cancellationToken);

    Task RecordInfraAsync(BenchmarkInfraObservationWrite request, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed record BenchmarkObservationWrite
{
    public required Guid TeamId { get; init; }
    public required string SuiteVersion { get; init; }
    public required BenchmarkResult Result { get; init; }
    public BenchmarkAgentSelection? Selection { get; init; }
    public Guid? ObservationGroupId { get; init; }
    public string? ObservationArm { get; init; }
    public int? ObservationSession { get; init; }
    public string? CodeRevision { get; init; }
}

public sealed record BenchmarkInfraObservationWrite
{
    public required Guid TeamId { get; init; }
    public required string SuiteVersion { get; init; }
    public required CorpusBenchmarkError Error { get; init; }
    public BenchmarkAgentSelection? Selection { get; init; }
    public Guid? ObservationGroupId { get; init; }
    public string? ObservationArm { get; init; }
    public int? ObservationSession { get; init; }
    public string? CodeRevision { get; init; }
}
