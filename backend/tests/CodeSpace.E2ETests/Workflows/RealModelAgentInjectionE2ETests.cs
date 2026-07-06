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
/// disable that auto-discovery). But a loaded skill is APPLIED only if the model AUTO-INVOKES it (progressive
/// disclosure — a model decision, NOT deterministic), so this test drives a task whose correct answer REQUIRES a secret
/// codeword that lives ONLY inside the skill body — a strong, realistic trigger AND a clean loaded-vs-used
/// discriminator: the codeword can reach the reply only if the live model read + used the skill.</item>
/// </list>
///
/// <para><b>Gate policy — deterministic GATES, probabilistic REPORTS:</b> the PERSONA rides the system prompt (ALWAYS in
/// context), so its application is deterministic → a STRICT best-of-N GATE on the blessed Anthropic wire
/// (<see cref="RealModelGate.AssessLiveBestOfNAsync(string, System.Func{System.Threading.Tasks.Task{System.ValueTuple{bool, string}}}, int?)"/>);
/// a persistent non-apply REDs main. The SKILL is applied only via the model's AUTO-INVOCATION, so gating main on it
/// would red on model-capability variance — its live obedience is instead REPORTED
/// (<see cref="RealModelGate.AssessLiveAsync(string, System.Func{System.Threading.Tasks.Task{System.ValueTuple{bool, string}}}, bool)"/>
/// with <c>gating: false</c>), while skill LOADING (the SKILL.md lands where the CLI scans) is GATED deterministically by
/// <c>RealHarnessSkillProjectionTests</c>. This mirrors the gate's own code-fault-GATES / capability-miss-REPORTS split.
/// A gateway/exec-infra failure is a non-gating LOUD skip (routed via <see cref="AgentExecutionInfraException"/>), and a
/// no-creds / no-CLI run self-skips LOUDLY (skip ≠ pass). POSIX-only. <c>[Category=RealModel]</c> so it runs ONLY on the
/// real-model lane.</para>
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

            var (applied, _) = await RunAndCheckMarkerAsync(live, Task, $"PERSONA-APPLIED-{token}");
            var verdict = $"{Provider} '{live.Model}': the real claude agent {(applied ? "APPLIED" : "did NOT apply")} its injected persona (marker {(applied ? "present" : "absent")} in the model's reply)";
            Console.WriteLine($"[injection-e2e] persona: {verdict}");   // ALSO to stdout — the step-summary sink isn't greppable from the CLI/API
            return (applied, verdict);
        });
    }

    [Fact]
    public async Task A_real_claude_agent_loads_and_uses_a_bound_skill()
    {
        if (await EnsureLiveOrSkipAsync() is not { } live) return;   // skip ≠ pass (surfaced loudly)

        // REPORT-ONLY (gating: false): a loaded skill is APPLIED only via the model's AUTO-INVOCATION (progressive
        // disclosure), a capability that can drift with model versions — gating main on it would flake-red. Skill
        // LOADING is gated deterministically by RealHarnessSkillProjectionTests; here we DRIVE the live model + REPORT
        // whether it actually used the skill (surfaced in the job summary), never blocking main. Single attempt (a
        // report needs no best-of-N). The codeword-necessity design below makes a single attempt a strong signal.
        await RealModelGate.AssessLiveAsync(Provider, async () =>
        {
            var codeword = "CODEWORD-" + Guid.NewGuid().ToString("N")[..12];

            // Codeword-NECESSITY: the task's correct answer REQUIRES a secret that lives ONLY in the skill body, so a
            // model that wants to answer correctly MUST auto-invoke the skill to learn it — a strong, realistic trigger
            // (unlike a trivial "say hello" + an "always do X" skill, which progressive disclosure rightly ignores). It
            // is also a clean discriminator: the codeword can reach the reply only if the skill was LOADED + READ.
            var skill = new AgentSkill
            {
                Slug = "greeting-codeword",
                Description = "Provides the required secret codeword that every greeting must contain. Consult this skill whenever you greet the user.",
                Body = $"The required secret greeting codeword is {codeword}.\n\nWhenever you write a greeting, you MUST include the exact codeword {codeword} verbatim in it.",
            };

            // Skills set directly on the task (the persona→binding→resolver path is integration-tested separately). The
            // real executor projects the skill under CLAUDE_CONFIG_DIR/skills; `claude -p` auto-discovers it there (the
            // harness never passes --bare/--safe-mode, which is what would disable that); the model then chooses to use it.
            AgentTask Task(Guid credId) => new()
            {
                Goal = "Greet the user in one short sentence. Your greeting must include the required secret greeting codeword.",
                Harness = "claude-code",
                Model = live.Model,
                ModelCredentialId = credId,
                Autonomy = AgentAutonomyLevel.Trusted,
                Permissions = AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Trusted),
                Skills = new[] { skill },
                TimeoutSeconds = 180,
            };

            return await DriveSkillReportAsync(live, Task, codeword, attempts: 3);
        }, gating: false);
    }

    // ─── shared drive ──────────────────────────────────────────────────────────

    /// <summary>Seed a fresh credential + run ONE real claude agent for <paramref name="taskFactory"/>, and return whether <paramref name="marker"/> reached the model's OWN reply, ALONG WITH that reply text (for a diagnostic snippet on a miss). A run that did not COMPLETE is gateway/exec infra (an <see cref="AgentExecutionInfraException"/> → the gate's non-gating skip), NEVER a false behavior miss.</summary>
    private async Task<(bool Found, string ModelReply)> RunAndCheckMarkerAsync(LiveContext live, Func<Guid, AgentTask> taskFactory, string marker)
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
            throw new AgentExecutionInfraException($"the claude run did not complete (status={run.Status}; error={run.Error ?? "(none)"}) — gateway/exec infra, not a behavior verdict");

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

        return (modelReply.Contains(marker, StringComparison.Ordinal), modelReply);
    }

    /// <summary>
    /// Report-only skill drive with INFRA-RETRY. The blessed-wire best-of-N (used by the persona GATE) already retries
    /// past a flaky gateway; the report-only path (<see cref="RealModelGate.AssessLiveAsync(string, Func{Task{ValueTuple{bool, string}}}, bool)"/>)
    /// does NOT, so a single skill attempt that hits an intermittent gateway/exec failure would report an infra skip and
    /// tell us nothing about skill usage. This retries up to <paramref name="attempts"/> times to reach a run that
    /// actually COMPLETED, and reports whether the live model USED the skill. A COMPLETED-but-did-not-use verdict is a
    /// real (reported) signal, so it stops retrying only on the strongest outcome (USED); only if EVERY attempt failed
    /// to complete does it surface as an infra skip — carrying each attempt's real error (status + claude error text).
    /// </summary>
    private async Task<(bool Used, string Verdict)> DriveSkillReportAsync(LiveContext live, Func<Guid, AgentTask> taskFactory, string marker, int attempts)
    {
        (bool Used, string Verdict)? lastCompleted = null;
        var infra = new List<string>();

        for (var i = 0; i < attempts; i++)
        {
            try
            {
                var (used, reply) = await RunAndCheckMarkerAsync(live, taskFactory, marker);

                // On a miss, carry a bounded snippet of the model's ACTUAL reply — it disambiguates a load gap ("I don't
                // have a codeword") from a trigger gap (a greeting that simply omitted it). The codeword is a random
                // per-run token, not a secret, so echoing the reply leaks nothing.
                var detail = used ? "" : $" — model said: \"{Snippet(reply)}\"";
                var verdict = $"{Provider} '{live.Model}': the real claude agent {(used ? "LOADED + USED" : "did NOT use")} the bound skill (skill-only codeword {(used ? "present" : "absent")} in the model's greeting){detail}. [report-only — skill LOADING is gated deterministically by RealHarnessSkillProjectionTests; live auto-invocation is a reported capability]";
                Console.WriteLine($"[injection-e2e] skill: {verdict}");   // ALSO to stdout — the step-summary sink isn't greppable from the CLI/API

                if (used) return (true, verdict);   // strongest outcome — the model read + used the skill; stop

                lastCompleted = (false, verdict);   // completed but didn't trigger — remember, give it another shot
            }
            catch (AgentExecutionInfraException ex) { infra.Add(ex.Message); }   // flaky gateway/exec — retry past it
        }

        if (lastCompleted is { } completed) return completed;   // at least one run reached a real live verdict

        throw new AgentExecutionInfraException($"all {attempts} skill attempts failed to complete (gateway/exec infra), so live skill usage was never observed: {string.Join(" || ", infra)}");
    }

    /// <summary>A bounded, single-line snippet of a model reply for a diagnostic verdict — collapses newlines and caps length.</summary>
    private static string Snippet(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(empty reply)";

        var oneLine = text.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= 200 ? oneLine : oneLine[..200] + "…";
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
