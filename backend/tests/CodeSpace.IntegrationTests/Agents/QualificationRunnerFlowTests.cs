using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Completion;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// 🟡 Medium-mock (real Postgres + real receipt store + real statistics; faked suite source + corpus runner —
/// the corpus machinery has its own coverage): Q2's qualification runner end to end — the hidden suite's digest,
/// the frozen-denominator score, the one-sided lower bound, and the granted tier all land on ONE immutable
/// receipt row; an infra-riddled round mints Shadow evidence, never a sealed claim; an absent suite throws
/// (misconfiguration, never a silent pass).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class QualificationRunnerFlowTests
{
    private readonly PostgresFixture _fixture;

    public QualificationRunnerFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_healthy_round_above_the_bar_mints_a_sealed_receipt()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var mode = "supervisor-" + Guid.NewGuid().ToString("N")[..6];

        using var scope = _fixture.BeginScope();
        var runner = Runner(scope, cells: Cells(solved: 19, unsolved: 1, infra: 0));

        var outcome = await runner.QualifyAsync(mode, "git-branch", Spec(minLowerBound: 0.7), teamId, Selection(), CancellationToken.None);

        outcome.Granted.ShouldBe(PerformanceQualification.Sealed, $"19/20 bounds at ~{outcome.SolveRateLowerBound:F3} — above the 0.7 bar");
        outcome.SuiteDigest.ShouldBe("sha256:fake-suite");

        var row = await scope.Resolve<CodeSpaceDbContext>().QualificationReceipt.AsNoTracking().SingleAsync(r => r.Id == outcome.ReceiptId);
        row.GrantedPerformance.ShouldBe(PerformanceQualification.Sealed);
        row.SuiteDigest.ShouldBe("sha256:fake-suite", "the digest pins WHICH tasks were run — a later suite edit can never claim this number");

        var metrics = JsonDocument.Parse(row.MetricsJson!).RootElement;
        metrics.GetProperty("solved").GetInt32().ShouldBe(19);
        metrics.GetProperty("solveRateLowerBound").GetDouble().ShouldBe(outcome.SolveRateLowerBound);

        // Q5: the round's identity lands as the TYPED nouns — a claim reader parses them back verbatim.
        var cohort = JsonDocument.Parse(row.CohortJson!).RootElement;
        cohort.GetProperty("teamId").GetGuid().ShouldBe(teamId);
        cohort.GetProperty("mode").GetString().ShouldBe(mode);
        cohort.GetProperty("tier").GetString().ShouldBe("internal-qualification");
        cohort.GetProperty("completionPolicyVersion").GetInt32().ShouldBe(CodeSpace.Core.Services.Completion.CompletionPolicy.CurrentVersion);
        JsonDocument.Parse(row.VerifierBundleJson!).RootElement.GetProperty("harness").GetString().ShouldBe("codex-cli");
    }

    [Fact]
    public async Task An_infra_riddled_round_mints_shadow_evidence_never_a_seal()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var mode = "supervisor-" + Guid.NewGuid().ToString("N")[..6];

        using var scope = _fixture.BeginScope();
        var runner = Runner(scope, cells: Cells(solved: 19, unsolved: 0, infra: 5));

        var outcome = await runner.QualifyAsync(mode, "git-branch", Spec(minLowerBound: 0.5), teamId, Selection(), CancellationToken.None);

        outcome.Granted.ShouldBe(PerformanceQualification.Shadow, "a broken evaluator proves nothing about the model — the round records evidence, no sealed claim");
        (await scope.Resolve<IQualificationReceiptStore>().ListCurrentAsync(mode, "git-branch", DateTimeOffset.UtcNow, CancellationToken.None))
            .ShouldHaveSingleItem().GrantedPerformance.ShouldBe(PerformanceQualification.Shadow);
    }

    [Fact]
    public async Task An_absent_suite_throws_never_a_silent_pass()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScope();
        var runner = new QualificationRunner(new FakeSuiteSource(null), new FakeCorpusRunner(Array.Empty<CorpusCellOutcome>()),
            scope.Resolve<IQualificationReceiptStore>(), NullLogger<QualificationRunner>.Instance);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            runner.QualifyAsync("supervisor", "git-branch", Spec(0.5), teamId, Selection(), CancellationToken.None));

        ex.Message.ShouldContain("misconfiguration");
    }

    [Fact]
    public async Task The_minting_entry_surfaces_an_absent_suite_as_a_misconfiguration()
    {
        // Q-ops: the global-admin command → handler → runner wiring, driven end-to-end. On a host with no staged
        // suite (this CI runner), the round must THROW naming the misconfiguration — never mint, never silently pass.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScopeAs(userId, teamId, CodeSpace.Messages.Constants.Roles.Admin);
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => scope.Resolve<MediatR.IMediator>().Send(new CodeSpace.Messages.Commands.Agents.RunQualificationRoundCommand
        {
            Mode = "supervisor", CapabilityKey = "git-branch", MinSolveRateLowerBound = 0.5,
        }, CancellationToken.None));

        ex.Message.ShouldContain("misconfiguration");
    }

    // ─── Plumbing ────────────────────────────────────────────────────────────────

    private static QualificationRunner Runner(ILifetimeScope scope, IReadOnlyList<CorpusCellOutcome> cells) =>
        new(new FakeSuiteSource(new HiddenSuite(new[] { Task_() }, "sha256:fake-suite")), new FakeCorpusRunner(cells),
            scope.Resolve<IQualificationReceiptStore>(), NullLogger<QualificationRunner>.Instance);

    private static QualificationSpec Spec(double minLowerBound) => new() { MinSolveRateLowerBound = minLowerBound, MinEvaluatorHealth = 0.9, ValidityDays = 30 };

    private static BenchmarkAgentSelection Selection() => new() { Harness = "codex-cli", Model = "test-model" };

    private static BenchmarkTask Task_() => new() { Id = "t1", Description = "d", Goal = "g", FixtureRef = "f1", Harness = "codex-cli", Modes = new[] { BenchmarkMode.HarnessCli }, Grading = BenchmarkGradingKind.TestsPass, TestCommand = new[] { "sh", "check.sh" } };

    private static IReadOnlyList<CorpusCellOutcome> Cells(int solved, int unsolved, int infra)
    {
        var cells = new List<CorpusCellOutcome>();
        for (var i = 0; i < solved; i++) cells.Add(new CorpusCellOutcome { TaskId = $"s{i}", Mode = BenchmarkMode.HarnessCli, State = CorpusCellState.Solved });
        for (var i = 0; i < unsolved; i++) cells.Add(new CorpusCellOutcome { TaskId = $"u{i}", Mode = BenchmarkMode.HarnessCli, State = CorpusCellState.Unsolved });
        for (var i = 0; i < infra; i++) cells.Add(new CorpusCellOutcome { TaskId = $"i{i}", Mode = BenchmarkMode.HarnessCli, State = CorpusCellState.InfraUnknown });
        return cells;
    }

    private sealed class FakeSuiteSource : IHiddenSuiteSource
    {
        private readonly HiddenSuite? _suite;
        public FakeSuiteSource(HiddenSuite? suite) => _suite = suite;
        public HiddenSuite? Load() => _suite;
    }

    private sealed class FakeCorpusRunner : ICorpusBenchmarkRunner
    {
        private readonly IReadOnlyList<CorpusCellOutcome> _cells;
        public FakeCorpusRunner(IReadOnlyList<CorpusCellOutcome> cells) => _cells = cells;

        public Task<CorpusBenchmarkRun> RunAsync(IReadOnlyList<BenchmarkTask> corpus, Guid teamId, BenchmarkAgentSelection? selection, CancellationToken cancellationToken) =>
            Task.FromResult(new CorpusBenchmarkRun
            {
                Results = Array.Empty<BenchmarkResult>(),
                Errored = Array.Empty<CorpusBenchmarkError>(),
                Scorecard = new CodeSpace.Messages.Agents.AgentRunScorecard { Harnesses = Array.Empty<CodeSpace.Messages.Agents.HarnessScore>(), Overall = new CodeSpace.Messages.Agents.HarnessScore { Harness = "overall", Total = 0, Succeeded = 0, SuccessRate = 0 } },
                Cells = _cells,
            });
    }
}
