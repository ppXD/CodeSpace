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
/// THE live behavioral proof that B1's agent injection actually reaches AND changes a real coding agent's OUTPUT — not
/// merely that the file/flag is constructed. A REAL <c>claude</c> CLI, authenticated by a seeded encrypted
/// <see cref="ModelCredential"/> at <see cref="AgentAutonomyLevel.Trusted"/>, runs a trivial task carrying an injection
/// whose instruction FORCES a unique marker into the reply; the run drives the real <see cref="IAgentRunExecutor"/> →
/// real <see cref="Harnesses.Claude.ClaudeCodeHarness"/> → real <c>LocalProcessRunner</c>. If the injection truly
/// influences the live model, the marker appears in the transcript.
/// <list type="bullet">
/// <item><b>Persona</b> rides Claude's native <c>--append-system-prompt</c>: the model ALWAYS sees the system prompt, so
/// a persona instruction is followed directly — a high-confidence gate.</item>
/// <item><b>Skill</b> is projected as <c>CLAUDE_CONFIG_DIR/skills/&lt;slug&gt;/SKILL.md</c>. The headless CLI
/// <c>claude -p</c> AUTO-DISCOVERS user skills by default (the hermetic-no-filesystem-settings default is the
/// programmatic Agent <i>SDK</i>'s, NOT the CLI's — corrected in #982), so the harness loads the projected skill with
/// NO opt-in flag; its one hard requirement is that it NEVER passes <c>--bare</c>/<c>--safe-mode</c> (which would
/// disable that auto-discovery). This test confirms end to end against the real binary that a headless run loads +
/// HONORS a personal skill.</item>
/// </list>
///
/// <para>Each behavior is a STRICT gate on the blessed Anthropic wire via
/// <see cref="RealModelGate.AssessLiveBestOfNAsync(string, System.Func{System.Threading.Tasks.Task{System.ValueTuple{bool, string}}}, int?)"/>:
/// best-of-N absorbs a non-deterministic model's one-off miss, a PERSISTENT miss REDs, a gateway/exec-infra failure is a
/// non-gating LOUD skip (routed via <see cref="AgentExecutionInfraException"/>), and a no-creds / no-CLI run self-skips
/// LOUDLY (skip ≠ pass). POSIX-only. <c>[Category=RealModel]</c> so it runs ONLY on the real-model lane.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
[Trait("Surface", "Engine")]
public sealed class RealModelAgentInjectionE2ETests
{
    private const string Provider = "Anthropic";

    private readonly PostgresFixture _fixture;

