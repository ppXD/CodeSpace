using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Exceptions;
using CodeSpace.Messages.Agents.Benchmark;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>
/// Default <see cref="ICorpusBenchmarkRunner"/>: loops the corpus (task × its modes), stages a fresh fixture per
/// pair into an isolated temp workspace via <see cref="IBenchmarkFixtureStager"/>, drives the pair through the real
/// <see cref="IBenchmarkRunner"/>, disposes the workspace, and reduces the per-pair <see cref="BenchmarkResult"/>s
/// into a per-mode solve-rate via <see cref="BenchmarkScorecard.Compute"/>. Composes the proven instrument — it does
/// NOT re-implement the run/grade/score; it only owns the cross-corpus loop + the isolated staging lifecycle.
///
/// <para><b>Resilient + honest:</b> each cell runs in its own try/finally so one execution fault is recorded in
/// <see cref="CorpusBenchmarkRun.Errored"/> and the corpus CONTINUES. The legacy scorecard reports capability over
/// completed cells while <see cref="CorpusBenchmarkRun.Cells"/> preserves the fixed manifest denominator; paired
/// qualification evaluates that fixed denominator and also requires every observation append to succeed. A caller
/// cancellation and a paired durable-write fault propagate.</para>
/// </summary>
public sealed class CorpusBenchmarkRunner : ICorpusBenchmarkRunner, IPairedCorpusBenchmarkRunner, IScopedDependency
{
    private readonly IBenchmarkRunner _runner;
    private readonly IBenchmarkFixtureStager _stager;
    private readonly IBenchmarkResultStore _results;
    private readonly ILogger<CorpusBenchmarkRunner> _logger;
    private readonly IPairedQualificationCellAdmissionStore _admissions;

    public CorpusBenchmarkRunner(IBenchmarkRunner runner, IBenchmarkFixtureStager stager, IBenchmarkResultStore results, ILogger<CorpusBenchmarkRunner> logger, IPairedQualificationCellAdmissionStore admissions)
    {
        _runner = runner;
        _stager = stager;
        _results = results;
        _logger = logger;
        _admissions = admissions;
    }

    public Task<CorpusBenchmarkRun> RunAsync(IReadOnlyList<BenchmarkTask> corpus, Guid teamId, BenchmarkAgentSelection? selection, CancellationToken cancellationToken) => RunAsync(new CorpusBenchmarkRequest { Tasks = corpus, TeamId = teamId, Selection = selection }, cancellationToken);

    public async Task<CorpusBenchmarkRun> RunAsync(CorpusBenchmarkRequest request, CancellationToken cancellationToken)
    {
        if (request.FixtureStager is not null && string.IsNullOrWhiteSpace(request.SuiteContentHash))
            throw new ArgumentException("A corpus fixture override requires its frozen content hash.", nameof(request));
        // M1a: the suite's immutable identity + FIXED cell universe are derived BEFORE anything runs, so the
        // denominator can never shrink to whatever happened to survive — a cell the loop never reaches is still
        // a cell (InfraUnknown), and the version names exactly what any reported percentage was measured over.
        var manifest = EvalSuite.ManifestFor(request.Tasks, request.SuiteContentHash);

        var results = new List<BenchmarkResult>();
        var errored = new List<CorpusBenchmarkError>();

        var execution = new CorpusExecution(request, manifest.Version, results, errored);
        foreach (var task in request.Tasks)
            foreach (var mode in task.Modes)
                await RunPairAsync(task, mode, execution, cancellationToken).ConfigureAwait(false);

        return Complete(manifest, results, errored);
    }

