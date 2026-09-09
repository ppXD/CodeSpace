using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Stagers;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// 🟢 High fidelity, REPORT-ONLY (dispatch-only lane): the qualification rehearsal drives the deployed
/// <see cref="IQualificationRunner"/> receipt chain and paired TaskLaunch chain under live gateway credentials.
/// The shipped development corpus always exercises the physical model/CLI path; an operator-staged hidden suite
/// adds holdout evidence when present. It reports bounds, health, digest, exact revision and redacted observed-model
/// identities. Every receipt and paired cell lands only in the job-local database; the rehearsal never mints a
/// deployment claim. Missing live credentials self-skip loudly (skip ≠ pass).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
[Trait("Surface", "Engine")]
public sealed class RealModelQualificationRehearsalE2ETests
{
    private const string Provider = "Anthropic";
    private const string BaselineModelEnvVar = "CODESPACE_LLM_BASELINE_MODEL_ID";
    private const string EvidenceDirectoryEnvVar = "CODESPACE_QUALIFICATION_EVIDENCE_DIRECTORY";

    /// <summary>The rehearsal's report bar — deliberately the corpus lane's conservative floor, NOT the operator's claim bar: the printout reports the bound and the operator compares it to the bar they intend to mint with.</summary>
    private const double RehearsalBar = 0.5;

    private readonly PostgresFixture _fixture;

    public RealModelQualificationRehearsalE2ETests(PostgresFixture fixture) { _fixture = fixture; }

