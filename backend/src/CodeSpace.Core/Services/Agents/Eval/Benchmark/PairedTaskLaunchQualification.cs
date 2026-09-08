using System.Security.Cryptography;
using System.Text;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Agents.Benchmark;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

public enum PairedQualificationCriterion
{
    Quality = 0,
    Efficiency = 1,
}

public static class BenchmarkEvidenceRevision
{
    public static bool IsGitObjectId(string? value) => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);
}

/// <summary>Pre-registered policy for one paired capability campaign. It measures budget-admissible oracle solves; durable delivery qualification remains a separate release gate.</summary>
public sealed record PairedQualificationSpec
{
    public required int SessionsPerCell { get; init; }
    public required int MinimumIndependentClusters { get; init; }
    public required int MinimumStrata { get; init; }
    public required int MinimumRequiredExecutionClusters { get; init; }
    public required double MinimumEvaluatorHealth { get; init; }
    public required decimal MaxCostUsdPerLaunch { get; init; }
    public required PairedQualificationCriterion Criterion { get; init; }
    public double MinimumQualityLift { get; init; } = 0.05;
    public double NonInferiorityMargin { get; init; } = -0.02;
    public double MinimumCostReduction { get; init; } = 0.20;
    public bool RequireDistinctObservedModels { get; init; } = true;
    public required string OrderingSeed { get; init; }
}

public sealed record PairedQualificationRequest
{
    public required Guid TeamId { get; init; }
    public required BenchmarkAgentSelection Control { get; init; }
    public required BenchmarkAgentSelection Candidate { get; init; }
    public required PairedQualificationSpec Spec { get; init; }
    public required string CodeRevision { get; init; }
}

public sealed record PairedArmQualificationSummary
{
    public required int Solved { get; init; }
    public required int BudgetAdmissibleSolved { get; init; }
    public required int Total { get; init; }
    public required int InfraUnknown { get; init; }
    public required int CostKnownCells { get; init; }
    public required int CapabilityVerdictCells { get; init; }
    public required int ObservedModelCells { get; init; }
    public required double EvaluatorHealth { get; init; }
    public required IReadOnlyList<string> ObservedModels { get; init; }
    public decimal? TotalCostUsd { get; init; }
    public decimal? CostPerBudgetAdmissibleSolveUsd => TotalCostUsd is { } cost && BudgetAdmissibleSolved > 0 ? cost / BudgetAdmissibleSolved : null;
}

public sealed record PairedStratumSummary
{
    public required string Stratum { get; init; }
    public required int Pairs { get; init; }
    public required int ControlSolved { get; init; }
    public required int CandidateSolved { get; init; }
    public required int ControlInfraUnknown { get; init; }
    public required int CandidateInfraUnknown { get; init; }
}

public sealed record PairedInfraObservation
{
    public required int Session { get; init; }
    public required string Arm { get; init; }
    public required string TaskId { get; init; }
    public required BenchmarkMode Mode { get; init; }
    public string? Detail { get; init; }
}

public sealed record PairedQualificationOutcome
{
    public const string StatisticsVersion = "paired-cluster-bootstrap/v1";

    public required Guid ObservationGroupId { get; init; }
    public required string CodeRevision { get; init; }
    public required string SuiteDigest { get; init; }
    public required string SuiteVersion { get; init; }
    public required int IndependentClusters { get; init; }
    public required int PairedCells { get; init; }
    public required int RequiredExecutionClusters { get; init; }
    public required PairedArmQualificationSummary Control { get; init; }
    public required PairedArmQualificationSummary Candidate { get; init; }
    public required double QualityDifference { get; init; }
    public required double QualityDifferenceLower95 { get; init; }
    public double? CostReduction { get; init; }
    public required bool RequiredExecutionComplete { get; init; }
    public required bool QualifiedForCapabilityClaim { get; init; }
    public required IReadOnlyList<string> BlockingReasons { get; init; }
    public required IReadOnlyList<PairedStratumSummary> Strata { get; init; }
    public required IReadOnlyList<PairedInfraObservation> InfraFailures { get; init; }
}

public sealed record PairedQualificationAnalysisRequest
{
    public required Guid ObservationGroupId { get; init; }
    public required string CodeRevision { get; init; }
    public required HiddenSuite Suite { get; init; }
    public required EvalSuiteManifest Manifest { get; init; }
    public required PairedQualificationSpec Spec { get; init; }
    public required BenchmarkAgentSelection Control { get; init; }
    public required BenchmarkAgentSelection Candidate { get; init; }
    public required IReadOnlyList<PairedCorpusBenchmarkRun> Sessions { get; init; }
}

