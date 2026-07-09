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
/// THE live solve-rate gate (P2 slice 2) — the actual L2 signal: a REAL claude coding agent attempts the WHOLE seed
/// corpus and we report how many tasks it objectively SOLVED. It composes the proven instrument end to end — the real
/// <see cref="ICorpusBenchmarkRunner"/> stages each seed fixture in its FAILING start-state, drives the pair through
/// the real <c>IAgentRunExecutor</c> under a LIVE selection (the <c>claude-code</c> harness authenticated by a seeded,
/// encrypted <c>ModelCredential</c> at <see cref="AgentAutonomyLevel.Trusted"/>, so the agent reaches the gateway +
/// edits the workspace), and the objective <c>TestsPassGrader</c> re-runs each task's <c>check.sh</c>. The solve-rate
/// is the corpus's grades, NOT the agent's self-reports — the same honesty the deterministic 0.0 baseline
/// (<c>CorpusBenchmarkFlowTests</c>) makes executable, now with a real brain producing the real number.
///
/// <para>GATING solve-rate FLOOR (the SWE-bench-class number): the blessed wire passes only when the real coding agent
/// objectively SOLVES at least <see cref="MinSolveRate"/> of the cleanly-run corpus pairs — <see cref="RealModelOutcome.Drove"/>
/// = rate ≥ floor, <see cref="RealModelOutcome.CapabilityMiss"/> = it ran but fell SHORT of the floor → REDs the wire. A
/// gateway outage (every pair fails to execute) is non-gating LOUD infra (<see cref="AgentExecutionInfraException"/>),
/// never a false CapabilityMiss. attempts:1 — the rate is already an average over the 18 task×mode pairs (stable; a
/// per-corpus best-of-N would not fit the 60-min job budget) and the floor sits well below a capable model's expected
/// rate, so a single run clears it with margin. The robust upgrade of the single-task gating solve in the whole-loop arc.</para>
///
/// <para>Self-skips (skip ≠ pass, surfaced LOUDLY) when the <c>CODESPACE_LLM_*</c> secrets are absent or the
/// <c>claude</c> CLI is not installed; FAILS on a partial secret config. POSIX-only (the seed fixtures + checks are
/// /bin/sh). <c>[Category=RealModel]</c> so it runs ONLY on the real-model lane, never the e2e/integration lanes.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
[Trait("Surface", "Engine")]
public sealed class RealModelBenchmarkCorpusE2ETests
{
    private const string Provider = "Anthropic";   // the blessed wire (RealModelGate gates it)

    /// <summary>The GATING solve-rate FLOOR over the cleanly-run corpus pairs: a real coding agent must objectively SOLVE
    /// at least this fraction or the blessed wire REDs. Set CONSERVATIVELY below a capable model's expected rate (the
    /// 9-task corpus's easy tier alone is ~44% of the 18 task×mode pairs, and the model reliably clears the harder tier
    /// too), so a single corpus run clears it with margin — yet it is a genuine non-trivial solve-rate claim, not "solved
    /// ≥1". Raise it as the model's reliability is confirmed across runs; lower only if a dispatch shows it borderline.</summary>
    private const double MinSolveRate = 0.5;

    private readonly PostgresFixture _fixture;

    public RealModelBenchmarkCorpusE2ETests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_real_coding_agent_runs_the_seed_corpus_and_reports_a_live_solve_rate()
    {
        var baseUrl = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => v is not null);
        if (present == 0) { RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)"); return; }   // skip ≠ pass
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three (base url / api key / model id) or none; a partial config would otherwise self-skip the benchmark gate green proving nothing.");

        if (OperatingSystem.IsWindows()) return;                          // the seed fixtures + checks are /bin/sh scripts
        if (!await GitReadyAsync()) return;
        if (!await ClaudeReadyAsync()) { RealModelGate.ReportSkipped(Provider, "the `claude` coding-agent CLI is not installed — the benchmark needs a harness binary (skip ≠ pass)"); return; }   // honest-skip, NOT a pass

        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var credId = await SeedAgentCredentialAsync(teamId, baseUrl!.TrimEnd('/'), apiKey!);

        await RealModelGate.AssessLiveWholeLoopAsync(Provider, async () =>
        {
            // The whole seed corpus runs under the REAL claude-code CLI authenticated by the seeded gateway credential,
            // at Trusted autonomy (it must reach the gateway + edit the workspace to solve). null harness/model on the
            // tasks is overridden by this selection — the SAME corpus the deterministic lane runs under a fake CLI.
            var selection = new BenchmarkAgentSelection { Harness = "claude-code", Model = model, ModelCredentialId = credId, Autonomy = AgentAutonomyLevel.Trusted };

            CorpusBenchmarkRun run;
            using (var scope = _fixture.BeginScope())
                run = await scope.Resolve<ICorpusBenchmarkRunner>().RunAsync(SeedBenchmarkCorpus.Tasks, teamId, selection, CancellationToken.None);

            // P4.2 — reuse the ALREADY-HONEST BenchmarkScorecard/EvalScorecard denominator (run.Scorecard.Overall)
            // instead of re-deriving a stricter "RunStatus==Succeeded" filter here: that hand-rolled filter used to
            // DROP any TimedOut/Failed/NeedsReview pair from BOTH halves of the fraction — a pair that ran long enough
            // to time out (exactly what a longer, multi-phase corpus task makes more likely) would silently vanish
            // instead of counting as an attempted-but-unsolved task, inflating the reported rate. The scorecard's own
            // Total already counts every TERMINAL pair (Succeeded/Failed/TimedOut/NeedsReview/Cancelled) and its
            // Succeeded already means "the grader passed" (BenchmarkScorecard.ToOutcome), not merely "the CLI exited 0".
            var overall = run.Scorecard.Overall;
            var ran = run.Results.Count;

            // No pair reached a TERMINAL state at all (every pair errored during staging/execution — an infra fault,
            // never a capability verdict). Mirrors the whole-loop's all-agents-failed classification: a gateway
            // outage must not red the lane as a false CapabilityMiss.
            if (overall.Total == 0)
                throw new AgentExecutionInfraException($"no benchmark pair reached a terminal state — ran={ran}, errored={run.Errored.Count} (gateway/execution infra, not a capability miss)");

            // Total ≥ 1 → a genuine capability verdict: of every pair that ran to a terminal state (TimedOut/Failed
            // included), did the agent SOLVE at least the floor?
            var rate = overall.SuccessRate;
            var outcome = rate >= MinSolveRate ? RealModelOutcome.Drove : RealModelOutcome.CapabilityMiss;   // GATING FLOOR — short of the floor REDs the blessed wire (CapabilityMiss)
            return (outcome, $"{Provider} model '{model}' seed-corpus solve-rate {overall.Succeeded}/{overall.Total} ({rate:P0}) vs floor {MinSolveRate:P0} → {outcome} over {SeedBenchmarkCorpus.Tasks.Count} tasks × their modes — graded={ran}, errored={run.Errored.Count}");
        }, attempts: 1);
    }

    /// <summary>Seed an encrypted gateway <see cref="ModelCredential"/> the agent.code executor resolves via <c>ModelCredentialId</c> and the <c>ClaudeCodeHarness</c> projects onto its env (ANTHROPIC_BASE_URL / ANTHROPIC_API_KEY for Provider="Anthropic"). The same shape the whole-loop coding arm seeds — the live key is read from the DB, never in-process.</summary>
    private async Task<Guid> SeedAgentCredentialAsync(Guid teamId, string baseUrl, string apiKey)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();

        var credId = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = credId, TeamId = teamId, Provider = Provider, DisplayName = "benchmark agent cred",
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
