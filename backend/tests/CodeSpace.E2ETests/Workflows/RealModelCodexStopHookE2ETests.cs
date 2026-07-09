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
/// THE live behavioral proof of P3.3's in-loop verify — the CODEX half, the exact counterpart to
/// <see cref="RealModelStopHookE2ETests"/> (Claude). A REAL <c>codex</c> CLI, authenticated by a seeded encrypted
/// <see cref="ModelCredential"/>, is given a goal that does NOT mention creating a file, but carries an
/// <see cref="AgentTask.Acceptance"/> command that only passes once a specific file exists — the model's natural
/// first stop attempt is GUARANTEED to fail that check, guaranteeing the injected <c>hooks.json</c> Stop hook fires.
/// If in-loop verify does its job, the model reads the block reason (the check's own failure output, via
/// <c>InLoopAcceptanceHook</c>'s actionable-output feature) and creates the file before its FINAL stop.
///
/// <para><b>Why REPORT-ONLY, not a hard gate:</b> same wire mismatch as
/// <see cref="RealModelCodexInjectionE2ETests"/> — Codex talks the OpenAI <c>responses</c> wire, which the shared
/// gateway may not serve, so a live Codex run may not COMPLETE at all; that is a wire/infra fact, never a behavior
/// miss. <see cref="RealModelGate.AssessLiveAsync(string, System.Func{System.Threading.Tasks.Task{System.ValueTuple{bool, string}}}, bool)"/>
/// runs it and REPORTS (<c>gating: false</c>), the same posture <see cref="RealModelStopHookE2ETests"/> uses for the
/// SAME reason (whether a live model reacts to feedback is capability-dependent, not deterministic). A no-creds /
/// no-CLI run self-skips LOUDLY (skip ≠ pass). POSIX-only. <c>[Category=RealModel]</c> so it runs ONLY on the
/// real-model lane.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
[Trait("Surface", "Engine")]
public sealed class RealModelCodexStopHookE2ETests : IDisposable
{
    // Codex routes through the OpenAI-family wire (see CodexHarness.SupportedProviders) — same informational-wire
    // tagging as RealModelCodexInjectionE2ETests, so a gateway that can't serve Codex's `responses` protocol never reds.
    private const string Provider = "OpenAI";

    private readonly PostgresFixture _fixture;
    private readonly List<string> _tempDirs = new();

    public RealModelCodexStopHookE2ETests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_real_codex_agent_reacts_to_the_stop_hooks_feedback_and_creates_the_missing_file()
    {
        if (await EnsureLiveOrSkipAsync() is not { } live) return;   // skip ≠ pass (surfaced loudly)

        await RealModelGate.AssessLiveAsync(Provider, () => DriveOnceAsync(live), gating: false);
    }

    // ─── shared drive ──────────────────────────────────────────────────────────

    /// <summary>Seed a fresh credential + workspace and run ONE real codex agent whose acceptance check only passes once a file the goal never mentions exists. Returns whether the file was created by the time the run settled, plus a diagnostic verdict. A run that did not COMPLETE is gateway/exec/wire infra (an <see cref="AgentExecutionInfraException"/> → the gate's non-gating skip), never a false miss.</summary>
    private async Task<(bool Created, string Verdict)> DriveOnceAsync(LiveContext live)
    {
        var credId = await SeedAgentCredentialAsync(live.TeamId, live.BaseUrl, live.ApiKey);
        var workspace = NewGitWorkspace();

        var task = new AgentTask
        {
            Goal = "Reply with the single word done, then stop. Do not create, edit, or read any files unless the situation genuinely requires it.",
            Harness = CodexHarness.HarnessKind,
            Model = live.Model,
            ModelCredentialId = credId,
            WorkspaceDirectory = workspace,
            Autonomy = AgentAutonomyLevel.Trusted,
            Permissions = AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Trusted),
            // The goal never mentions this file — the model's natural first stop attempt is guaranteed to fail this
            // check, guaranteeing the Stop hook fires. --dangerously-bypass-hook-trust (CodexHarness) lets the
            // freshly generated per-run hooks.json actually run in this non-interactive exec context.
            Acceptance = new SupervisorAcceptanceSpec
            {
                Command = new[] { "sh", "-c", "test -f STOPHOOK-PROOF.txt || { echo 'STOPHOOK-PROOF.txt is missing from the repo root — create it (any content) before stopping' >&2; exit 1; }" },
            },
            TimeoutSeconds = 240,
        };

        Guid runId;
        using (var scope = _fixture.BeginScope())
            runId = (await scope.Resolve<IAgentRunService>().CreateAsync(task, live.TeamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunExecutor>().ExecuteAsync(runId, CancellationToken.None);

        using var read = _fixture.BeginScope();
        var run = await read.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);

        if (run.Status != AgentRunStatus.Succeeded)
        {
            var reason = $"status={run.Status}; exitReason={RealModelRunClassifier.ExitReasonOf(run)}; error={run.Error ?? "(none)"}";

            if (RealModelRunClassifier.IsGatewayInfra(run))
                throw new AgentExecutionInfraException($"the codex run did not complete — gateway/exec/wire infra (non-gating skip): {reason}");

            return (false, $"{Provider} '{live.Model}': the run did NOT complete and is not classified as gateway infra — worth reviewing the real log for a Stop-hook wiring regression: {reason}");
        }

        var created = File.Exists(Path.Combine(workspace, "STOPHOOK-PROOF.txt"));
        var verdict = $"{Provider} '{live.Model}': the run completed; STOPHOOK-PROOF.txt was {(created ? "CREATED — the model reacted to the Stop hook's in-loop feedback" : "still missing — the model did not act on the hook's feedback in this attempt (report-only, model-capability-dependent)")}.";

        return (created, verdict);
    }

    // ─── gate + seeding ────────────────────────────────────────────────────────

    private readonly record struct LiveContext(Guid TeamId, string BaseUrl, string ApiKey, string Model);

    private async Task<LiveContext?> EnsureLiveOrSkipAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => !string.IsNullOrWhiteSpace(v));
        if (present == 0) { RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)"); return null; }
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three (base url / api key / model id) or none; a partial config would otherwise self-skip green proving nothing.");

        if (OperatingSystem.IsWindows()) return null;
        if (!await CodexReadyAsync()) { RealModelGate.ReportSkipped(Provider, "the `codex` coding-agent CLI is not installed — the in-loop verify E2E needs the harness binary (skip ≠ pass)"); return null; }

        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        return new LiveContext(teamId, baseUrl!.TrimEnd('/'), apiKey!, model!);
    }

    private async Task<Guid> SeedAgentCredentialAsync(Guid teamId, string baseUrl, string apiKey)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();

        var credId = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = credId, TeamId = teamId, Provider = Provider, DisplayName = "codex stop hook e2e agent cred",
            EncryptedApiKey = encryptor.Encrypt(apiKey), BaseUrl = baseUrl, Status = CredentialStatus.Active,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return credId;
    }

    /// <summary>A fresh git-initialised temp workspace — <c>codex exec</c> refuses to run outside a trusted git repo (mirrors <see cref="RealModelCodexInjectionE2ETests"/>'s own helper). Tracked for teardown.</summary>
    private string NewGitWorkspace()
    {
        var ws = Path.Combine(Path.GetTempPath(), "cs-codex-stophook-" + Guid.NewGuid().ToString("N"), "ws");
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