public interface IPairedTaskLaunchQualificationRunner
{
    Task<PairedQualificationOutcome> RunAsync(PairedQualificationRequest request, CancellationToken cancellationToken);
}

/// <summary>Runs two selections against the same sealed suite in balanced cell order and applies a conservative, clustered release fold without model/task lookup tables.</summary>
public sealed class PairedTaskLaunchQualificationRunner : IPairedTaskLaunchQualificationRunner, IScopedDependency
{
    private readonly IHiddenSuiteSource _suite;
    private readonly IPairedCorpusBenchmarkRunner _corpus;
    private readonly CodeSpaceDbContext _db;

    public PairedTaskLaunchQualificationRunner(IHiddenSuiteSource suite, IPairedCorpusBenchmarkRunner corpus, CodeSpaceDbContext db)
    {
        _suite = suite;
        _corpus = corpus;
        _db = db;
    }

    public async Task<PairedQualificationOutcome> RunAsync(PairedQualificationRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        var suite = _suite.Load() ?? throw new InvalidOperationException($"No hidden suite at '{HiddenSuiteLoader.DefaultSuiteDirectory}' — paired qualification cannot run without owner-held bytes.");
        var manifest = EvalSuite.ManifestFor(suite.Tasks, suite.SuiteContentHash);
        if (CorpusBenchmarkRunner.ExecutionPathFor(manifest) != BenchmarkExecutionPath.TaskLaunch)
            throw new InvalidOperationException("Paired capability qualification requires every hidden-suite cell to use the TaskLaunch execution path.");

        var groupId = Guid.NewGuid();
        var control = await CanonicalizeAsync(request.TeamId, request.Control, request.Spec.MaxCostUsdPerLaunch, cancellationToken).ConfigureAwait(false);
        var candidate = await CanonicalizeAsync(request.TeamId, request.Candidate, request.Spec.MaxCostUsdPerLaunch, cancellationToken).ConfigureAwait(false);
        var sessions = new List<PairedCorpusBenchmarkRun>();

        for (var session = 0; session < request.Spec.SessionsPerCell; session++)
        {
            sessions.Add(await _corpus.RunPairedAsync(new PairedCorpusBenchmarkRequest
            {
                Tasks = suite.Tasks, TeamId = request.TeamId, Control = control, Candidate = candidate,
                ObservationGroupId = groupId, ObservationSession = session, OrderingSeed = request.Spec.OrderingSeed,
                CodeRevision = request.CodeRevision, FixtureStager = suite.FixtureStager, SuiteContentHash = suite.SuiteContentHash,
            }, cancellationToken).ConfigureAwait(false));
        }

        return PairedQualificationStatistics.Analyze(new PairedQualificationAnalysisRequest
        {
            ObservationGroupId = groupId, CodeRevision = request.CodeRevision, Suite = suite, Manifest = manifest,
            Spec = request.Spec, Control = control, Candidate = candidate, Sessions = sessions,
        });
    }

