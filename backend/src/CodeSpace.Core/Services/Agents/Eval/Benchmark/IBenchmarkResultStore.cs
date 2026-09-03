using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>
/// Persists ONE graded benchmark cell. Until now a corpus run's per-cell results reached the CI step summary and
/// nowhere else, so a solve rate was re-derived from scratch every run and never comparable across runs, commits,
/// or model bundles.
///
/// <para>ADDITIVE and OBSERVATION-ONLY: the benchmark gate's verdict is still computed from the in-memory
/// <see cref="CorpusBenchmarkRun"/>. The runner swallows a write fault, so persistence can neither fail nor pass a
/// gate that would otherwise have gone the other way.</para>
/// </summary>
public interface IBenchmarkResultStore
{
    /// <summary>Append one row for <paramref name="result"/>, identified by its suite's content-derived <paramref name="suiteVersion"/> and attributed to the agent <paramref name="selection"/> that attempted it (null ⇒ the deterministic fake CLI).</summary>
    Task RecordAsync(Guid teamId, string suiteVersion, BenchmarkResult result, BenchmarkAgentSelection? selection, CancellationToken cancellationToken);
}
