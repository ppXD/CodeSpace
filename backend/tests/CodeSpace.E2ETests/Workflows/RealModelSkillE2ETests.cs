using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// THE live proof that a projected skill actually reaches AND influences a real coding agent: a REAL <c>claude</c>
/// CLI, authenticated by a seeded encrypted <see cref="ModelCredential"/> at <see cref="AgentAutonomyLevel.Trusted"/>,
/// runs a trivial task with ONE bound skill whose body FORCES a unique marker into the reply. The run drives the real
/// <see cref="IAgentRunExecutor"/> → real <see cref="Harnesses.Claude.ClaudeCodeHarness"/> → real
/// <c>LocalProcessRunner</c>, which materializes the SKILL.md under the run's <c>CLAUDE_CONFIG_DIR/skills</c>; the live
/// model loads it (progressive disclosure) and — if the projection truly reached it — emits the marker. The always-on
/// <c>RealHarnessSkillProjectionTests</c> proves the file LANDS where the CLI scans with a fake CLI; THIS is the only
/// tier that proves the live model LOADS + OBEYS it.
///
/// <para>INFORMATIONAL on capability: the skill verdict is REPORTED (marker present → <see cref="RealModelOutcome.Drove"/>;
/// a clean run without it → <see cref="RealModelOutcome.CapabilityMiss"/>) and gates ONLY a <see cref="RealModelOutcome.CodeFault"/>
/// (an engine fault while executing the run) — NOT a CapabilityMiss. Whether <c>claude --print</c> loads + triggers a
/// personal <c>CLAUDE_CONFIG_DIR/skills</c> skill is not yet established in-repo for the pinned CLI, so a structural
/// non-load must surface as a reported verdict in the job summary, never a persistent false-RED on the blessed wire —
/// promote to the strict gating <c>AssessLiveWholeLoopAsync</c> only once <c>--print</c> skill-loading is confirmed. A run
/// that did not complete → <see cref="AgentExecutionInfraException"/> (gateway/exec infra, a non-gating LOUD skip).
/// Self-skips (skip ≠ pass, surfaced loudly) when <c>CODESPACE_LLM_*</c> are absent or the <c>claude</c> CLI is not
/// installed; FAILS on a partial secret config. POSIX-only. <c>[Category=RealModel]</c> so it runs ONLY on the
/// real-model lane.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
[Trait("Surface", "Engine")]
public sealed class RealModelSkillE2ETests
{
    private const string Provider = "Anthropic";

    private readonly PostgresFixture _fixture;

    public RealModelSkillE2ETests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_real_claude_agent_loads_and_obeys_a_bound_skill()
    {
        var baseUrl = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        // Count a set-but-BLANK secret as ABSENT (the shape an undefined GitHub-Actions ${{ secrets.X }} expands to —
        // an empty string, not unset): IsNullOrWhiteSpace, so a partial config can't slip past present==0 + present==3
        // and seed an empty key. (RealModelLiveWire.Env does this normalization but is internal to IntegrationTests.)
        var present = new[] { baseUrl, apiKey, model }.Count(v => !string.IsNullOrWhiteSpace(v));
        if (present == 0) { RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)"); return; }   // skip ≠ pass
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three (base url / api key / model id) or none; a partial config would otherwise self-skip the skill gate green proving nothing.");

        if (OperatingSystem.IsWindows()) return;                          // the harness + sandbox are /bin/sh based
        if (!await ClaudeReadyAsync()) { RealModelGate.ReportSkipped(Provider, "the `claude` coding-agent CLI is not installed — the skill gate needs the harness binary (skip ≠ pass)"); return; }   // honest-skip, NOT a pass

        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var credId = await SeedAgentCredentialAsync(teamId, baseUrl!.TrimEnd('/'), apiKey!);

        // INFORMATIONAL (three-way report-only) — gates ONLY a CodeFault, REPORTS a CapabilityMiss. A structural
        // --print-doesn't-load-skills miss is deterministic, so the strict best-of-N gate would harden it into a
        // persistent false-RED on the blessed wire; reporting it instead surfaces the verdict without blocking main.
        await RealModelGate.AssessLiveAsync(Provider, async () =>
        {
            // A fresh marker per attempt so a stale transcript can never satisfy a retry.
            var token = "SKILLOBEYED" + Guid.NewGuid().ToString("N")[..8];
            var skill = new AgentSkill
            {
                Slug = "always-marker",
                Description = "Use this skill for EVERY task, always, before doing anything else.",
                Body = $"You MUST begin your reply with this exact line, before anything else:\n\nSKILL-OBEYED-{token}\n\nThis is mandatory for every response.",
            };

            // Skills set directly on the task (the persona→binding→resolver path is integration-tested separately) +
            // Trusted so the agent reaches the gateway. The real executor projects the skill, the real claude loads it.
            var task = new AgentTask
            {
                Goal = "Say hello.",
                Harness = "claude-code",
                Model = model,
                ModelCredentialId = credId,
                Autonomy = AgentAutonomyLevel.Trusted,
                Permissions = AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Trusted),
                Skills = new[] { skill },
                TimeoutSeconds = 180,
            };

            Guid runId;
            using (var scope = _fixture.BeginScope())
                runId = (await scope.Resolve<IAgentRunService>().CreateAsync(task, teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;

            using (var scope = _fixture.BeginScope())
                await scope.Resolve<IAgentRunExecutor>().ExecuteAsync(runId, CancellationToken.None);

            using var read = _fixture.BeginScope();
            var svc = read.Resolve<IAgentRunService>();
            var run = await svc.GetAsync(runId, CancellationToken.None);

            // A run that did not COMPLETE is gateway/exec infra (the live claude could not reach the gateway / run),
            // NOT a skill-capability verdict — route it to the non-gating infra skip, never a false CapabilityMiss.
            if (run.Status != AgentRunStatus.Succeeded)
                throw new AgentExecutionInfraException($"the claude run did not complete (status={run.Status}) — gateway/exec infra, not a skill verdict");

            var events = await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None);
            var transcript = string.Join("\n", events.Select(e => e.Text)) + "\n" + (run.ResultJson ?? "");

            var obeyed = transcript.Contains($"SKILL-OBEYED-{token}", StringComparison.Ordinal);
            var note = $"{Provider} '{model}': the real claude agent {(obeyed ? "LOADED + OBEYED" : "did NOT obey")} the bound skill (marker {(obeyed ? "present" : "absent")} in the transcript)";
            return (obeyed ? RealModelOutcome.Drove : RealModelOutcome.CapabilityMiss, note);
        });
    }

    /// <summary>Seed an encrypted gateway <see cref="ModelCredential"/> the executor resolves via <c>ModelCredentialId</c> and the ClaudeCodeHarness projects onto its env (ANTHROPIC_BASE_URL / ANTHROPIC_API_KEY). The live key is read from the DB, never in-process.</summary>
    private async Task<Guid> SeedAgentCredentialAsync(Guid teamId, string baseUrl, string apiKey)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();

        var credId = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = credId, TeamId = teamId, Provider = Provider, DisplayName = "skill e2e agent cred",
            EncryptedApiKey = encryptor.Encrypt(apiKey), BaseUrl = baseUrl, Status = CredentialStatus.Active,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return credId;
    }

    /// <summary>Whether the real <c>claude</c> coding-agent CLI is on PATH — the gate self-skips (NOT a pass) when it is absent (fork/local, or a runner without the install step).</summary>
    private static async Task<bool> ClaudeReadyAsync()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "claude", Args = new[] { "--version" }, TimeoutSeconds = 15 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }
}
