using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using Shouldly;

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
        var corpus = new FakePairedCorpus();
        using var scope = _fixture.BeginScope();
        var runner = new PairedTaskLaunchQualificationRunner(new SuiteSource(Suite()), corpus, scope.Resolve<CodeSpaceDbContext>());

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
        corpus.Requests.Count.ShouldBe(2);
        corpus.Requests.Select(request => request.ObservationSession).ShouldBe(new[] { 0, 1 });
        corpus.Requests.ShouldAllBe(request => request.Control.Model == "control-model" && request.Control.ModelCredentialId == controlCredential && request.Control.MaxCostUsd == 3m);
        corpus.Requests.ShouldAllBe(request => request.Candidate.Model == "candidate-model" && request.Candidate.ModelCredentialId == candidateCredential && request.Candidate.MaxCostUsd == 3m);
        corpus.Requests.Select(request => request.ObservationGroupId).Distinct().ShouldHaveSingleItem().ShouldBe(outcome.ObservationGroupId);
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
        var runner = new PairedTaskLaunchQualificationRunner(new SuiteSource(Suite()), corpus, scope.Resolve<CodeSpaceDbContext>());

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
        var runner = new PairedTaskLaunchQualificationRunner(new SuiteSource(Suite()), corpus, scope.Resolve<CodeSpaceDbContext>());

        await Should.ThrowAsync<ArgumentException>(() => runner.RunAsync(new PairedQualificationRequest
        {
            TeamId = teamId, CodeRevision = "main", Control = Selection(controlRow), Candidate = Selection(candidateRow),
            Spec = new PairedQualificationSpec { SessionsPerCell = 1, MinimumIndependentClusters = 1, MinimumStrata = 1, MinimumRequiredExecutionClusters = 1, MinimumEvaluatorHealth = 1, MaxCostUsdPerLaunch = 3m, Criterion = PairedQualificationCriterion.Quality, OrderingSeed = "frozen-order" },
        }, CancellationToken.None));

        corpus.Requests.ShouldBeEmpty("revision provenance must be frozen before any paid cell can start");
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

    private static BenchmarkAgentSelection Selection(Guid rowId) => new() { Harness = "claude-code", Model = "caller-forged", ModelCredentialId = Guid.NewGuid(), ModelCredentialModelId = rowId };

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
        public Task<PairedCorpusBenchmarkRun> RunPairedAsync(PairedCorpusBenchmarkRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new PairedCorpusBenchmarkRun
            {
                Control = Run(false, "control-observed"),
                Candidate = Run(true, "candidate-observed"),
            });
        }

        private static CorpusBenchmarkRun Run(bool solved, string observed)
        {
            var result = new BenchmarkResult
            {
                TaskId = "generic-task", Mode = BenchmarkMode.TaskLaunchQuick, RunStatus = AgentRunStatus.Succeeded,
                Grade = new BenchmarkGrade { Passed = solved, Detail = solved ? "passed" : "failed" }, McpFullCatalog = false,
                ObservedModel = observed, CostUsd = 1m,
            };
            var cell = new CorpusCellOutcome { TaskId = result.TaskId, Mode = result.Mode, State = solved ? CorpusCellState.Solved : CorpusCellState.Unsolved };
            var task = Suite().Tasks;
            return new CorpusBenchmarkRun { ExecutionPath = BenchmarkExecutionPath.TaskLaunch, Results = new[] { result }, Errored = Array.Empty<CorpusBenchmarkError>(), Scorecard = BenchmarkScorecard.Compute(new[] { result }), Cells = new[] { cell }, SuiteVersion = EvalSuite.ManifestFor(task, "sha256:hidden").Version };
        }
    }
}