    private async Task<BenchmarkAgentSelection> CanonicalizeAsync(Guid teamId, BenchmarkAgentSelection selection, decimal cap, CancellationToken cancellationToken)
    {
        if (selection.ModelCredentialModelId is not { } rowId) return selection with { MaxCostUsd = cap };
        var row = await _db.ModelCredentialModel.AsNoTracking()
            .Where(model => model.Id == rowId && model.Enabled && model.Credential.TeamId == teamId && model.Credential.DeletedDate == null && model.Credential.Status == Messages.Enums.CredentialStatus.Active)
            .Select(model => new { model.ModelId, model.ModelCredentialId }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Credentialed model {rowId} is not active and enabled for qualification team {teamId}.");
        return selection with { Model = row.ModelId, ModelCredentialId = row.ModelCredentialId, MaxCostUsd = cap };
    }

    private static void Validate(PairedQualificationRequest request)
    {
        if (request.TeamId == Guid.Empty) throw new ArgumentException("A qualification team is required.", nameof(request));
        if (request.Spec.SessionsPerCell < 1) throw new ArgumentOutOfRangeException(nameof(request), "SessionsPerCell must be at least one.");
        if (request.Spec.MinimumIndependentClusters < 1) throw new ArgumentOutOfRangeException(nameof(request), "MinimumIndependentClusters must be at least one.");
        if (request.Spec.MinimumStrata < 1) throw new ArgumentOutOfRangeException(nameof(request), "MinimumStrata must be at least one.");
        if (request.Spec.MinimumRequiredExecutionClusters < 0) throw new ArgumentOutOfRangeException(nameof(request), "MinimumRequiredExecutionClusters cannot be negative.");
        if (request.Spec.MinimumEvaluatorHealth is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(request), "MinimumEvaluatorHealth must be between zero and one.");
        if (request.Spec.MaxCostUsdPerLaunch <= 0) throw new ArgumentOutOfRangeException(nameof(request), "MaxCostUsdPerLaunch must be positive.");
        if (request.Spec.MinimumQualityLift is < -1 or > 1) throw new ArgumentOutOfRangeException(nameof(request), "MinimumQualityLift must be between negative one and one.");
        if (request.Spec.NonInferiorityMargin is < -1 or > 1) throw new ArgumentOutOfRangeException(nameof(request), "NonInferiorityMargin must be between negative one and one.");
        if (request.Spec.MinimumCostReduction is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(request), "MinimumCostReduction must be between zero and one.");
        if (request.Control.ModelCredentialModelId is null || request.Candidate.ModelCredentialModelId is null) throw new ArgumentException("Both qualification arms require an exact credential-model row.", nameof(request));
        if (request.Control.ModelCredentialModelId == request.Candidate.ModelCredentialModelId) throw new ArgumentException("Qualification arms require distinct credential-model rows.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Spec.OrderingSeed)) throw new ArgumentException("A pre-registered ordering seed is required.", nameof(request));
        if (!BenchmarkEvidenceRevision.IsGitObjectId(request.CodeRevision)) throw new ArgumentException("A full Git object id is required for qualification evidence.", nameof(request));
    }
}

public static class PairedQualificationStatistics
{
    private const int BootstrapSamples = 20_000;

    public static PairedQualificationOutcome Analyze(PairedQualificationAnalysisRequest request)
    {
        var suite = request.Suite;
        var manifest = request.Manifest;
        var spec = request.Spec;
        var sessions = request.Sessions;
        var tasks = suite.Tasks.ToDictionary(task => task.Id, StringComparer.Ordinal);
        var pairs = sessions.SelectMany((session, index) => Pair(index, session, manifest, tasks, spec.MaxCostUsdPerLaunch)).ToList();
        var control = Summarize(pairs.Select(pair => pair.Control).ToList());
        var candidate = Summarize(pairs.Select(pair => pair.Candidate).ToList());
        var clusters = pairs.Select(pair => pair.Cluster).Distinct(StringComparer.Ordinal).Count();
        var requiredClusters = pairs.Where(pair => pair.Required).Select(pair => pair.Cluster).Distinct(StringComparer.Ordinal).Count();
        var clusterDifferences = pairs.GroupBy(pair => pair.Cluster, StringComparer.Ordinal).Select(cluster => cluster.Average(pair => pair.Difference)).ToList();
        var difference = clusterDifferences.Count == 0 ? 0 : clusterDifferences.Average();
        var lower = ClusterBootstrapLower(pairs, spec.OrderingSeed);
        var requiredComplete = pairs.Where(pair => pair.Required).All(pair => pair.Control.State != CorpusCellState.InfraUnknown && pair.Candidate.State != CorpusCellState.InfraUnknown);
        var costReduction = CostReduction(control, candidate);
        var stratumCount = pairs.Select(pair => pair.Stratum).Distinct(StringComparer.Ordinal).Count();
        var protocolEvidenceMatches = ProtocolEvidenceMatches(sessions, manifest, spec.SessionsPerCell);
        var blockers = Blockers(new QualificationEvidence
        {
            ObservationGroupId = request.ObservationGroupId, CodeRevision = request.CodeRevision,
            Spec = spec, ControlSelection = request.Control, CandidateSelection = request.Candidate, Control = control, Candidate = candidate,
            Clusters = clusters, Strata = stratumCount, RequiredClusters = requiredClusters, RequiredComplete = requiredComplete,
            ProtocolEvidenceMatches = protocolEvidenceMatches, Difference = difference, Lower = lower, CostReduction = costReduction,
        });

        return new PairedQualificationOutcome
        {
            ObservationGroupId = request.ObservationGroupId, CodeRevision = request.CodeRevision, SuiteDigest = suite.SuiteContentHash, SuiteVersion = manifest.Version,
            IndependentClusters = clusters, PairedCells = pairs.Count, RequiredExecutionClusters = requiredClusters, Control = control, Candidate = candidate,
            QualityDifference = difference, QualityDifferenceLower95 = lower, CostReduction = costReduction,
            RequiredExecutionComplete = requiredComplete, QualifiedForCapabilityClaim = blockers.Count == 0, BlockingReasons = blockers,
            Strata = pairs.GroupBy(pair => pair.Stratum, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => new PairedStratumSummary
            {
                Stratum = group.Key, Pairs = group.Count(), ControlSolved = group.Count(pair => pair.Control.BudgetAdmissibleSolved),
                CandidateSolved = group.Count(pair => pair.Candidate.BudgetAdmissibleSolved),
                ControlInfraUnknown = group.Count(pair => pair.Control.State == CorpusCellState.InfraUnknown),
                CandidateInfraUnknown = group.Count(pair => pair.Candidate.State == CorpusCellState.InfraUnknown),
            }).ToList(),
            InfraFailures = pairs.SelectMany(pair => Infra(pair)).ToList(),
        };
    }

    private static IReadOnlyList<CellPair> Pair(int sessionIndex, PairedCorpusBenchmarkRun session, EvalSuiteManifest manifest, IReadOnlyDictionary<string, BenchmarkTask> tasks, decimal cap)
    {
        var controlCells = (session.Control.Cells ?? Array.Empty<CorpusCellOutcome>()).ToDictionary(cell => (cell.TaskId, cell.Mode));
        var candidateCells = (session.Candidate.Cells ?? Array.Empty<CorpusCellOutcome>()).ToDictionary(cell => (cell.TaskId, cell.Mode));
        var controlResults = session.Control.Results.ToDictionary(result => (result.TaskId, result.Mode));
        var candidateResults = session.Candidate.Results.ToDictionary(result => (result.TaskId, result.Mode));

        return manifest.Cells.Select(cell =>
        {
            var task = tasks[cell.TaskId];
            var control = Observation(controlCells.GetValueOrDefault((cell.TaskId, cell.Mode)), controlResults.GetValueOrDefault((cell.TaskId, cell.Mode)), cap);
            var candidate = Observation(candidateCells.GetValueOrDefault((cell.TaskId, cell.Mode)), candidateResults.GetValueOrDefault((cell.TaskId, cell.Mode)), cap);
            return new CellPair
            {
                Session = sessionIndex, TaskId = cell.TaskId, Mode = cell.Mode, Stratum = task.Stratum,
                Cluster = string.IsNullOrWhiteSpace(task.IndependenceCluster) ? task.Id : task.IndependenceCluster!,
                Required = task.RequiresCompleteExecution, Control = control, Candidate = candidate,
            };
        }).ToList();
    }

    private static CellObservation Observation(CorpusCellOutcome? cell, BenchmarkResult? result, decimal cap)
    {
        var state = cell?.State ?? CorpusCellState.InfraUnknown;
        var admissible = state == CorpusCellState.Solved && result is { CostIndeterminate: false, CostUsd: { } cost } && cost <= cap;
        return new CellObservation
        {
            State = state, Detail = cell?.Detail, BudgetAdmissibleSolved = admissible, CostUsd = result?.CostUsd,
            CostIndeterminate = result?.CostIndeterminate ?? state == CorpusCellState.InfraUnknown, ObservedModel = result?.ObservedModel,
        };
    }

    private static PairedArmQualificationSummary Summarize(IReadOnlyList<CellObservation> cells)
    {
        var allCostsKnown = cells.Count > 0 && cells.All(cell => cell.CostUsd is not null && !cell.CostIndeterminate);
        return new PairedArmQualificationSummary
        {
            Solved = cells.Count(cell => cell.State == CorpusCellState.Solved), BudgetAdmissibleSolved = cells.Count(cell => cell.BudgetAdmissibleSolved),
            Total = cells.Count, InfraUnknown = cells.Count(cell => cell.State == CorpusCellState.InfraUnknown),
            CostKnownCells = cells.Count(cell => cell.CostUsd is not null && !cell.CostIndeterminate),
            CapabilityVerdictCells = cells.Count(cell => cell.State != CorpusCellState.InfraUnknown),
            ObservedModelCells = cells.Count(cell => cell.State != CorpusCellState.InfraUnknown && !string.IsNullOrWhiteSpace(cell.ObservedModel)),
            EvaluatorHealth = cells.Count == 0 ? 0 : (double)cells.Count(cell => cell.State != CorpusCellState.InfraUnknown) / cells.Count,
            ObservedModels = cells.Select(cell => cell.ObservedModel?.Trim()).Where(model => !string.IsNullOrWhiteSpace(model)).Distinct(StringComparer.OrdinalIgnoreCase).Cast<string>().OrderBy(model => model, StringComparer.OrdinalIgnoreCase).ToList(),
            TotalCostUsd = allCostsKnown ? cells.Sum(cell => cell.CostUsd!.Value) : null,
        };
    }

    private static bool ProtocolEvidenceMatches(IReadOnlyList<PairedCorpusBenchmarkRun> sessions, EvalSuiteManifest manifest, int expectedSessions)
    {
        var expectedCells = manifest.Cells.Select(cell => (cell.TaskId, cell.Mode)).ToHashSet();
        return sessions.Count == expectedSessions && sessions.SelectMany(session => new[] { session.Control, session.Candidate }).All(run =>
            run.ExecutionPath == BenchmarkExecutionPath.TaskLaunch
            && string.Equals(run.SuiteVersion, manifest.Version, StringComparison.Ordinal)
            && (run.Cells ?? Array.Empty<CorpusCellOutcome>()).Select(cell => (cell.TaskId, cell.Mode)).ToHashSet().SetEquals(expectedCells)
            && run.Results.All(result => expectedCells.Contains((result.TaskId, result.Mode))));
    }

    private static List<string> Blockers(QualificationEvidence evidence)
    {
        var spec = evidence.Spec;
        var controlSelection = evidence.ControlSelection;
        var candidateSelection = evidence.CandidateSelection;
        var control = evidence.Control;
        var candidate = evidence.Candidate;
        var blockers = new List<string>();
        if (evidence.ObservationGroupId == Guid.Empty) blockers.Add("observation-group-unbound");
        if (!BenchmarkEvidenceRevision.IsGitObjectId(evidence.CodeRevision)) blockers.Add("code-revision-invalid");
        if (controlSelection.ModelCredentialModelId is null || candidateSelection.ModelCredentialModelId is null) blockers.Add("unbound-model-row");
        if (controlSelection.ModelCredentialModelId == candidateSelection.ModelCredentialModelId) blockers.Add("identical-model-row");
        if (control.ObservedModels.Count != 1 || candidate.ObservedModels.Count != 1 || control.ObservedModelCells != control.CapabilityVerdictCells || candidate.ObservedModelCells != candidate.CapabilityVerdictCells) blockers.Add("incomplete-or-inconsistent-observed-model");
        if (spec.RequireDistinctObservedModels && control.ObservedModels.Count == 1 && candidate.ObservedModels.Count == 1 && string.Equals(control.ObservedModels[0], candidate.ObservedModels[0], StringComparison.OrdinalIgnoreCase)) blockers.Add("identical-observed-model");
        if (evidence.Clusters < spec.MinimumIndependentClusters) blockers.Add("insufficient-independent-clusters");
        if (evidence.Strata < spec.MinimumStrata) blockers.Add("insufficient-strata");
        if (evidence.RequiredClusters < spec.MinimumRequiredExecutionClusters) blockers.Add("required-execution-strata-absent");
        if (!evidence.ProtocolEvidenceMatches) blockers.Add("paired-protocol-evidence-mismatch");
        if (control.EvaluatorHealth < spec.MinimumEvaluatorHealth || candidate.EvaluatorHealth < spec.MinimumEvaluatorHealth) blockers.Add("evaluator-health-below-floor");
        if (!evidence.RequiredComplete) blockers.Add("required-execution-incomplete");

        if (spec.Criterion == PairedQualificationCriterion.Quality)
        {
            if (evidence.Difference < spec.MinimumQualityLift) blockers.Add("quality-lift-below-floor");
            if (evidence.Lower <= 0) blockers.Add("paired-confidence-lower-bound-not-positive");
        }
        else
        {
            if (evidence.Lower <= spec.NonInferiorityMargin) blockers.Add("quality-noninferiority-not-established");
            if (evidence.CostReduction is null) blockers.Add("cost-indeterminate");
            else if (evidence.CostReduction < spec.MinimumCostReduction) blockers.Add("cost-reduction-below-floor");
        }

        return blockers;
    }

    private static double? CostReduction(PairedArmQualificationSummary control, PairedArmQualificationSummary candidate)
    {
        if (control.CostPerBudgetAdmissibleSolveUsd is not { } controlCost || candidate.CostPerBudgetAdmissibleSolveUsd is not { } candidateCost || controlCost <= 0) return null;
        return (double)((controlCost - candidateCost) / controlCost);
    }

    private static double ClusterBootstrapLower(IReadOnlyList<CellPair> pairs, string seed)
    {
        var clusters = pairs.GroupBy(pair => pair.Cluster, StringComparer.Ordinal).Select(group => group.Average(pair => pair.Difference)).ToList();
        if (clusters.Count == 0) return -1;
        if (clusters.All(cluster => cluster == clusters[0])) return clusters[0];

        var random = new StableRandom(seed);
        var samples = new double[BootstrapSamples];
        for (var sample = 0; sample < samples.Length; sample++)
        {
            var sum = 0d;
            var count = 0;
            for (var draw = 0; draw < clusters.Count; draw++)
            {
                sum += clusters[random.Next(clusters.Count)];
                count++;
            }
            samples[sample] = count == 0 ? -1 : (double)sum / count;
        }
        Array.Sort(samples);
        return samples[(int)Math.Floor(0.05 * (samples.Length - 1))];
    }

    private static IEnumerable<PairedInfraObservation> Infra(CellPair pair)
    {
        if (pair.Control.State == CorpusCellState.InfraUnknown) yield return new PairedInfraObservation { Session = pair.Session, Arm = "control", TaskId = pair.TaskId, Mode = pair.Mode, Detail = pair.Control.Detail };
        if (pair.Candidate.State == CorpusCellState.InfraUnknown) yield return new PairedInfraObservation { Session = pair.Session, Arm = "candidate", TaskId = pair.TaskId, Mode = pair.Mode, Detail = pair.Candidate.Detail };
    }

    private sealed record CellObservation
    {
        public required CorpusCellState State { get; init; }
        public string? Detail { get; init; }
        public required bool BudgetAdmissibleSolved { get; init; }
        public decimal? CostUsd { get; init; }
        public required bool CostIndeterminate { get; init; }
        public string? ObservedModel { get; init; }
    }
    private sealed record CellPair
    {
        public required int Session { get; init; }
        public required string TaskId { get; init; }
        public required BenchmarkMode Mode { get; init; }
        public required string Stratum { get; init; }
        public required string Cluster { get; init; }
        public required bool Required { get; init; }
        public required CellObservation Control { get; init; }
        public required CellObservation Candidate { get; init; }
        public int Difference => (Candidate.BudgetAdmissibleSolved ? 1 : 0) - (Control.BudgetAdmissibleSolved ? 1 : 0);
    }
    private sealed record QualificationEvidence
    {
        public required Guid ObservationGroupId { get; init; }
        public required string CodeRevision { get; init; }
        public required PairedQualificationSpec Spec { get; init; }
        public required BenchmarkAgentSelection ControlSelection { get; init; }
        public required BenchmarkAgentSelection CandidateSelection { get; init; }
        public required PairedArmQualificationSummary Control { get; init; }
        public required PairedArmQualificationSummary Candidate { get; init; }
        public required int Clusters { get; init; }
        public required int Strata { get; init; }
        public required int RequiredClusters { get; init; }
        public required bool RequiredComplete { get; init; }
        public required bool ProtocolEvidenceMatches { get; init; }
        public required double Difference { get; init; }
        public required double Lower { get; init; }
        public double? CostReduction { get; init; }
    }

    private sealed class StableRandom
    {
        private ulong _state;
        public StableRandom(string seed)
        {
            _state = BitConverter.ToUInt64(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));
            if (_state == 0) _state = 0x9E3779B97F4A7C15UL;
        }

        public int Next(int exclusiveMax)
        {
            _state ^= _state << 13;
            _state ^= _state >> 7;
            _state ^= _state << 17;
            return (int)(_state % (uint)exclusiveMax);
        }
    }
}
