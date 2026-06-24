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
/// <para>REPORT-ONLY for now (legacy three-way <see cref="RealModelGate.AssessLiveAsync(string, System.Func{System.Threading.Tasks.Task{System.ValueTuple{RealModelOutcome, string}}})"/>):
/// a brand-new live coding-over-corpus path REPORTS the solve-rate to the job summary — <see cref="RealModelOutcome.Drove"/>
/// = the real model solved ≥1 task, <see cref="RealModelOutcome.CapabilityMiss"/> = it ran but solved none — and only a
/// CODE FAULT reds. A gateway outage (every pair fails to execute) is non-gating LOUD infra
/// (<see cref="AgentExecutionInfraException"/>), never a false CapabilityMiss. Flip to a gating solve-rate FLOOR once a
/// first live run confirms the wiring AND a solve — gating main on an unproven live coding path would violate 穩.</para>
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

        await RealModelGate.AssessLiveAsync(Provider, async () =>
        {
            // The whole seed corpus runs under the REAL claude-code CLI authenticated by the seeded gateway credential,
            // at Trusted autonomy (it must reach the gateway + edit the workspace to solve). null harness/model on the
            // tasks is overridden by this selection — the SAME corpus the deterministic lane runs under a fake CLI.
            var selection = new BenchmarkAgentSelection { Harness = "claude-code", Model = model, ModelCredentialId = credId, Autonomy = AgentAutonomyLevel.Trusted };

            CorpusBenchmarkRun run;
            using (var scope = _fixture.BeginScope())
                run = await scope.Resolve<ICorpusBenchmarkRunner>().RunAsync(SeedBenchmarkCorpus.Tasks, teamId, selection, CancellationToken.None);

            var ran = run.Results.Count;
            var executed = run.Results.Count(r => r.RunStatus == AgentRunStatus.Succeeded);
            var solved = run.Results.Count(r => r.Grade.Passed);

            // No pair ran, OR every pair that ran FAILED execution (the claude CLI could not reach the gateway / could
            // not run) → infra, never a capability verdict. Mirrors the whole-loop's all-agents-failed classification:
            // a gateway outage must not red the lane as a false CapabilityMiss.
            if (executed == 0)
                throw new AgentExecutionInfraException($"no benchmark pair executed cleanly — ran={ran}, executed=0, errored={run.Errored.Count} (gateway/execution infra, not a capability miss)");

            // executed ≥ 1 → a genuine capability verdict: of the pairs the agent actually RAN, did it SOLVE any?
            var outcome = solved > 0 ? RealModelOutcome.Drove : RealModelOutcome.CapabilityMiss;
            var rate = (double)solved / executed;   // over the pairs that ran CLEANLY — a mid-corpus flake (a Failed-exec pair) is infra, never deflates the capability rate
            return (outcome, $"{Provider} model '{model}' seed-corpus solve-rate {solved}/{executed} cleanly-run ({rate:P0}) over {SeedBenchmarkCorpus.Tasks.Count} tasks × their modes — graded={ran}, errored={run.Errored.Count}");
        });
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
