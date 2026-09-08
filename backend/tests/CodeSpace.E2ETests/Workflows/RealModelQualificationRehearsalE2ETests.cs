using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
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
/// 🟢 High fidelity, REPORT-ONLY (dispatch-only lane): the hidden-suite qualification rehearsal drives the deployed
/// <see cref="IQualificationRunner"/> receipt chain and the paired TaskLaunch chain against the operator-staged suite
/// under live gateway credentials. It reports bounds, health, digest, exact revision and redacted observed-model
/// identities. Every receipt and paired cell lands only in the job-local database; the rehearsal never mints a
/// deployment claim. It self-skips loudly when the suite or live credential is absent (skip ≠ pass).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
[Trait("Surface", "Engine")]
public sealed class RealModelQualificationRehearsalE2ETests
{
    private const string Provider = "Anthropic";
    private const string BaselineModelEnvVar = "CODESPACE_LLM_BASELINE_MODEL_ID";

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
        var baseUrl = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var candidateModel = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);
        var present = new[] { baseUrl, apiKey, candidateModel }.Count(value => value is not null);
        if (present == 0) throw RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)");
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three or none.");
        var suite = HiddenSuiteLoader.LoadFromDefaultLocation() ?? throw RealModelGate.ReportSkipped(Provider, $"no hidden suite at '{HiddenSuiteLoader.DefaultSuiteDirectory}' — skip ≠ pass");
        if (OperatingSystem.IsWindows()) return;

        var baselineModel = Environment.GetEnvironmentVariable(BaselineModelEnvVar);
        var usedFallbackBaseline = string.IsNullOrWhiteSpace(baselineModel);
        baselineModel = usedFallbackBaseline ? candidateModel : baselineModel;
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture, inProcessPool: false);
        var (controlCredentialId, controlRowId) = await SeedAgentCredentialAsync(teamId, baseUrl!.TrimEnd('/'), apiKey!, baselineModel!);
        var (candidateCredentialId, candidateRowId) = await SeedAgentCredentialAsync(teamId, baseUrl.TrimEnd('/'), apiKey, candidateModel!);

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
                    SessionsPerCell = 1, MinimumIndependentClusters = 40, MinimumStrata = 8, MinimumRequiredExecutionClusters = 1, MinimumEvaluatorHealth = 0.98,
                    MaxCostUsdPerLaunch = 5m, Criterion = PairedQualificationCriterion.Quality,
                    MinimumQualityLift = 0.05, OrderingSeed = "paired-tasklaunch-q1-v1",
                },
            };

            PairedQualificationOutcome outcome;
            using (var scope = _fixture.BeginScope()) outcome = await scope.Resolve<IPairedTaskLaunchQualificationRunner>().RunAsync(request, CancellationToken.None);

            outcome.SuiteDigest.ShouldBe(suite.SuiteContentHash);
            outcome.PairedCells.ShouldBe(suite.Tasks.Sum(task => task.Modes.Count));
            using (var scope = _fixture.BeginScope())
            {
                var durable = await scope.Resolve<CodeSpaceDbContext>().BenchmarkResultRecord.AsNoTracking().Where(row => row.ObservationGroupId == outcome.ObservationGroupId).ToListAsync();
                durable.Count.ShouldBe(outcome.PairedCells * 2, "every control/candidate cell must be durably appendable before the report can exist");
                durable.ShouldAllBe(row => row.GitSha == outcome.CodeRevision && row.ObservationSession == 0);
                durable.Count(row => row.ObservationArm == "control" && row.ModelCredentialModelId == controlRowId).ShouldBe(outcome.PairedCells);
                durable.Count(row => row.ObservationArm == "candidate" && row.ModelCredentialModelId == candidateRowId).ShouldBe(outcome.PairedCells);
            }
            if (usedFallbackBaseline)
            {
                outcome.QualifiedForCapabilityClaim.ShouldBeFalse("one configured model can exercise the paired plumbing but cannot become its own independent baseline");
                outcome.BlockingReasons.ShouldContain("identical-observed-model");
            }

            var evidence = new
            {
                schema = PairedQualificationOutcome.StatisticsVersion,
                outcome.ObservationGroupId,
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
            };
            Directory.CreateDirectory("backend/TestResults");
            await File.WriteAllTextAsync("backend/TestResults/paired-tasklaunch-qualification.json", JsonSerializer.Serialize(evidence, AgentJson.Options));
            var report = $"paired suite {outcome.SuiteDigest} at {outcome.CodeRevision}: {outcome.PairedCells} cell pairs / {outcome.IndependentClusters} independent clusters, "
                       + $"budget-admissible solves control {outcome.Control.BudgetAdmissibleSolved}/{outcome.Control.Total}, candidate {outcome.Candidate.BudgetAdmissibleSolved}/{outcome.Candidate.Total}, "
                       + $"paired difference {outcome.QualityDifference:P1}, cluster-bootstrap lower 95% {outcome.QualityDifferenceLower95:P1}, qualified {outcome.QualifiedForCapabilityClaim}; blockers [{string.Join(',', outcome.BlockingReasons)}].";
            Console.WriteLine($"[paired-qualification-rehearsal] {report}");
            return (RealModelOutcome.Drove, report);
        });
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