    [SkippableFact]
    public async Task The_hidden_suite_rehearsal_reports_the_would_be_qualification()
    {
        var baseUrl = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => v is not null);
        if (present == 0) throw RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)");   // skip ≠ pass
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three or none; a partial config would self-skip green proving nothing.");

        if (HiddenSuiteLoader.LoadFromDefaultLocation() is not { } suite)
        {
            throw RealModelGate.ReportSkipped(Provider, $"no hidden suite at '{HiddenSuiteLoader.DefaultSuiteDirectory}' — stage it (CODESPACE_HIDDEN_SUITE_URL secret) to rehearse; skip ≠ pass");
        }

        if (OperatingSystem.IsWindows()) return;
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture, inProcessPool: false);
        var (credId, modelRowId) = await SeedAgentCredentialAsync(teamId, baseUrl!.TrimEnd('/'), apiKey!, model!);

        await RealModelGate.AssessLiveWholeLoopAsync(Provider, async () =>
        {
            var selection = new BenchmarkAgentSelection { Harness = "claude-code", Model = model, ModelCredentialId = credId, ModelCredentialModelId = modelRowId, Autonomy = AgentAutonomyLevel.Trusted };
            var spec = new QualificationSpec { MinSolveRateLowerBound = RehearsalBar, MinEvaluatorHealth = 0.9, ValidityDays = 1 };

            QualificationOutcome outcome;
            using (var scope = _fixture.BeginScope())
                outcome = await scope.Resolve<IQualificationRunner>().QualifyAsync("supervisor", "git-branch", spec, teamId, selection, CancellationToken.None);

            outcome.ModelEvidence.ShouldNotBeNull().ModelCredentialModelId.ShouldBe(modelRowId, "the live round must name the exact credentialed-model row it dispatched");

            // The gateway-health disclosure the corpus lanes carry, on the round a real claim would be minted from:
            // the mitigation disables extended thinking, so a bound leaning on respawned solves was measured under a
            // different configuration than the receipt names — read it before minting against this bar.
            var report = $"suite {outcome.SuiteDigest} ({suite.Tasks.Count} task(s)), execution path {outcome.ExecutionPath}: solved {outcome.Score.Solved}/{outcome.Score.Total}, "
                       + $"one-sided 95% lower bound {outcome.SolveRateLowerBound:P1}, evaluator health {outcome.Score.EvaluatorHealth:P1}, model attribution {outcome.ModelEvidence.Attribution} "
                       + $"({outcome.ModelEvidence.ObservedCellCount}/{outcome.ModelEvidence.SampleSize} observed), {outcome.FormatFaults} "
                       + $"→ at the {RehearsalBar:P0} rehearsal bar this round would grant {outcome.Granted}. "
                       + "Compare the BOUND to the bar you intend to mint with — the rehearsal receipt lives in the job-local db and dies with it.";
            Console.WriteLine($"[qualification-rehearsal] {report}");

            return (RealModelOutcome.Drove, report);   // report-only: the rehearsal's signal is the printout, never a capability gate
        });
    }

    [SkippableFact]
    public async Task The_hidden_suite_paired_rehearsal_drives_balanced_budgeted_TaskLaunch_cells()
    {
        var connection = LiveConnection();
        var suite = HiddenSuiteLoader.LoadFromDefaultLocation() ?? throw RealModelGate.ReportSkipped(Provider, $"no hidden suite at '{HiddenSuiteLoader.DefaultSuiteDirectory}' — skip ≠ pass");
        await RunLivePairedRehearsalAsync(new LivePairedRehearsalRequest
        {
            Suite = suite, SuiteKind = "hidden-holdout", Connection = connection,
            MinimumIndependentClusters = 40, MinimumStrata = 8, MinimumRequiredExecutionClusters = 1, MinimumEvaluatorHealth = 0.98,
        });
    }

    [SkippableFact]
    public async Task The_shipped_development_suite_drives_a_real_paired_TaskLaunch_model_and_CLI_path()
    {
        var tasks = SeedBenchmarkCorpus.Tasks.Select(task => task with
        {
            Modes = Array.AsReadOnly(new[] { BenchmarkMode.TaskLaunchQuick }),
            Stratum = "development",
            IndependenceCluster = task.Id,
            RequiresCompleteExecution = true,
        }).ToList().AsReadOnly();
        tasks.ShouldNotBeEmpty();
        tasks.ShouldAllBe(task => task.Modes.SequenceEqual(new[] { BenchmarkMode.TaskLaunchQuick }) && task.IndependenceCluster == task.Id && task.RequiresCompleteExecution);
        var corpusDigest = EvalSuite.ManifestFor(tasks).Version;
        var suite = new HiddenSuite(tasks, $"development:{corpusDigest}", new SeedFixtureStager());
        await RunLivePairedRehearsalAsync(new LivePairedRehearsalRequest
        {
            Suite = suite, SuiteKind = "development-protocol", Connection = LiveConnection(),
            MinimumIndependentClusters = tasks.Count, MinimumStrata = 1, MinimumRequiredExecutionClusters = tasks.Count, MinimumEvaluatorHealth = 0.9,
        });
    }

    private async Task RunLivePairedRehearsalAsync(LivePairedRehearsalRequest input)
    {
        if (OperatingSystem.IsWindows()) return;
        var (baseUrl, apiKey, candidateModel) = input.Connection;
        var configuredBaseline = Environment.GetEnvironmentVariable(BaselineModelEnvVar);
        var usedFallbackBaseline = string.IsNullOrWhiteSpace(configuredBaseline);
        var baselineModel = usedFallbackBaseline ? candidateModel : configuredBaseline;
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture, inProcessPool: false);
        var (controlCredentialId, controlRowId) = await SeedAgentCredentialAsync(teamId, baseUrl.TrimEnd('/'), apiKey, baselineModel!);
        var (candidateCredentialId, candidateRowId) = await SeedAgentCredentialAsync(teamId, baseUrl.TrimEnd('/'), apiKey, candidateModel);

        await RealModelGate.AssessLiveWholeLoopAsync(Provider, async () =>
        {
            var selection = new BenchmarkAgentSelection { Harness = "claude-code", Autonomy = AgentAutonomyLevel.Trusted };
            var request = new PairedQualificationRequest
            {
                TeamId = teamId,
                Control = selection with { Model = baselineModel, ModelCredentialId = controlCredentialId, ModelCredentialModelId = controlRowId },
                Candidate = selection with { Model = candidateModel, ModelCredentialId = candidateCredentialId, ModelCredentialModelId = candidateRowId },
                CodeRevision = CurrentRevision(),
                Spec = new PairedQualificationSpec
                {
                    SessionsPerCell = 1, MinimumIndependentClusters = input.MinimumIndependentClusters, MinimumStrata = input.MinimumStrata,
                    MinimumRequiredExecutionClusters = input.MinimumRequiredExecutionClusters, MinimumEvaluatorHealth = input.MinimumEvaluatorHealth,
                    MaxCostUsdPerLaunch = 5m, Criterion = PairedQualificationCriterion.Quality,
                    MinimumQualityLift = 0.05, OrderingSeed = $"paired-tasklaunch-{input.SuiteKind}-v1",
                },
            };

            PairedQualificationOutcome outcome;
            List<BenchmarkResultRecord> durable;
            using (var scope = _fixture.BeginScope())
            {
                var runner = new PairedTaskLaunchQualificationRunner(new FixedHiddenSuiteSource(input.Suite), scope.Resolve<IPairedCorpusBenchmarkRunner>(), scope.Resolve<CodeSpaceDbContext>());
                outcome = await runner.RunAsync(request, CancellationToken.None);
            }
            using (var scope = _fixture.BeginScope())
            {
                durable = await scope.Resolve<CodeSpaceDbContext>().BenchmarkResultRecord.AsNoTracking().Where(row => row.ObservationGroupId == outcome.ObservationGroupId).ToListAsync();
                durable.Count.ShouldBe(outcome.PairedCells * 2, "every control/candidate cell must be durably appendable before the report can exist");
                durable.ShouldAllBe(row => row.GitSha == outcome.CodeRevision && row.ObservationSession == 0);
                durable.Count(row => row.ObservationArm == "control" && row.ModelCredentialModelId == controlRowId).ShouldBe(outcome.PairedCells);
                durable.Count(row => row.ObservationArm == "candidate" && row.ModelCredentialModelId == candidateRowId).ShouldBe(outcome.PairedCells);
            }

            outcome.SuiteDigest.ShouldBe(input.Suite.SuiteContentHash);
            outcome.ProtocolDigest.ShouldNotBeNull().Length.ShouldBe(64, "the paid paired run must expose its durable pre-outcome protocol identity");
            outcome.PairedCells.ShouldBe(input.Suite.Tasks.Sum(task => task.Modes.Count));
            if (usedFallbackBaseline)
            {
                outcome.QualifiedForCapabilityClaim.ShouldBeFalse("one configured model can exercise the paired plumbing but cannot become its own independent baseline");
                outcome.BlockingReasons.ShouldContain("identical-observed-model");
            }

            var cellOrdinals = input.Suite.Tasks.SelectMany(task => task.Modes.Select(mode => (task.Id, Mode: mode.ToString()))).Select((cell, index) => (cell, index)).ToDictionary(item => item.cell, item => item.index);
            var evidence = new
            {
                schema = PairedQualificationOutcome.StatisticsVersion,
                suiteKind = input.SuiteKind,
                outcome.ObservationGroupId,
                outcome.ProtocolDigest,
                outcome.CodeRevision,
                outcome.SuiteDigest,
                outcome.SuiteVersion,
                outcome.IndependentClusters,
                outcome.PairedCells,
                outcome.RequiredExecutionClusters,
                outcome.QualityDifference,
                outcome.QualityDifferenceLower95,
                outcome.RequiredExecutionComplete,
                outcome.QualifiedForCapabilityClaim,
                outcome.BlockingReasons,
                control = Redacted(outcome.Control),
                candidate = Redacted(outcome.Candidate),
                outcome.Strata,
                outcome.InfraFailures,
                baselineConfiguration = usedFallbackBaseline ? "same-model-plumbing-rehearsal" : "distinct-configured-models",
                identicalSingleObservedModel = outcome.Control.ObservedModels.Count == 1 && outcome.Candidate.ObservedModels.Count == 1
                    && string.Equals(outcome.Control.ObservedModels[0], outcome.Candidate.ObservedModels[0], StringComparison.OrdinalIgnoreCase),
                cells = durable.OrderBy(row => cellOrdinals[(row.TaskId, row.Mode)]).ThenBy(row => row.ObservationArm, StringComparer.Ordinal).Select(row => new
                {
                    cellOrdinal = cellOrdinals[(row.TaskId, row.Mode)], row.Mode, arm = row.ObservationArm, session = row.ObservationSession,
                    state = row.OutcomeState, detail = row.OutcomeDetail, row.Solved, runStatus = row.RunStatus,
                    row.CostUsd, row.CostIndeterminate, row.MaxCostUsd, row.ReviseRounds, row.ExitReason, row.DurationSeconds,
                }),
            };
            var evidenceDirectory = QualificationEvidenceDirectory();
            Directory.CreateDirectory(evidenceDirectory);
            await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, $"paired-tasklaunch-{SafeEvidenceFilePart(input.SuiteKind)}.json"), JsonSerializer.Serialize(evidence, AgentJson.Options));
            var report = $"{input.SuiteKind} paired suite {outcome.SuiteDigest} at {outcome.CodeRevision}: {outcome.PairedCells} cell pairs / {outcome.IndependentClusters} independent clusters, "
                       + $"solves control {outcome.Control.Solved}/{outcome.Control.Total}, candidate {outcome.Candidate.Solved}/{outcome.Candidate.Total}; "
                       + $"budget-admissible control {outcome.Control.BudgetAdmissibleSolved}/{outcome.Control.Total}, candidate {outcome.Candidate.BudgetAdmissibleSolved}/{outcome.Candidate.Total}; "
                       + $"cost-known control {outcome.Control.CostKnownCells}/{outcome.Control.Total}, candidate {outcome.Candidate.CostKnownCells}/{outcome.Candidate.Total}; "
                       + $"paired difference {outcome.QualityDifference:P1}, cluster-bootstrap lower 95% {outcome.QualityDifferenceLower95:P1}, qualified {outcome.QualifiedForCapabilityClaim}; blockers [{string.Join(',', outcome.BlockingReasons)}].";
            Console.WriteLine($"[paired-qualification-rehearsal] {report}");
            return (RealModelOutcome.Drove, report);
        });
    }

    internal static string QualificationEvidenceDirectory(string? configured = null)
    {
        configured ??= Environment.GetEnvironmentVariable(EvidenceDirectoryEnvVar);
        return !string.IsNullOrWhiteSpace(configured) ? Path.GetFullPath(configured) : Path.Combine(Path.GetTempPath(), "codespace-qualification-evidence");
    }

    internal static string SafeEvidenceFilePart(string value)
    {
        var safe = new string(value.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-').ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "suite" : safe;
    }

    private static (string BaseUrl, string ApiKey, string CandidateModel) LiveConnection()
    {
        var baseUrl = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var candidateModel = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);
        var present = new[] { baseUrl, apiKey, candidateModel }.Count(value => value is not null);
        if (present == 0) throw RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)");
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three or none.");
        return (baseUrl!, apiKey!, candidateModel!);
    }

    private sealed record LivePairedRehearsalRequest
    {
        public required HiddenSuite Suite { get; init; }
        public required string SuiteKind { get; init; }
        public required (string BaseUrl, string ApiKey, string CandidateModel) Connection { get; init; }
        public required int MinimumIndependentClusters { get; init; }
        public required int MinimumStrata { get; init; }
        public required int MinimumRequiredExecutionClusters { get; init; }
        public required double MinimumEvaluatorHealth { get; init; }
    }

    private sealed record FixedHiddenSuiteSource(HiddenSuite Suite) : IHiddenSuiteSource
    {
        public HiddenSuite? Load() => Suite;
    }

    private static object Redacted(PairedArmQualificationSummary arm) => new
    {
        arm.Solved, arm.BudgetAdmissibleSolved, arm.Total, arm.InfraUnknown, arm.CostKnownCells, arm.CapabilityVerdictCells, arm.ObservedModelCells,
        arm.EvaluatorHealth, arm.TotalCostUsd, arm.CostPerBudgetAdmissibleSolveUsd,
        observedModelDistinctCount = arm.ObservedModels.Count,
    };

    private static string CurrentRevision()
    {
        var revision = Environment.GetEnvironmentVariable(BenchmarkResultStore.GitShaEnvVar);
        if (BenchmarkEvidenceRevision.IsGitObjectId(revision)) return revision!;
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            ArgumentList = { "rev-parse", "HEAD" },
        }) ?? throw new InvalidOperationException("Could not start git to bind qualification evidence to the current revision.");
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Timed out while resolving the qualification evidence revision.");
        }
        revision = process.StandardOutput.ReadToEnd().Trim();
        return process.ExitCode == 0 && BenchmarkEvidenceRevision.IsGitObjectId(revision) ? revision : throw new InvalidOperationException("Could not resolve a full Git object id for qualification evidence.");
    }

    /// <summary>Same encrypted-credential seed shape as the benchmark corpus lane — the live key is read from the db by the executor, never in-process.</summary>
    private async Task<(Guid CredentialId, Guid ModelRowId)> SeedAgentCredentialAsync(Guid teamId, string baseUrl, string apiKey, string model)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var id = Guid.NewGuid();
        var modelRowId = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = id, TeamId = teamId, Provider = Provider, DisplayName = "qualification rehearsal cred",
            BaseUrl = baseUrl, EncryptedApiKey = encryptor.Encrypt(apiKey), Status = CredentialStatus.Active,
        });
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = modelRowId, ModelCredentialId = id, ModelId = model, Source = ModelSource.Manual, Enabled = true });
        await db.SaveChangesAsync();
        return (id, modelRowId);
    }
}
