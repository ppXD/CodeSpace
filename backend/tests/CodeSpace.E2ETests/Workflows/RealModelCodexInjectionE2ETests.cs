using System.Diagnostics;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
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
/// THE live behavioral proof for the CODEX half of B1 — the exact counterpart to
/// <see cref="RealModelAgentInjectionE2ETests"/> (Claude). A REAL <c>codex</c> CLI, authenticated by a seeded encrypted
/// <see cref="ModelCredential"/>, runs a trivial task carrying an injection whose instruction FORCES a unique marker
/// into the reply; the run drives the real <see cref="IAgentRunExecutor"/> → real <see cref="CodexHarness"/> → real
/// <c>LocalProcessRunner</c>. If the injection truly influences the live model, the marker appears in the MODEL'S OWN
/// output.
/// <list type="bullet">
/// <item><b>Persona</b> rides Codex's config-home <c>$CODEX_HOME/AGENTS.md</c> (codex <c>exec</c> has no
/// system-prompt flag — B1 writes the persona there, and the real binary merges it into the turn).</item>
/// <item><b>Skill</b> is projected as <c>$CODEX_HOME/skills/&lt;slug&gt;/SKILL.md</c>, which Codex's native loader
/// discovers — the same Agent-Skills format as Claude, only the config-home root differs.</item>
/// </list>
///
/// <para><b>Why REPORT-ONLY, not a hard gate:</b> Codex talks the OpenAI <c>responses</c> wire
/// (<see cref="CodexHarness.ModelProviderWireApi"/>), whereas the shared gateway the <c>CODESPACE_LLM_*</c> secrets
/// point at is only known to serve OpenAI <i>chat/completions</i> + an Anthropic-family model id. So a live Codex run
/// may not COMPLETE on that gateway — that is a WIRE/INFRA mismatch, never a behavior miss. This test therefore seeds
/// the credential as the non-blessed <c>OpenAI</c> wire: <see cref="RealModelGate.AssessLiveBestOfNAsync(string, System.Func{System.Threading.Tasks.Task{System.ValueTuple{bool, string}}}, int?)"/>
/// runs it ONCE and REPORTS its verdict (an operator blesses it via <c>CODESPACE_REALMODEL_REQUIRED_PROVIDERS</c> once
/// their gateway serves the responses wire), and a non-completing run is a non-gating LOUD skip (routed via
/// <see cref="AgentExecutionInfraException"/>). The DETERMINISTIC, gateway-free floor for Codex persona + skill LOADING
/// is <see cref="RealCodexResumeE2ETests"/> (real binary, rollout-level). A no-creds / no-CLI run self-skips LOUDLY
/// (skip ≠ pass). POSIX-only. <c>[Category=RealModel]</c> so it runs ONLY on the real-model lane.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
[Trait("Surface", "Engine")]
public sealed class RealModelCodexInjectionE2ETests : IDisposable
{
    // Codex routes through the OpenAI-family wire (see CodexHarness.SupportedProviders); tag the credential OpenAI so the
    // gate treats this as the INFORMATIONAL wire — a gateway that can't serve Codex's `responses` protocol never reds.
    private const string Provider = "OpenAI";

    private readonly PostgresFixture _fixture;
    private readonly List<string> _tempDirs = new();

