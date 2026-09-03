using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Agents.Benchmark;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>
/// Default <see cref="ICorpusBenchmarkRunner"/>: loops the corpus (task × its modes), stages a fresh fixture per
/// pair into an isolated temp workspace via <see cref="IBenchmarkFixtureStager"/>, drives the pair through the real
/// <see cref="IBenchmarkRunner"/>, disposes the workspace, and reduces the per-pair <see cref="BenchmarkResult"/>s
/// into a per-mode solve-rate via <see cref="BenchmarkScorecard.Compute"/>. Composes the proven instrument — it does
/// NOT re-implement the run/grade/score; it only owns the cross-corpus loop + the isolated staging lifecycle.
///
/// <para><b>Resilient + honest:</b> each pair runs in its own try/finally so one pair's infra failure (a staging
/// error, a runner throw) is recorded in <see cref="CorpusBenchmarkRun.Errored"/> and the corpus CONTINUES — a
/// single flake never aborts the whole benchmark — and the errored pair is EXCLUDED from the scorecard rather than
/// silently scored as an unsolved task (the same infra-vs-capability honesty the real-model gates enforce). A caller
/// cancellation propagates (it is not a per-pair infra fault).</para>
/// </summary>
public sealed class CorpusBenchmarkRunner : ICorpusBenchmarkRunner, IScopedDependency
{
    private readonly IBenchmarkRunner _runner;
    private readonly IBenchmarkFixtureStager _stager;
    private readonly IBenchmarkResultStore _results;
    private readonly ILogger<CorpusBenchmarkRunner> _logger;

    public CorpusBenchmarkRunner(IBenchmarkRunner runner, IBenchmarkFixtureStager stager, IBenchmarkResultStore results, ILogger<CorpusBenchmarkRunner> logger)
    {
        _runner = runner;
        _stager = stager;
        _results = results;
        _logger = logger;
    }

    public async Task<CorpusBenchmarkRun> RunAsync(IReadOnlyList<BenchmarkTask> corpus, Guid teamId, BenchmarkAgentSelection? selection, CancellationToken cancellationToken)
    {
        // M1a: the suite's immutable identity + FIXED cell universe are derived BEFORE anything runs, so the
        // denominator can never shrink to whatever happened to survive — a cell the loop never reaches is still
        // a cell (InfraUnknown), and the version names exactly what any reported percentage was measured over.
        var manifest = EvalSuite.ManifestFor(corpus);

        var results = new List<BenchmarkResult>();
        var errored = new List<CorpusBenchmarkError>();

        foreach (var task in corpus)
            foreach (var mode in task.Modes)
                await RunPairAsync(task, mode, teamId, selection, manifest.Version, results, errored, cancellationToken).ConfigureAwait(false);

        return new CorpusBenchmarkRun
        {
            Results = results,
            Errored = errored,
            Scorecard = BenchmarkScorecard.Compute(results),
            SuiteVersion = manifest.Version,
            Cells = EvalSuite.Classify(manifest, results, errored),
        };
    }

    /// <summary>Stage → run → grade → PERSIST ONE (task,mode) pair in an isolated workspace; a non-cancellation throw is recorded as an infra error (the pair is excluded from the score), never aborting the corpus. The workspace is always reclaimed.</summary>
    private async Task RunPairAsync(BenchmarkTask task, BenchmarkMode mode, Guid teamId, BenchmarkAgentSelection? selection, string suiteVersion, List<BenchmarkResult> results, List<CorpusBenchmarkError> errored, CancellationToken cancellationToken)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "cs-corpus-bench-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(workspace);
            _stager.Stage(task.FixtureRef, workspace);

            var result = await _runner.RunAsync(task, mode, workspace, teamId, selection, cancellationToken).ConfigureAwait(false);

            results.Add(result);

            await PersistAsync(teamId, suiteVersion, result, selection, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Benchmark pair {TaskId}/{Mode} could not run (infra fault); recorded as errored + excluded from the score, continuing the corpus", task.Id, mode);
            errored.Add(new CorpusBenchmarkError { TaskId = task.Id, Mode = mode, Error = ex.Message });
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { /* best-effort reclaim */ }
        }
    }

    /// <summary>
    /// A4: append the cell's durable row so a solve rate is comparable across runs, commits, and model bundles
    /// instead of dying with the step summary. DELIBERATELY swallowed on failure and deliberately placed AFTER the
    /// result joins <paramref name="results"/>: the gate's verdict is computed from that in-memory list, so a
    /// persistence fault can neither red a passing corpus nor turn a failing cell into an infra error.
    /// </summary>
    private async Task PersistAsync(Guid teamId, string suiteVersion, BenchmarkResult result, BenchmarkAgentSelection? selection, CancellationToken cancellationToken)
    {
        try
        {
            await _results.RecordAsync(teamId, suiteVersion, result, selection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Benchmark cell {TaskId}/{Mode} could not be persisted; the corpus verdict is unaffected (it reads the in-memory results)", result.TaskId, result.Mode);
        }
    }
}
