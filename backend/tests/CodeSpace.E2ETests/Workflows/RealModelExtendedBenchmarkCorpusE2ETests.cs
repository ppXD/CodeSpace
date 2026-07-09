using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// P4.2 — the EXTENDED-tier solve-rate REPORT (not a gate): the same real-claude-agent-over-the-real-corpus-runner
/// proof as <see cref="RealModelBenchmarkCorpusE2ETests"/>, but over <see cref="SeedBenchmarkCorpus.ExtendedTasks"/>
/// (harder, multi-step algorithms new to the corpus, with an unproven natural solve-rate) instead of the blessed
/// 9-task <see cref="SeedBenchmarkCorpus.Tasks"/>. REPORT-ONLY (<c>gating: false</c> — never asserts) on every wire:
/// these tasks have no established floor yet, so a low first-run rate must surface loudly without redding main. Runs
/// only via this class's OWN opt-in CI lane (workflow_dispatch), never the push-triggered corpus job, so a harder /
/// possibly-slower pair can never put the existing 60-minute gating floor's budget at risk.
///
/// <para>Same self-skip contract as the blessed corpus test: skips (never a pass) when <c>CODESPACE_LLM_*</c> secrets
/// or the <c>claude</c> CLI are absent, fails on a partial secret config, and is POSIX-only.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
[Trait("Surface", "Engine")]
public sealed class RealModelExtendedBenchmarkCorpusE2ETests
{
    private const string Provider = "Anthropic";

    private readonly PostgresFixture _fixture;

    public RealModelExtendedBenchmarkCorpusE2ETests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_real_coding_agent_runs_the_extended_benchmark_corpus_and_reports_a_live_solve_rate()
    {
        var baseUrl = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => v is not null);
        if (present == 0) { RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)"); return; }   // skip ≠ pass
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three (base url / api key / model id) or none; a partial config would otherwise self-skip the benchmark report green proving nothing.");

        if (OperatingSystem.IsWindows()) return;                          // the seed fixtures + checks are /bin/sh scripts
        if (!await GitReadyAsync()) return;
        if (!await ClaudeReadyAsync()) { RealModelGate.ReportSkipped(Provider, "the `claude` coding-agent CLI is not installed — the benchmark needs a harness binary (skip ≠ pass)"); return; }   // honest-skip, NOT a pass

        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var credId = await SeedAgentCredentialAsync(teamId, baseUrl!.TrimEnd('/'), apiKey!);

        await RealModelGate.AssessLiveAsync(Provider, async () =>
        {
            // The whole EXTENDED corpus runs under the REAL claude-code CLI authenticated by the seeded gateway
            // credential, at Trusted autonomy — the same shape as the blessed corpus, over the harder task set.
            var selection = new BenchmarkAgentSelection { Harness = "claude-code", Model = model, ModelCredentialId = credId, Autonomy = AgentAutonomyLevel.Trusted };

            CorpusBenchmarkRun run;
            using (var scope = _fixture.BeginScope())
                run = await scope.Resolve<ICorpusBenchmarkRunner>().RunAsync(SeedBenchmarkCorpus.ExtendedTasks, teamId, selection, CancellationToken.None);

            // Reuse the same honest BenchmarkScorecard/EvalScorecard denominator as the blessed corpus test (P4.2) —
            // Total counts every TERMINAL pair (Succeeded/Failed/TimedOut/NeedsReview/Cancelled), Succeeded means "the
            // grader passed". A gateway/execution outage (every pair errored before reaching a terminal state) is
            // infra, routed to the non-gating skip path — never a false "0% solve rate" report.
            var overall = run.Scorecard.Overall;
            var ran = run.Results.Count;

            if (overall.Total == 0)
                throw new AgentExecutionInfraException($"no extended-benchmark pair reached a terminal state — ran={ran}, errored={run.Errored.Count} (gateway/execution infra, not a capability signal)");

            var rate = overall.SuccessRate;

            // report-only (gating:false below) — Ok carries no pass/fail meaning here, only whether the run itself
            // completed a genuine capability signal; the real information is the rate in the verdict text.
            return (true, $"{Provider} model '{model}' EXTENDED-corpus solve-rate {overall.Succeeded}/{overall.Total} ({rate:P0}) over {SeedBenchmarkCorpus.ExtendedTasks.Count} tasks × their modes — graded={ran}, errored={run.Errored.Count} (report-only — new/unproven-difficulty tasks, no floor set yet)");
        }, gating: false);
    }

    /// <summary>Seed an encrypted gateway <see cref="ModelCredential"/> the agent.code executor resolves via <c>ModelCredentialId</c> and the <c>ClaudeCodeHarness</c> projects onto its env (ANTHROPIC_BASE_URL / ANTHROPIC_API_KEY for Provider="Anthropic"). Mirrors the blessed corpus test's seeding.</summary>
    private async Task<Guid> SeedAgentCredentialAsync(Guid teamId, string baseUrl, string apiKey)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();

        var credId = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = credId, TeamId = teamId, Provider = Provider, DisplayName = "extended benchmark agent cred",
            EncryptedApiKey = encryptor.Encrypt(apiKey), BaseUrl = baseUrl, Status = CredentialStatus.Active,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return credId;
    }

    /// <summary>Whether real <c>git</c> is on PATH — the benchmark stages + grades with shell tooling; self-skips when absent.</summary>
    private static async Task<bool> GitReadyAsync()
    {
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 15 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>Whether the real <c>claude</c> coding-agent CLI is on PATH — the gate self-skips (NOT a pass) when it is absent (fork/local, or a runner without the install step).</summary>
    private static async Task<bool> ClaudeReadyAsync()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "claude", Args = new[] { "--version" }, TimeoutSeconds = 15 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }
}
