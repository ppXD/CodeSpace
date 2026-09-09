using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Exceptions;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

public interface IPairedQualificationRecoveryService
{
    Task<PairedQualificationOutcome> RecoverAsync(Guid observationGroupId, CancellationToken cancellationToken);
}

/// <summary>Recovers a complete, unsealed paired campaign entirely from immutable protocol and observation rows; it never dispatches paid work.</summary>
public sealed class PairedQualificationRecoveryService : IPairedQualificationRecoveryService, IScopedDependency
{
    private readonly IHiddenSuiteSource _suite;
    private readonly CodeSpaceDbContext _db;
    private readonly IPairedQualificationResultStore _results;

    public PairedQualificationRecoveryService(IHiddenSuiteSource suite, CodeSpaceDbContext db, IPairedQualificationResultStore results)
    {
        _suite = suite;
        _db = db;
        _results = results;
    }

    public async Task<PairedQualificationOutcome> RecoverAsync(Guid observationGroupId, CancellationToken cancellationToken)
    {
        if (observationGroupId == Guid.Empty) throw Invalid("observation-group-unbound");
        var protocol = await _db.PairedQualificationProtocol.AsNoTracking().SingleOrDefaultAsync(row => row.ObservationGroupId == observationGroupId, cancellationToken).ConfigureAwait(false)
            ?? throw Invalid("protocol-not-found");
        if (await _db.PairedQualificationResult.AsNoTracking().AnyAsync(row => row.ObservationGroupId == observationGroupId, cancellationToken).ConfigureAwait(false)) throw Invalid("result-already-sealed");
        if (protocol.StatisticsVersion != PairedQualificationOutcome.StatisticsVersion) throw Invalid("statistics-version-unsupported");

        var suite = _suite.Load() ?? throw Invalid("sealed-suite-unavailable");
        var manifest = EvalSuite.ManifestFor(suite.Tasks, suite.SuiteContentHash);
        if (suite.SuiteContentHash != protocol.SuiteDigest || manifest.Version != protocol.SuiteVersion || CorpusBenchmarkRunner.ExecutionPathFor(manifest) != BenchmarkExecutionPath.TaskLaunch) throw Invalid("sealed-suite-mismatch");
        var observations = await _db.BenchmarkResultRecord.AsNoTracking().Where(row => row.ObservationGroupId == observationGroupId).ToListAsync(cancellationToken).ConfigureAwait(false);
        ValidateCensus(protocol, manifest, observations);

        var control = Selection(protocol.ControlModelRowId, protocol.MaxCostUsdPerLaunch);
        var candidate = Selection(protocol.CandidateModelRowId, protocol.MaxCostUsdPerLaunch);
        var sessions = Enumerable.Range(0, protocol.SessionsPerCell).Select(session => new PairedCorpusBenchmarkRun
        {
            Control = Rehydrate(manifest, observations.Where(row => row.ObservationSession == session && row.ObservationArm == "control")),
            Candidate = Rehydrate(manifest, observations.Where(row => row.ObservationSession == session && row.ObservationArm == "candidate")),
        }).ToList();
        var outcome = PairedQualificationStatistics.Analyze(new PairedQualificationAnalysisRequest
        {
            ObservationGroupId = observationGroupId, CodeRevision = protocol.CodeRevision, Suite = suite, Manifest = manifest,
            Spec = Spec(protocol), Control = control, Candidate = candidate, Sessions = sessions,
        }) with { ProtocolDigest = protocol.ProtocolDigest };
        return await _results.SealAsync(new PairedQualificationSealRequest { ObservationGroupId = observationGroupId, Manifest = manifest, Outcome = outcome }, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateCensus(PairedQualificationProtocol protocol, EvalSuiteManifest manifest, IReadOnlyList<BenchmarkResultRecord> observations)
    {
        var expected = (from session in Enumerable.Range(0, protocol.SessionsPerCell)
                        from arm in new[] { "control", "candidate" }
                        from cell in manifest.Cells
                        select (session, arm, cell.TaskId, Mode: cell.Mode.ToString())).ToHashSet();
        var actual = observations.Select(row => (row.ObservationSession ?? -1, row.ObservationArm ?? string.Empty, row.TaskId, row.Mode)).ToHashSet();
        if (observations.Count != expected.Count || actual.Count != observations.Count || !actual.SetEquals(expected)) throw Invalid("observation-census-mismatch");
    }

    private static CorpusBenchmarkRun Rehydrate(EvalSuiteManifest manifest, IEnumerable<BenchmarkResultRecord> source)
    {
        var rows = source.ToDictionary(row => (row.TaskId, row.Mode));
        var cells = manifest.Cells.Select(cell => Cell(rows[(cell.TaskId, cell.Mode.ToString())], cell.Mode)).ToList();
        var results = manifest.Cells.Select(cell => rows[(cell.TaskId, cell.Mode.ToString())]).Where(row => ParseState(row.OutcomeState) != CorpusCellState.InfraUnknown).Select(Result).ToList();
        var errors = manifest.Cells.Select(cell => rows[(cell.TaskId, cell.Mode.ToString())]).Where(row => ParseState(row.OutcomeState) == CorpusCellState.InfraUnknown)
            .Select(row => new CorpusBenchmarkError { TaskId = row.TaskId, Mode = ParseMode(row.Mode), Error = row.OutcomeDetail ?? "infra-unknown" }).ToList();
        return new CorpusBenchmarkRun
        {
            ExecutionPath = BenchmarkExecutionPath.TaskLaunch, Results = results, Errored = errors,
            Scorecard = BenchmarkScorecard.Compute(results), SuiteVersion = manifest.Version, Cells = cells,
        };
    }

    private static CorpusCellOutcome Cell(BenchmarkResultRecord row, BenchmarkMode mode) => new() { TaskId = row.TaskId, Mode = mode, State = ParseState(row.OutcomeState), Detail = row.OutcomeDetail };

    private static BenchmarkResult Result(BenchmarkResultRecord row) => new()
    {
        TaskId = row.TaskId, Mode = ParseMode(row.Mode), AgentRunId = row.AgentRunId, RunStatus = ParseRunStatus(row.RunStatus), DurationSeconds = row.DurationSeconds,
        Grade = new BenchmarkGrade { Passed = row.Solved, Detail = row.OutcomeDetail ?? row.OutcomeState }, CostUsd = row.CostUsd, CostIndeterminate = row.CostIndeterminate,
        ReviseRounds = row.ReviseRounds, McpFullCatalog = row.McpFullCatalog, ExitReason = row.ExitReason, ObservedModel = row.ObservedModel,
    };

    private static PairedQualificationSpec Spec(PairedQualificationProtocol protocol) => new()
    {
        SessionsPerCell = protocol.SessionsPerCell, MinimumIndependentClusters = protocol.MinimumIndependentClusters, MinimumStrata = protocol.MinimumStrata,
        MinimumRequiredExecutionClusters = protocol.MinimumRequiredExecutionClusters, MinimumEvaluatorHealth = protocol.MinimumEvaluatorHealth,
        MaxCostUsdPerLaunch = protocol.MaxCostUsdPerLaunch, Criterion = Enum.TryParse<PairedQualificationCriterion>(protocol.Criterion, out var criterion) ? criterion : throw Invalid("criterion-invalid"),
        MinimumQualityLift = protocol.MinimumQualityLift, NonInferiorityMargin = protocol.NonInferiorityMargin,
        MinimumCostReduction = protocol.MinimumCostReduction, RequireDistinctObservedModels = protocol.RequireDistinctObservedModels, OrderingSeed = protocol.OrderingSeed,
    };

    private static BenchmarkAgentSelection Selection(Guid modelRowId, decimal cap) => new() { ModelCredentialModelId = modelRowId, MaxCostUsd = cap };
    private static BenchmarkMode ParseMode(string value) => Enum.TryParse<BenchmarkMode>(value, out var parsed) ? parsed : throw Invalid("observation-mode-invalid");
    private static CorpusCellState ParseState(string value) => Enum.TryParse<CorpusCellState>(value, out var parsed) ? parsed : throw Invalid("observation-state-invalid");
    private static AgentRunStatus ParseRunStatus(string value) => Enum.TryParse<AgentRunStatus>(value, out var parsed) ? parsed : throw Invalid("observation-run-status-invalid");
    private static DurableQualificationResultException Invalid(string reason) => new($"Paired qualification recovery refused: {reason}.");
}