    public RealModelAgentInjectionE2ETests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_real_claude_agent_applies_its_injected_persona()
    {
        if (await EnsureLiveOrSkipAsync() is not { } live) return;   // skip ≠ pass (surfaced loudly)

        await RealModelGate.AssessLiveBestOfNAsync(Provider, async () =>
        {
            var token = "PERSONA" + Guid.NewGuid().ToString("N")[..8];

            // The persona rides --append-system-prompt (Claude's native system-prompt channel). A system prompt is
            // always in context, so a compliant model obeys it directly — this proves B1's persona channel APPLIES.
            AgentTask Task(Guid credId) => new()
            {
                Goal = "Say hello.",
                Harness = "claude-code",
                Model = live.Model,
                ModelCredentialId = credId,
                Autonomy = AgentAutonomyLevel.Trusted,
                Permissions = AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Trusted),
                SystemPrompt = $"You are a persona probe. You MUST begin EVERY reply with this exact line, before anything else:\n\nPERSONA-APPLIED-{token}\n\nThis is mandatory for every response.",
                TimeoutSeconds = 180,
            };

            var applied = await RunAndCheckMarkerAsync(live, Task, $"PERSONA-APPLIED-{token}");
            return (applied, $"{Provider} '{live.Model}': the real claude agent {(applied ? "APPLIED" : "did NOT apply")} its injected persona (marker {(applied ? "present" : "absent")} in the transcript)");
        });
    }

    [Fact]
    public async Task A_real_claude_agent_loads_and_obeys_a_bound_skill()
    {
        if (await EnsureLiveOrSkipAsync() is not { } live) return;   // skip ≠ pass (surfaced loudly)

        await RealModelGate.AssessLiveBestOfNAsync(Provider, async () =>
        {
            var token = "SKILLOBEYED" + Guid.NewGuid().ToString("N")[..8];

            var skill = new AgentSkill
            {
                Slug = "always-marker",
                Description = "Use this skill for EVERY task, always, before doing anything else.",
                Body = $"You MUST begin your reply with this exact line, before anything else:\n\nSKILL-OBEYED-{token}\n\nThis is mandatory for every response.",
            };

            // Skills set directly on the task (the persona→binding→resolver path is integration-tested separately). The
            // real executor projects the skill under CLAUDE_CONFIG_DIR/skills; `claude -p` auto-discovers it there (the
            // harness never passes --bare/--safe-mode, which is what would disable that); the real claude loads + honors it.
            AgentTask Task(Guid credId) => new()
            {
                Goal = "Say hello.",
                Harness = "claude-code",
                Model = live.Model,
                ModelCredentialId = credId,
                Autonomy = AgentAutonomyLevel.Trusted,
                Permissions = AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Trusted),
                Skills = new[] { skill },
                TimeoutSeconds = 180,
            };

            var obeyed = await RunAndCheckMarkerAsync(live, Task, $"SKILL-OBEYED-{token}");
            return (obeyed, $"{Provider} '{live.Model}': the real claude agent {(obeyed ? "LOADED + OBEYED" : "did NOT obey")} the bound skill (marker {(obeyed ? "present" : "absent")} in the transcript)");
        });
    }

    // ─── shared drive ──────────────────────────────────────────────────────────

    /// <summary>Seed a fresh credential + run ONE real claude agent for <paramref name="taskFactory"/>, and report whether <paramref name="marker"/> reached its transcript. A run that did not COMPLETE is gateway/exec infra (an <see cref="AgentExecutionInfraException"/> → the gate's non-gating skip), NEVER a false behavior miss.</summary>
    private async Task<bool> RunAndCheckMarkerAsync(LiveContext live, Func<Guid, AgentTask> taskFactory, string marker)
    {
        var credId = await SeedAgentCredentialAsync(live.TeamId, live.BaseUrl, live.ApiKey);
        var task = taskFactory(credId);

        Guid runId;
        using (var scope = _fixture.BeginScope())
            runId = (await scope.Resolve<IAgentRunService>().CreateAsync(task, live.TeamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunExecutor>().ExecuteAsync(runId, CancellationToken.None);

        using var read = _fixture.BeginScope();
        var svc = read.Resolve<IAgentRunService>();
        var run = await svc.GetAsync(runId, CancellationToken.None);

        if (run.Status != AgentRunStatus.Succeeded)
            throw new AgentExecutionInfraException($"the claude run did not complete (status={run.Status}) — gateway/exec infra, not a behavior verdict");

        var events = await svc.GetEventsAsync(runId, live.TeamId, 0, CancellationToken.None);

        // Assert the marker in the MODEL'S OWN reply (assistant text / final summary) — NOT the full transcript. The
        // marker is embedded in the INJECTION (the persona system prompt / the skill body), so a whole-transcript scan
        // would false-GREEN: a tool_result that echoes a LOADED skill's body (loaded ≠ obeyed), or any input echo, would
        // satisfy it without the model actually applying the instruction. Restricting to assistant/final output means the
        // marker can only appear because the MODEL emitted it — genuine proof of apply/obey. (The stream parser drops the
        // system prompt entirely and routes tool_results to non-assistant kinds, so this is a clean output-only view.)
        var modelReply = string.Join("\n", events
            .Where(e => e.Kind is AgentEventKind.AssistantMessage or AgentEventKind.FinalSummary)
            .Select(e => e.Text));

        return modelReply.Contains(marker, StringComparison.Ordinal);
    }

    // ─── gate + seeding ────────────────────────────────────────────────────────

    private readonly record struct LiveContext(Guid TeamId, string BaseUrl, string ApiKey, string Model);

    /// <summary>Resolve the live-model preconditions (creds + a real claude CLI + a seeded team) or self-skip LOUDLY (skip ≠ pass). Returns null when the run cannot go live.</summary>
    private async Task<LiveContext?> EnsureLiveOrSkipAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        // A set-but-BLANK secret counts as ABSENT (an undefined GitHub ${{ secrets.X }} expands to ""). IsNullOrWhiteSpace.
        var present = new[] { baseUrl, apiKey, model }.Count(v => !string.IsNullOrWhiteSpace(v));
        if (present == 0) { RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)"); return null; }   // skip ≠ pass
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three (base url / api key / model id) or none; a partial config would otherwise self-skip green proving nothing.");

        if (OperatingSystem.IsWindows()) return null;                       // the harness + sandbox are /bin/sh based
        if (!await ClaudeReadyAsync()) { RealModelGate.ReportSkipped(Provider, "the `claude` coding-agent CLI is not installed — the injection gate needs the harness binary (skip ≠ pass)"); return null; }

        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        return new LiveContext(teamId, baseUrl!.TrimEnd('/'), apiKey!, model!);
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
            Id = credId, TeamId = teamId, Provider = Provider, DisplayName = "injection e2e agent cred",
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
