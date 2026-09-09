using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Exceptions;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeSpace.IntegrationTests.Agents;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PairedQualificationRunnerFlowTests
{
    private readonly PostgresFixture _fixture;
    public PairedQualificationRunnerFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Exact_team_rows_are_canonicalized_and_every_session_uses_one_matched_cap()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (controlCredential, controlRow) = await SeedModelAsync(teamId, "control-model");
        var (candidateCredential, candidateRow) = await SeedModelAsync(teamId, "candidate-model");
        var protocolChecks = 0;
        string? persistedProtocolDigest = null;
        var corpus = new FakePairedCorpus
        {
            BeforeRun = async request =>
            {
                var digest = await AssertProtocolPersistedAsync(request, teamId, controlRow, candidateRow);
                if (persistedProtocolDigest is null) persistedProtocolDigest = digest;
                else digest.ShouldBe(persistedProtocolDigest);
                protocolChecks++;
            },
            AfterRun = PersistFakePairAsync,
        };
        using var scope = _fixture.BeginScope();
        var runner = Runner(scope, corpus);

        var outcome = await runner.RunAsync(new PairedQualificationRequest
        {
            TeamId = teamId, CodeRevision = new string('a', 40),
            Control = Selection(controlRow), Candidate = Selection(candidateRow),
            Spec = new PairedQualificationSpec
            {
                SessionsPerCell = 2, MinimumIndependentClusters = 1, MinimumStrata = 1, MinimumRequiredExecutionClusters = 1, MinimumEvaluatorHealth = 1,
                MaxCostUsdPerLaunch = 3m, Criterion = PairedQualificationCriterion.Quality,
                MinimumQualityLift = 0.05, OrderingSeed = "frozen-order",
            },
        }, CancellationToken.None);

        outcome.QualifiedForCapabilityClaim.ShouldBeTrue();
        outcome.PairedCells.ShouldBe(2);
        outcome.ProtocolDigest.ShouldBe(persistedProtocolDigest, "the report must name the exact pre-outcome row every paid session used");
        corpus.Requests.Count.ShouldBe(2);
        corpus.Requests.Select(request => request.ObservationSession).ShouldBe(new[] { 0, 1 });
        protocolChecks.ShouldBe(2, "the immutable protocol must commit before every paid paired session can start");
        corpus.Requests.ShouldAllBe(request => request.Control.Model == "control-model" && request.Control.ModelCredentialId == controlCredential && request.Control.MaxCostUsd == 3m);
        corpus.Requests.ShouldAllBe(request => request.Candidate.Model == "candidate-model" && request.Candidate.ModelCredentialId == candidateCredential && request.Candidate.MaxCostUsd == 3m);
        corpus.Requests.Select(request => request.ObservationGroupId).Distinct().ShouldHaveSingleItem().ShouldBe(outcome.ObservationGroupId);
        await AssertProtocolImmutableAsync(outcome.ObservationGroupId);
        await AssertResultSealedAndImmutableAsync(outcome);
    }

    [Fact]
    public async Task A_foreign_model_row_is_rejected_before_any_paid_cell_runs()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (foreignTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (_, controlRow) = await SeedModelAsync(teamId, "control-model");
        var (_, foreignRow) = await SeedModelAsync(foreignTeamId, "foreign-model");
        var corpus = new FakePairedCorpus();
        using var scope = _fixture.BeginScope();
        var runner = Runner(scope, corpus);

        await Should.ThrowAsync<InvalidOperationException>(() => runner.RunAsync(new PairedQualificationRequest
        {
            TeamId = teamId, CodeRevision = new string('a', 40), Control = Selection(controlRow), Candidate = Selection(foreignRow),
            Spec = new PairedQualificationSpec { SessionsPerCell = 1, MinimumIndependentClusters = 1, MinimumStrata = 1, MinimumRequiredExecutionClusters = 1, MinimumEvaluatorHealth = 1, MaxCostUsdPerLaunch = 3m, Criterion = PairedQualificationCriterion.Quality, OrderingSeed = "frozen-order" },
        }, CancellationToken.None));

        corpus.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_non_object_revision_is_rejected_before_any_paid_cell_runs()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (_, controlRow) = await SeedModelAsync(teamId, "control-model");
        var (_, candidateRow) = await SeedModelAsync(teamId, "candidate-model");
        var corpus = new FakePairedCorpus();
        using var scope = _fixture.BeginScope();
        var runner = Runner(scope, corpus);

        await Should.ThrowAsync<ArgumentException>(() => runner.RunAsync(new PairedQualificationRequest
        {
            TeamId = teamId, CodeRevision = "main", Control = Selection(controlRow), Candidate = Selection(candidateRow),
            Spec = new PairedQualificationSpec { SessionsPerCell = 1, MinimumIndependentClusters = 1, MinimumStrata = 1, MinimumRequiredExecutionClusters = 1, MinimumEvaluatorHealth = 1, MaxCostUsdPerLaunch = 3m, Criterion = PairedQualificationCriterion.Quality, OrderingSeed = "frozen-order" },
        }, CancellationToken.None));

        corpus.Requests.ShouldBeEmpty("revision provenance must be frozen before any paid cell can start");
    }

    [Fact]
    public async Task A_complete_in_memory_report_cannot_seal_when_one_or_more_durable_observations_are_missing()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (_, controlRow) = await SeedModelAsync(teamId, "control-model");
        var (_, candidateRow) = await SeedModelAsync(teamId, "candidate-model");
        var corpus = new FakePairedCorpus();
        using var scope = _fixture.BeginScope();
        var runner = Runner(scope, corpus);

        var failure = await Should.ThrowAsync<DurableQualificationResultException>(() => runner.RunAsync(new PairedQualificationRequest
        {
            TeamId = teamId, CodeRevision = new string('b', 40), Control = Selection(controlRow), Candidate = Selection(candidateRow),
            Spec = new PairedQualificationSpec { SessionsPerCell = 1, MinimumIndependentClusters = 1, MinimumStrata = 1, MinimumRequiredExecutionClusters = 1, MinimumEvaluatorHealth = 1, MaxCostUsdPerLaunch = 3m, Criterion = PairedQualificationCriterion.Quality, OrderingSeed = "frozen-order" },
        }, CancellationToken.None));

        failure.Message.ShouldContain("observation-census-mismatch");
        var groupId = corpus.Requests.Single().ObservationGroupId;
        (await scope.Resolve<CodeSpaceDbContext>().PairedQualificationResult.AsNoTracking().AnyAsync(value => value.ObservationGroupId == groupId)).ShouldBeFalse();
        using var recoveryScope = _fixture.BeginScope();
        var recovery = new PairedQualificationRecoveryService(new SuiteSource(Suite()), recoveryScope.Resolve<CodeSpaceDbContext>(), recoveryScope.Resolve<IPairedQualificationResultStore>());
        var recoveryFailure = await Should.ThrowAsync<DurableQualificationResultException>(() => recovery.RecoverAsync(groupId, CancellationToken.None));
        recoveryFailure.Message.ShouldContain("observation-census-mismatch");
        corpus.Requests.Count.ShouldBe(1, "an incomplete campaign cannot dispatch replacement paid work during recovery");
    }

    [Fact]
    public async Task A_complete_campaign_crashing_before_seal_recovers_from_durable_evidence_without_new_paid_calls()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (_, controlRow) = await SeedModelAsync(teamId, "control-model");
        var (_, candidateRow) = await SeedModelAsync(teamId, "candidate-model");
        var corpus = new FakePairedCorpus { AfterRun = PersistFakePairAsync };
        var crash = new CrashBeforeSeal();
        using (var runScope = _fixture.BeginScope())
        {
            var runner = new PairedTaskLaunchQualificationRunner(new SuiteSource(Suite()), corpus, runScope.Resolve<CodeSpaceDbContext>(), crash);
            await Should.ThrowAsync<SimulatedCrashException>(() => runner.RunAsync(new PairedQualificationRequest
            {
                TeamId = teamId, CodeRevision = new string('c', 40), Control = Selection(controlRow), Candidate = Selection(candidateRow),
                Spec = new PairedQualificationSpec { SessionsPerCell = 2, MinimumIndependentClusters = 1, MinimumStrata = 1, MinimumRequiredExecutionClusters = 1, MinimumEvaluatorHealth = 1, MaxCostUsdPerLaunch = 3m, Criterion = PairedQualificationCriterion.Quality, MinimumQualityLift = 0.05, OrderingSeed = "frozen-order" },
            }, CancellationToken.None));
        }

        corpus.Requests.Count.ShouldBe(2);
        var groupId = corpus.Requests.Select(request => request.ObservationGroupId).Distinct().Single();
        using (var driftScope = _fixture.BeginScope())
        {
            var drift = new PairedQualificationRecoveryService(new SuiteSource(Suite() with { SuiteContentHash = "sha256:drift" }), driftScope.Resolve<CodeSpaceDbContext>(), driftScope.Resolve<IPairedQualificationResultStore>());
            var mismatch = await Should.ThrowAsync<DurableQualificationResultException>(() => drift.RecoverAsync(groupId, CancellationToken.None));
            mismatch.Message.ShouldContain("sealed-suite-mismatch");
            (await driftScope.Resolve<CodeSpaceDbContext>().PairedQualificationResult.AsNoTracking().AnyAsync(value => value.ObservationGroupId == groupId)).ShouldBeFalse();
        }
        PairedQualificationOutcome recovered;
        using (var recoveryScope = _fixture.BeginScope())
        {
            var recovery = new PairedQualificationRecoveryService(new SuiteSource(Suite()), recoveryScope.Resolve<CodeSpaceDbContext>(), recoveryScope.Resolve<IPairedQualificationResultStore>());
            recovered = await recovery.RecoverAsync(groupId, CancellationToken.None);
            var alreadySealed = await Should.ThrowAsync<DurableQualificationResultException>(() => recovery.RecoverAsync(groupId, CancellationToken.None));
            alreadySealed.Message.ShouldContain("result-already-sealed");
        }

        corpus.Requests.Count.ShouldBe(2, "recovery may read already-paid rows but must never call the corpus/model again");
        recovered.QualifiedForCapabilityClaim.ShouldBeTrue();
        recovered.ProtocolDigest.ShouldNotBeNull().Length.ShouldBe(64);
        recovered.EvidenceDigest.ShouldNotBeNull().Length.ShouldBe(64);
        recovered.ResultDigest.ShouldNotBeNull().Length.ShouldBe(64);
        recovered.QualityDifference.ShouldBe(crash.Attempted!.QualityDifference);
        recovered.QualityDifferenceLower95.ShouldBe(crash.Attempted.QualityDifferenceLower95);
        AssertArmEquivalent(recovered.Control, crash.Attempted.Control);
        AssertArmEquivalent(recovered.Candidate, crash.Attempted.Candidate);
        recovered.BlockingReasons.ShouldBe(crash.Attempted.BlockingReasons);
        await AssertResultSealedAndImmutableAsync(recovered);
    }

    private async Task<(Guid CredentialId, Guid RowId)> SeedModelAsync(Guid teamId, string model)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var credentialId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential { Id = credentialId, TeamId = teamId, Provider = "Custom", DisplayName = model, BaseUrl = "https://example.test", EncryptedApiKey = scope.Resolve<IPayloadEncryptor>().Encrypt("key"), Status = CredentialStatus.Active });
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = rowId, ModelCredentialId = credentialId, ModelId = model, Source = ModelSource.Manual, Enabled = true });
        await db.SaveChangesAsync();
        return (credentialId, rowId);
    }

    private async Task<string> AssertProtocolPersistedAsync(PairedCorpusBenchmarkRequest request, Guid teamId, Guid controlRow, Guid candidateRow)
    {
        using var scope = _fixture.BeginScope();
        var connection = scope.Resolve<CodeSpaceDbContext>().Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT team_id, suite_digest, suite_version, code_revision, control_model_row_id, candidate_model_row_id,
                   statistics_version, criterion, sessions_per_cell, minimum_independent_clusters, minimum_strata,
                   minimum_required_execution_clusters, minimum_evaluator_health, max_cost_usd_per_launch,
                   minimum_quality_lift, non_inferiority_margin, minimum_cost_reduction, require_distinct_observed_models,
                   ordering_seed, protocol_digest
            FROM paired_qualification_protocol
            WHERE observation_group_id = @group_id
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "group_id";
        parameter.Value = request.ObservationGroupId;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue("pre-registration must be durable before a paid corpus call");
        reader.GetGuid(0).ShouldBe(teamId);
        reader.GetString(1).ShouldBe("sha256:hidden");
        reader.GetString(2).ShouldBe(EvalSuite.ManifestFor(Suite().Tasks, "sha256:hidden").Version);
        reader.GetString(3).ShouldBe(new string('a', 40));
        reader.GetGuid(4).ShouldBe(controlRow);
        reader.GetGuid(5).ShouldBe(candidateRow);
        reader.GetString(6).ShouldBe(PairedQualificationOutcome.StatisticsVersion);
        reader.GetString(7).ShouldBe(nameof(PairedQualificationCriterion.Quality));
        reader.GetInt32(8).ShouldBe(2);
        reader.GetInt32(9).ShouldBe(1);
        reader.GetInt32(10).ShouldBe(1);
        reader.GetInt32(11).ShouldBe(1);
        reader.GetDouble(12).ShouldBe(1);
        reader.GetDecimal(13).ShouldBe(3m);
        reader.GetDouble(14).ShouldBe(0.05);
        reader.GetDouble(15).ShouldBe(-0.02);
        reader.GetDouble(16).ShouldBe(0.20);
        reader.GetBoolean(17).ShouldBeTrue();
        reader.GetString(18).ShouldBe("frozen-order");
        var digest = reader.GetString(19);
        digest.Length.ShouldBe(64);
        (await reader.ReadAsync()).ShouldBeFalse();
        return digest;
    }

    private async Task AssertProtocolImmutableAsync(Guid groupId)
    {
        using var updateScope = _fixture.BeginScope();
        var update = await Should.ThrowAsync<PostgresException>(() => updateScope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE paired_qualification_protocol SET sessions_per_cell = 99 WHERE observation_group_id = {groupId}"));
        update.SqlState.ShouldBe(PostgresErrorCodes.RaiseException);
        update.MessageText.ShouldContain("paired qualification protocol is immutable");

        using var deleteScope = _fixture.BeginScope();
        var delete = await Should.ThrowAsync<PostgresException>(() => deleteScope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"DELETE FROM paired_qualification_protocol WHERE observation_group_id = {groupId}"));
        delete.SqlState.ShouldBe(PostgresErrorCodes.RaiseException);
        delete.MessageText.ShouldContain("paired qualification protocol is immutable");
    }

    private async Task PersistFakePairAsync(PairedCorpusBenchmarkRequest request, PairedCorpusBenchmarkRun run)
    {
        using var scope = _fixture.BeginScope();
        var store = scope.Resolve<IBenchmarkResultStore>();
        foreach (var (arm, selection, result) in new[]
        {
            ("control", request.Control, run.Control.Results.Single()),
            ("candidate", request.Candidate, run.Candidate.Results.Single()),
        })
        {
            await store.RecordAsync(new BenchmarkObservationWrite
            {
                TeamId = request.TeamId, SuiteVersion = run.Control.SuiteVersion!, Result = result, Selection = selection,
                ObservationGroupId = request.ObservationGroupId, ObservationArm = arm, ObservationSession = request.ObservationSession,
                CodeRevision = request.CodeRevision,
            }, CancellationToken.None);
        }
    }

    private async Task AssertResultSealedAndImmutableAsync(PairedQualificationOutcome outcome)
    {
        outcome.EvidenceDigest.ShouldNotBeNull().Length.ShouldBe(64);
        outcome.ResultDigest.ShouldNotBeNull().Length.ShouldBe(64);
        using (var readScope = _fixture.BeginScope())
        {
            var row = await readScope.Resolve<CodeSpaceDbContext>().PairedQualificationResult.AsNoTracking().SingleAsync(value => value.ObservationGroupId == outcome.ObservationGroupId);
            row.ProtocolDigest.ShouldBe(outcome.ProtocolDigest);
            row.EvidenceDigest.ShouldBe(outcome.EvidenceDigest);
            row.ResultDigest.ShouldBe(outcome.ResultDigest);
            row.StatisticsVersion.ShouldBe(PairedQualificationOutcome.StatisticsVersion);
            row.ExpectedObservationCount.ShouldBe(4);
            row.ObservationCount.ShouldBe(4);
            row.QualifiedForCapabilityClaim.ShouldBeTrue();
            row.OutcomeJson.ShouldContain("\"qualityDifferenceLower95\"");
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { row.ProtocolDigest, row.EvidenceDigest, row.StatisticsVersion, row.OutcomeJson }, CodeSpace.Core.Services.Agents.AgentJson.Options)))).ShouldBe(row.ResultDigest);
        }

        using var updateScope = _fixture.BeginScope();
        var update = await Should.ThrowAsync<PostgresException>(() => updateScope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE paired_qualification_result SET qualified_for_capability_claim = FALSE WHERE observation_group_id = {outcome.ObservationGroupId}"));
        update.SqlState.ShouldBe(PostgresErrorCodes.RaiseException);
        update.MessageText.ShouldContain("paired qualification result is immutable");

        using var deleteScope = _fixture.BeginScope();
        var delete = await Should.ThrowAsync<PostgresException>(() => deleteScope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"DELETE FROM paired_qualification_result WHERE observation_group_id = {outcome.ObservationGroupId}"));
        delete.SqlState.ShouldBe(PostgresErrorCodes.RaiseException);
        delete.MessageText.ShouldContain("paired qualification result is immutable");

        using var appendScope = _fixture.BeginScope();
        var db = appendScope.Resolve<CodeSpaceDbContext>();
        var protocol = await db.PairedQualificationProtocol.AsNoTracking().SingleAsync(value => value.ObservationGroupId == outcome.ObservationGroupId);
        var append = await Should.ThrowAsync<DbUpdateException>(() => appendScope.Resolve<IBenchmarkResultStore>().RecordAsync(new BenchmarkObservationWrite
        {
            TeamId = protocol.TeamId, SuiteVersion = protocol.SuiteVersion,
            Result = FakePairedCorpus.Result(false, "control-observed"),
            Selection = Selection(protocol.ControlModelRowId) with { MaxCostUsd = protocol.MaxCostUsdPerLaunch },
            ObservationGroupId = protocol.ObservationGroupId, ObservationArm = "control", ObservationSession = protocol.SessionsPerCell,
            CodeRevision = protocol.CodeRevision,
        }, CancellationToken.None));
        append.InnerException.ShouldBeOfType<PostgresException>().MessageText.ShouldContain("paired qualification result is sealed");
    }

    private static void AssertArmEquivalent(PairedArmQualificationSummary actual, PairedArmQualificationSummary expected)
    {
        actual.Solved.ShouldBe(expected.Solved);
        actual.BudgetAdmissibleSolved.ShouldBe(expected.BudgetAdmissibleSolved);
        actual.Total.ShouldBe(expected.Total);
        actual.InfraUnknown.ShouldBe(expected.InfraUnknown);
        actual.CostKnownCells.ShouldBe(expected.CostKnownCells);
        actual.CapabilityVerdictCells.ShouldBe(expected.CapabilityVerdictCells);
        actual.ObservedModelCells.ShouldBe(expected.ObservedModelCells);
        actual.EvaluatorHealth.ShouldBe(expected.EvaluatorHealth);
        actual.ObservedModels.ShouldBe(expected.ObservedModels);
        actual.TotalCostUsd.ShouldBe(expected.TotalCostUsd);
        actual.CostPerBudgetAdmissibleSolveUsd.ShouldBe(expected.CostPerBudgetAdmissibleSolveUsd);
    }

    private static BenchmarkAgentSelection Selection(Guid rowId) => new() { Harness = "claude-code", Model = "caller-forged", ModelCredentialId = Guid.NewGuid(), ModelCredentialModelId = rowId };

    private static PairedTaskLaunchQualificationRunner Runner(ILifetimeScope scope, IPairedCorpusBenchmarkRunner corpus) => new(new SuiteSource(Suite()), corpus, scope.Resolve<CodeSpaceDbContext>(), scope.Resolve<IPairedQualificationResultStore>());

    private static HiddenSuite Suite()
    {
        var task = new BenchmarkTask
        {
            Id = "generic-task", Description = "generic", Goal = "solve", FixtureRef = "fixture", Harness = "claude-code",
            Grading = BenchmarkGradingKind.TestsPass, TestCommand = new[] { "sh", "check.sh" }, Modes = new[] { BenchmarkMode.TaskLaunchQuick },
            Stratum = "generic", IndependenceCluster = "source", RequiresCompleteExecution = true,
        };
        return new HiddenSuite(new[] { task }, "sha256:hidden", new NoopStager());
    }

    private sealed record SuiteSource(HiddenSuite Suite) : IHiddenSuiteSource { public HiddenSuite Load() => Suite; }
    private sealed class NoopStager : IBenchmarkFixtureStager { public void Stage(string fixtureRef, string directory) { } }

    private sealed class FakePairedCorpus : IPairedCorpusBenchmarkRunner
    {
        public List<PairedCorpusBenchmarkRequest> Requests { get; } = new();
        public Func<PairedCorpusBenchmarkRequest, Task>? BeforeRun { get; init; }
        public Func<PairedCorpusBenchmarkRequest, PairedCorpusBenchmarkRun, Task>? AfterRun { get; init; }
        public async Task<PairedCorpusBenchmarkRun> RunPairedAsync(PairedCorpusBenchmarkRequest request, CancellationToken cancellationToken)
        {
            if (BeforeRun is not null) await BeforeRun(request);
            Requests.Add(request);
            var run = new PairedCorpusBenchmarkRun
            {
                Control = Run(false, "control-observed"),
                Candidate = Run(true, "candidate-observed"),
            };
            if (AfterRun is not null) await AfterRun(request, run);
            return run;
        }

        private static CorpusBenchmarkRun Run(bool solved, string observed)
        {
            var result = Result(solved, observed);
            var cell = new CorpusCellOutcome { TaskId = result.TaskId, Mode = result.Mode, State = solved ? CorpusCellState.Solved : CorpusCellState.Unsolved };
            var task = Suite().Tasks;
            return new CorpusBenchmarkRun { ExecutionPath = BenchmarkExecutionPath.TaskLaunch, Results = new[] { result }, Errored = Array.Empty<CorpusBenchmarkError>(), Scorecard = BenchmarkScorecard.Compute(new[] { result }), Cells = new[] { cell }, SuiteVersion = EvalSuite.ManifestFor(task, "sha256:hidden").Version };
        }

        public static BenchmarkResult Result(bool solved, string observed) => new()
        {
            TaskId = "generic-task", Mode = BenchmarkMode.TaskLaunchQuick, RunStatus = AgentRunStatus.Succeeded,
            Grade = new BenchmarkGrade { Passed = solved, Detail = solved ? "passed" : "failed" }, McpFullCatalog = false,
            ObservedModel = observed, CostUsd = 1m,
        };
    }

    private sealed class CrashBeforeSeal : IPairedQualificationResultStore
    {
        public PairedQualificationOutcome? Attempted { get; private set; }
        public Task<PairedQualificationOutcome> SealAsync(PairedQualificationSealRequest request, CancellationToken cancellationToken)
        {
            Attempted = request.Outcome;
            throw new SimulatedCrashException();
        }
    }

    private sealed class SimulatedCrashException : Exception;
}
