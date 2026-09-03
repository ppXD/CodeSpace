using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// 🟢 High fidelity, REPORT-ONLY (dispatch-only lane): the hidden-suite qualification REHEARSAL — the exact
/// <see cref="IQualificationRunner"/> chain the deployment's minting entry drives, against the operator-staged
/// suite at the conventional path, under the live gateway credential. Prints the would-be statistics (bound /
/// evaluator health / suite digest) so the operator can price and sanity-check a round BEFORE minting the durable
/// receipt on the deployment; the receipt this rehearsal appends lands in the JOB-LOCAL throwaway db and dies
/// with the job — a rehearsal never mints the deployment's claim. Self-skips loudly when the suite or the live
/// credential is absent (skip ≠ pass).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
[Trait("Surface", "Engine")]
public sealed class RealModelQualificationRehearsalE2ETests
{
    private const string Provider = "Anthropic";

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
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var credId = await SeedAgentCredentialAsync(teamId, baseUrl!.TrimEnd('/'), apiKey!);

        await RealModelGate.AssessLiveWholeLoopAsync(Provider, async () =>
        {
            var selection = new BenchmarkAgentSelection { Harness = "claude-code", Model = model, ModelCredentialId = credId, Autonomy = AgentAutonomyLevel.Trusted };
            var spec = new QualificationSpec { MinSolveRateLowerBound = RehearsalBar, MinEvaluatorHealth = 0.9, ValidityDays = 1 };

            QualificationOutcome outcome;
            using (var scope = _fixture.BeginScope())
                outcome = await scope.Resolve<IQualificationRunner>().QualifyAsync("supervisor", "git-branch", spec, teamId, selection, CancellationToken.None);

            var report = $"suite {outcome.SuiteDigest} ({suite.Tasks.Count} task(s)): solved {outcome.Score.Solved}/{outcome.Score.Total}, "
                       + $"one-sided 95% lower bound {outcome.SolveRateLowerBound:P1}, evaluator health {outcome.Score.EvaluatorHealth:P1} "
                       + $"→ at the {RehearsalBar:P0} rehearsal bar this round would grant {outcome.Granted}. "
                       + "Compare the BOUND to the bar you intend to mint with — the rehearsal receipt lives in the job-local db and dies with it.";
            Console.WriteLine($"[qualification-rehearsal] {report}");

            return (RealModelOutcome.Drove, report);   // report-only: the rehearsal's signal is the printout, never a capability gate
        });
    }

    /// <summary>Same encrypted-credential seed shape as the benchmark corpus lane — the live key is read from the db by the executor, never in-process.</summary>
    private async Task<Guid> SeedAgentCredentialAsync(Guid teamId, string baseUrl, string apiKey)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var id = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = id, TeamId = teamId, Provider = Provider, DisplayName = "qualification rehearsal cred",
            BaseUrl = baseUrl, EncryptedApiKey = encryptor.Encrypt(apiKey), Status = CredentialStatus.Active,
        });
        await db.SaveChangesAsync();
        return id;
    }
}