    public async Task<PairedCorpusBenchmarkRun> RunPairedAsync(PairedCorpusBenchmarkRequest request, CancellationToken cancellationToken)
    {
        ValidatePairedRequest(request);
        if (request.FixtureStager is not null && string.IsNullOrWhiteSpace(request.SuiteContentHash))
            throw new ArgumentException("A corpus fixture override requires its frozen content hash.", nameof(request));

        var manifest = EvalSuite.ManifestFor(request.Tasks, request.SuiteContentHash);
        var control = Execution(request, manifest.Version, request.Control, "control");
        var candidate = Execution(request, manifest.Version, request.Candidate, "candidate");
        var tasks = request.Tasks.ToDictionary(task => task.Id, StringComparer.Ordinal);
        var cells = manifest.Cells.OrderBy(cell => CellOrder(request.OrderingSeed, cell.TaskId, cell.Mode, request.ObservationSession), StringComparer.Ordinal).ToList();

        if (request.SelectedCells is null)
        {
            for (var index = 0; index < cells.Count; index++)
            {
                var cell = cells[index];
                var candidateFirst = (index + request.ObservationSession) % 2 == 0;
                await RunPairAsync(tasks[cell.TaskId], cell.Mode, candidateFirst ? candidate : control, cancellationToken).ConfigureAwait(false);
                await RunPairAsync(tasks[cell.TaskId], cell.Mode, candidateFirst ? control : candidate, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            var selected = ValidateSelectedCells(request.SelectedCells, manifest);
            var selectedIndex = 0;
            foreach (var cell in cells.Where(cell => selected.ContainsKey((cell.TaskId, cell.Mode))))
            {
                var arms = selected[(cell.TaskId, cell.Mode)];
                var candidateFirst = (selectedIndex++ + request.ObservationSession) % 2 == 0;
                foreach (var arm in OrderedArms(arms, candidateFirst))
                    await RunPairAsync(tasks[cell.TaskId], cell.Mode, arm == "candidate" ? candidate : control, cancellationToken).ConfigureAwait(false);
            }
        }

        return new PairedCorpusBenchmarkRun
        {
            Control = Complete(manifest, control.Results, control.Errored),
            Candidate = Complete(manifest, candidate.Results, candidate.Errored),
        };
    }

    private static CorpusExecution Execution(PairedCorpusBenchmarkRequest request, string suiteVersion, BenchmarkAgentSelection selection, string arm) =>
        new(new CorpusBenchmarkRequest
        {
            Tasks = request.Tasks, TeamId = request.TeamId, Selection = selection, FixtureStager = request.FixtureStager,
            SuiteContentHash = request.SuiteContentHash, ObservationGroupId = request.ObservationGroupId,
            ObservationArm = arm, ObservationSession = request.ObservationSession, RequireDurableObservation = true, CodeRevision = request.CodeRevision,
        }, suiteVersion, new List<BenchmarkResult>(), new List<CorpusBenchmarkError>());

    private static string CellOrder(string seed, string taskId, BenchmarkMode mode, int session) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}\u001f{session}\u001f{taskId}\u001f{mode}")));

    private static IReadOnlyDictionary<(string TaskId, BenchmarkMode Mode), HashSet<string>> ValidateSelectedCells(IReadOnlyList<PairedCorpusBenchmarkCell> selected, EvalSuiteManifest manifest)
    {
        var manifestKeys = manifest.Cells.Select(cell => (cell.TaskId, cell.Mode)).ToHashSet();
        var distinct = selected.Select(cell => (cell.TaskId, cell.Mode, cell.Arm)).ToHashSet();
        if (selected.Count == 0 || distinct.Count != selected.Count) throw new ArgumentException("Selected paired cells must be a non-empty unique keyset.", nameof(selected));
        if (selected.Any(cell => !manifestKeys.Contains((cell.TaskId, cell.Mode)) || cell.Arm is not ("control" or "candidate"))) throw new ArgumentException("Selected paired cells must belong to the frozen manifest and a known arm.", nameof(selected));
        return selected.GroupBy(cell => (cell.TaskId, cell.Mode)).ToDictionary(group => group.Key, group => group.Select(cell => cell.Arm).ToHashSet(StringComparer.Ordinal));
    }

    private static IEnumerable<string> OrderedArms(IReadOnlySet<string> arms, bool candidateFirst)
    {
        var first = candidateFirst ? "candidate" : "control";
        var second = candidateFirst ? "control" : "candidate";
        if (arms.Contains(first)) yield return first;
        if (arms.Contains(second)) yield return second;
    }

    private static void ValidatePairedRequest(PairedCorpusBenchmarkRequest request)
    {
        if (request.TeamId == Guid.Empty) throw new ArgumentException("A paired benchmark team is required.", nameof(request));
        if (request.ObservationGroupId == Guid.Empty) throw new ArgumentException("A non-empty observation group is required.", nameof(request));
        if (request.ObservationSession < 0) throw new ArgumentOutOfRangeException(nameof(request), "ObservationSession cannot be negative.");
        if (string.IsNullOrWhiteSpace(request.OrderingSeed)) throw new ArgumentException("A pre-registered ordering seed is required.", nameof(request));
        if (!BenchmarkEvidenceRevision.IsGitObjectId(request.CodeRevision)) throw new ArgumentException("A full Git object id is required for paired evidence.", nameof(request));
        if (request.Control.ModelCredentialModelId is null || request.Candidate.ModelCredentialModelId is null) throw new ArgumentException("Both paired arms require an exact credential-model row.", nameof(request));
        if (request.Control.ModelCredentialModelId == request.Candidate.ModelCredentialModelId) throw new ArgumentException("Paired arms require distinct credential-model rows.", nameof(request));
        if (request.Control.MaxCostUsd is null || request.Control.MaxCostUsd <= 0 || request.Control.MaxCostUsd != request.Candidate.MaxCostUsd) throw new ArgumentException("Both paired arms require the same positive cost ceiling.", nameof(request));
    }

    private static CorpusBenchmarkRun Complete(EvalSuiteManifest manifest, List<BenchmarkResult> results, List<CorpusBenchmarkError> errored) => new()
    {
        ExecutionPath = ExecutionPathFor(manifest), Results = results, Errored = errored,
        Scorecard = BenchmarkScorecard.Compute(results), SuiteVersion = manifest.Version,
        Cells = EvalSuite.Classify(manifest, results, errored), FormatFaults = BenchmarkScorecard.TallyFormatFaults(results),
    };

    /// <summary>P19: TaskLaunch evidence only when EVERY cell in the manifest actually enters through the real Launch entry — a suite that mixes a direct-harness mode into even one cell cannot substantiate a product Launch-mode seal, so it stays DirectAgentHarness (the conservative default <see cref="QualificationRunner.Grant"/> already gates Sealed on). Internal so the boundary is unit-pinned directly (InternalsVisibleTo), not only through a full corpus run.</summary>
    internal static BenchmarkExecutionPath ExecutionPathFor(EvalSuiteManifest manifest) =>
        manifest.Cells.Count > 0 && manifest.Cells.All(cell => BenchmarkModeEffort.IsTaskLaunch(cell.Mode))
            ? BenchmarkExecutionPath.TaskLaunch
            : BenchmarkExecutionPath.DirectAgentHarness;

    /// <summary>Stage → run → grade → PERSIST ONE (task,mode) pair in an isolated workspace; a non-cancellation throw is recorded as an infra error (the pair is excluded from the score), never aborting the corpus. The workspace is always reclaimed.</summary>
    private sealed record CorpusExecution(CorpusBenchmarkRequest Request, string SuiteVersion, List<BenchmarkResult> Results, List<CorpusBenchmarkError> Errored);
    private async Task RunPairAsync(BenchmarkTask task, BenchmarkMode mode, CorpusExecution execution, CancellationToken cancellationToken)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "cs-corpus-bench-" + Guid.NewGuid().ToString("N"));
        AdmissionCompletionSink? completion = null;