    public RealModelCodexInjectionE2ETests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_real_codex_agent_applies_its_injected_persona()
    {
        if (await EnsureLiveOrSkipAsync() is not { } live) return;   // skip ≠ pass (surfaced loudly)

        await RealModelGate.AssessLiveBestOfNAsync(Provider, async () =>
        {
            var token = "PERSONA" + Guid.NewGuid().ToString("N")[..8];

            // The persona rides $CODEX_HOME/AGENTS.md (codex exec has no system-prompt flag). Codex merges it into every
            // turn, so a compliant model obeys it directly — this proves B1's Codex persona channel APPLIES live.
            AgentTask Task(Guid credId) => new()
            {
                Goal = "Say hello.",
                Harness = CodexHarness.HarnessKind,
                Model = live.Model,
                ModelCredentialId = credId,
                WorkspaceDirectory = NewGitWorkspace(),
                Autonomy = AgentAutonomyLevel.Trusted,
                Permissions = AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Trusted),
                SystemPrompt = $"You are a persona probe. You MUST begin EVERY reply with this exact line, before anything else:\n\nPERSONA-APPLIED-{token}\n\nThis is mandatory for every response.",
                TimeoutSeconds = 180,
            };

            var applied = await RunAndCheckMarkerAsync(live, Task, $"PERSONA-APPLIED-{token}");
            return (applied, $"{Provider} '{live.Model}': the real codex agent {(applied ? "APPLIED" : "did NOT apply")} its injected persona (marker {(applied ? "present" : "absent")} in the model's reply)");
        });
    }

    [Fact]
    public async Task A_real_codex_agent_loads_and_uses_a_bound_skill()
    {
        if (await EnsureLiveOrSkipAsync() is not { } live) return;   // skip ≠ pass (surfaced loudly)

        // OpenAI is the INFORMATIONAL wire → AssessLiveBestOfNAsync runs ONCE and reports (never gates). Same
        // codeword-necessity design as the Claude skill test: the task's correct answer requires a secret that lives
        // ONLY in the skill body, so a model that answers correctly MUST auto-invoke the skill — a strong, realistic
        // trigger + a clean loaded-vs-used discriminator (unlike a trivial task + an "always do X" skill).
        await RealModelGate.AssessLiveBestOfNAsync(Provider, async () =>
        {
            var codeword = "CODEWORD-" + Guid.NewGuid().ToString("N")[..12];

            var skill = new AgentSkill
            {
                Slug = "greeting-codeword",
                Description = "Provides the required secret codeword that every greeting must contain. Consult this skill whenever you greet the user.",
                Body = $"The required secret greeting codeword is {codeword}.\n\nWhenever you write a greeting, you MUST include the exact codeword {codeword} verbatim in it.",
            };

            // The real executor projects the skill under $CODEX_HOME/skills/<slug>/SKILL.md; Codex's native loader
            // discovers it. Skills set directly on the task (the resolver path is integration-tested elsewhere).
            AgentTask Task(Guid credId) => new()
            {
                Goal = "Greet the user in one short sentence. Your greeting must include the required secret greeting codeword.",
                Harness = CodexHarness.HarnessKind,
                Model = live.Model,
                ModelCredentialId = credId,
                WorkspaceDirectory = NewGitWorkspace(),
                Autonomy = AgentAutonomyLevel.Trusted,
                Permissions = AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Trusted),
                Skills = new[] { skill },
                TimeoutSeconds = 180,
            };

            var used = await RunAndCheckMarkerAsync(live, Task, codeword);
            return (used, $"{Provider} '{live.Model}': the real codex agent {(used ? "LOADED + USED" : "did NOT use")} the bound skill (skill-only codeword {(used ? "present" : "absent")} in the model's greeting)");
        });
    }

    // ─── shared drive ──────────────────────────────────────────────────────────

    /// <summary>Seed a fresh credential + run ONE real codex agent for <paramref name="taskFactory"/>, and report whether <paramref name="marker"/> reached the MODEL'S reply. A run that did not COMPLETE is gateway/exec/wire infra (an <see cref="AgentExecutionInfraException"/> → the gate's non-gating skip) — the likely path when the gateway does not serve Codex's <c>responses</c> wire — NEVER a false behavior miss.</summary>
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
            throw new AgentExecutionInfraException($"the codex run did not complete (status={run.Status}; error={run.Error ?? "(none)"}) — gateway/exec/wire infra (the shared gateway may not serve Codex's `responses` wire), not a behavior verdict");

        var events = await svc.GetEventsAsync(runId, live.TeamId, 0, CancellationToken.None);

        // Assert the marker in the MODEL'S OWN reply (assistant text / final summary) — NOT the full transcript. The
        // marker is embedded in the INJECTION (the persona AGENTS.md / the skill body), so a whole-transcript / rollout
        // scan would false-GREEN: Codex records the loaded AGENTS.md + skill as user-instruction items BEFORE the model
        // call (loaded ≠ obeyed). Restricting to assistant/final output means the marker can only appear because the
        // MODEL emitted it — genuine proof of apply/obey, the same discriminator the Claude injection E2E uses.
        var modelReply = string.Join("\n", events
            .Where(e => e.Kind is AgentEventKind.AssistantMessage or AgentEventKind.FinalSummary)
            .Select(e => e.Text));

        return modelReply.Contains(marker, StringComparison.Ordinal);
    }

    // ─── gate + seeding ────────────────────────────────────────────────────────

    private readonly record struct LiveContext(Guid TeamId, string BaseUrl, string ApiKey, string Model);

    /// <summary>Resolve the live-model preconditions (creds + a real codex CLI + a seeded team) or self-skip LOUDLY (skip ≠ pass). Returns null when the run cannot go live.</summary>
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
        if (!await CodexReadyAsync()) { RealModelGate.ReportSkipped(Provider, "the `codex` coding-agent CLI is not installed — the injection gate needs the harness binary (skip ≠ pass)"); return null; }

        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        return new LiveContext(teamId, baseUrl!.TrimEnd('/'), apiKey!, model!);
    }

    /// <summary>Seed an encrypted gateway <see cref="ModelCredential"/> the executor resolves via <c>ModelCredentialId</c> and the CodexHarness projects onto its env (OPENAI_API_KEY + the OPENAI_BASE_URL carrier it re-injects as a <c>-c</c> model-provider override). The live key is read from the DB, never in-process.</summary>
    private async Task<Guid> SeedAgentCredentialAsync(Guid teamId, string baseUrl, string apiKey)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();

        var credId = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = credId, TeamId = teamId, Provider = Provider, DisplayName = "codex injection e2e agent cred",
            EncryptedApiKey = encryptor.Encrypt(apiKey), BaseUrl = baseUrl, Status = CredentialStatus.Active,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return credId;
    }

    /// <summary>A fresh git-initialised temp workspace — <c>codex exec</c> refuses to run outside a trusted git repo, and the executor provisions NO workspace for a no-repo task, so the test supplies one (mirrors <see cref="RealCodexResumeE2ETests"/>). Tracked for teardown.</summary>
    private string NewGitWorkspace()
    {
        var ws = Path.Combine(Path.GetTempPath(), "cs-codex-inject-" + Guid.NewGuid().ToString("N"), "ws");
        Directory.CreateDirectory(ws);
        _tempDirs.Add(Directory.GetParent(ws)!.FullName);
        RunGitInit(ws);
        return ws;
    }

    private static void RunGitInit(string cwd)
    {
        var psi = new ProcessStartInfo { FileName = "git", WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("init");
        psi.ArgumentList.Add("-q");
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
    }

    /// <summary>Whether the real <c>codex</c> coding-agent CLI is on PATH — the gate self-skips (NOT a pass) when it is absent (fork/local, or a runner without the install step).</summary>
    private static async Task<bool> CodexReadyAsync()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "codex", Args = new[] { "--version" }, TimeoutSeconds = 15 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup of the gated run's temp workspaces */ }
    }
}
