using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>
/// The CORPUS orchestrator: runs an ENTIRE corpus — every <see cref="BenchmarkTask"/> through every one of its
/// <see cref="BenchmarkTask.Modes"/> — through the single-run <see cref="IBenchmarkRunner"/> instrument, staging a
/// fresh fixture per (task,mode) (the seam the runner's doc names: "the caller stages a fresh copy of the fixture per
/// (task, mode) and disposes it"), and reduces the per-pair grades into one solve-rate comparison. This is the piece
/// that turns the per-(task,mode) instrument into a benchmark with a SCORE — the reusable loop a console / an API /
/// the real-model gate all drive, so the corpus loop lives in exactly one place.
/// </summary>
public interface ICorpusBenchmarkRunner
{
    /// <summary>Run <paramref name="corpus"/> end to end under team <paramref name="teamId"/>: stage → run → grade each (task,mode), aggregate. A pair whose plumbing throws is recorded in <see cref="CorpusBenchmarkRun.Errored"/> and excluded from the solve-rate, never aborting the corpus.</summary>
    Task<CorpusBenchmarkRun> RunAsync(IReadOnlyList<BenchmarkTask> corpus, Guid teamId, CancellationToken cancellationToken);
}