        try
        {
            Directory.CreateDirectory(workspace);
            var request = execution.Request;
            var stager = request.FixtureStager ?? _stager;
            stager.Stage(task.FixtureRef, workspace);

            var admission = await AdmitPairedCellAsync(request, task, mode, cancellationToken).ConfigureAwait(false);
            if (admission?.CompletedResult is { } recovered)
            {
                execution.Results.Add(recovered);
                await PersistAsync(request, execution.SuiteVersion, recovered, cancellationToken).ConfigureAwait(false);
                return;
            }

            completion = admission is null ? null : new AdmissionCompletionSink(_admissions, admission.AdmissionId);
            var context = new BenchmarkExecutionContext { WorkspaceDirectory = workspace, TeamId = request.TeamId, Selection = request.Selection, FixtureStager = stager, Completion = completion, Checkpoints = admission?.Checkpoints ?? new Dictionary<string, BenchmarkExecutionCheckpoint>(), CheckpointSink = completion };
            var result = await _runner.RunAsync(task, mode, context, cancellationToken).ConfigureAwait(false);
            if (completion is not null) await completion.CompleteAsync(result, CancellationToken.None).ConfigureAwait(false);

            execution.Results.Add(result);

            await PersistAsync(request, execution.SuiteVersion, result, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DurableBenchmarkObservationException)
        {
            throw;
        }
        catch (Exception) when (completion?.CompletedResult is not null)
        {
            execution.Results.Add(completion.CompletedResult);
            await PersistAsync(execution.Request, execution.SuiteVersion, completion.CompletedResult, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Benchmark pair {TaskId}/{Mode} could not run (infra fault); recorded as errored + excluded from the score, continuing the corpus", task.Id, mode);
            var error = new CorpusBenchmarkError { TaskId = task.Id, Mode = mode, Error = ex.Message };
            execution.Errored.Add(error);
            await PersistInfraAsync(execution.Request, execution.SuiteVersion, error, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { /* best-effort reclaim */ }
        }
    }

    private async Task<PairedQualificationCellAdmissionOutcome?> AdmitPairedCellAsync(CorpusBenchmarkRequest request, BenchmarkTask task, BenchmarkMode mode, CancellationToken cancellationToken)
    {
        if (!request.RequireDurableObservation) return null;
        if (request.ObservationGroupId is not { } groupId || request.ObservationSession is not { } session || request.ObservationArm is not { } arm || request.Selection?.ModelCredentialModelId is not { } modelRowId)
            throw new DurableBenchmarkObservationException($"Paired benchmark cell {task.Id}/{mode} has no complete durable admission identity.");
        PairedQualificationCellAdmissionOutcome outcome;
        try
        {
            outcome = await _admissions.AdmitAsync(new PairedQualificationCellAdmissionRequest
            {
                ObservationGroupId = groupId, ObservationSession = session, ObservationArm = arm,
                TaskId = task.Id, Mode = mode, ModelCredentialModelId = modelRowId,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not DurableBenchmarkObservationException)
        {
            throw new DurableBenchmarkObservationException($"Durable paired benchmark admission {task.Id}/{mode} could not be committed.", exception);
        }
        if (outcome.Decision == PairedQualificationCellAdmissionDecision.AlreadyAdmitted && outcome.CompletedResult is null && outcome.Checkpoints.Count == 0)
            throw new DurableBenchmarkObservationException($"Paired benchmark cell {task.Id}/{mode} execution-indeterminate: a durable admission exists without the required observation, so automatic paid replay is forbidden.");
        return outcome;
    }

    /// <summary>
    /// A4: append the cell's durable row so a solve rate is comparable across runs, commits, and model bundles
    /// instead of dying with the step summary. DELIBERATELY swallowed on failure and deliberately placed AFTER the
    /// result joins the in-memory results: the gate's verdict is computed from that list, so a
    /// persistence fault can neither red a passing corpus nor turn a failing cell into an infra error.
    /// </summary>
    private async Task PersistAsync(CorpusBenchmarkRequest request, string suiteVersion, BenchmarkResult result, CancellationToken cancellationToken)
    {
        try
        {
            await _results.RecordAsync(new BenchmarkObservationWrite
            {
                TeamId = request.TeamId, SuiteVersion = suiteVersion, Result = result, Selection = request.Selection,
                ObservationGroupId = request.ObservationGroupId, ObservationArm = request.ObservationArm,
                ObservationSession = request.ObservationSession, CodeRevision = request.CodeRevision,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (request.RequireDurableObservation) throw new DurableBenchmarkObservationException($"Durable paired benchmark observation {result.TaskId}/{result.Mode} could not be appended.", ex);
            _logger.LogWarning(ex, "Benchmark cell {TaskId}/{Mode} could not be persisted; the corpus verdict is unaffected (it reads the in-memory results)", result.TaskId, result.Mode);
        }
    }

    private async Task PersistInfraAsync(CorpusBenchmarkRequest request, string suiteVersion, CorpusBenchmarkError error, CancellationToken cancellationToken)
    {
        try
        {
            await _results.RecordInfraAsync(new BenchmarkInfraObservationWrite
            {
                TeamId = request.TeamId, SuiteVersion = suiteVersion, Error = error, Selection = request.Selection,
                ObservationGroupId = request.ObservationGroupId, ObservationArm = request.ObservationArm,
                ObservationSession = request.ObservationSession, CodeRevision = request.CodeRevision,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (request.RequireDurableObservation) throw new DurableBenchmarkObservationException($"Durable paired benchmark infra observation {error.TaskId}/{error.Mode} could not be appended.", ex);
            _logger.LogWarning(ex, "Benchmark infra cell {TaskId}/{Mode} could not be persisted; the corpus verdict is unaffected", error.TaskId, error.Mode);
        }
    }

    private sealed class AdmissionCompletionSink : IBenchmarkCellCompletionSink, IBenchmarkCellCheckpointSink
    {
        private readonly IPairedQualificationCellAdmissionStore _store;
        private readonly Guid _admissionId;
        public BenchmarkResult? CompletedResult { get; private set; }
        public AdmissionCompletionSink(IPairedQualificationCellAdmissionStore store, Guid admissionId) { _store = store; _admissionId = admissionId; }
        public Task PutAsync(string kind, string payloadJson, CancellationToken cancellationToken) => _store.PutCheckpointAsync(_admissionId, kind, payloadJson, cancellationToken);
        public async Task CompleteAsync(BenchmarkResult result, CancellationToken cancellationToken)
        {
            await _store.CompleteAsync(_admissionId, result, cancellationToken).ConfigureAwait(false);
            CompletedResult = result;
        }
    }

}
