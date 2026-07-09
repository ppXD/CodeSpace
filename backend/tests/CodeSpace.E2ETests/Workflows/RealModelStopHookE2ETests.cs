using System.Diagnostics;
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
/// THE live behavioral proof of P3.3's in-loop verify (the harness-native Stop hook, the Claude Code half): a REAL
/// <c>claude</c> CLI, authenticated by a seeded encrypted <see cref="ModelCredential"/>, is given a goal that does
/// NOT mention creating a file, but carries an <see cref="AgentTask.Acceptance"/> command that only passes once a
/// specific file exists. The model's natural first stop attempt is GUARANTEED to fail that check (nothing in the
/// goal asked for the file), so the injected Stop hook is GUARANTEED to fire at least once — this is the live proof
/// that the settings.json this arc generates actually gets read + invoked by the real binary, not just a shape the
/// harness's own unit tests assert about a string. If in-loop verify does its job, the model reads the hook's
/// block reason (which carries the check's OWN failure output, not a generic notice) and creates the file before
/// its FINAL stop.
///
/// <para><b>Gate policy:</b> the run REACHING a terminal outcome without a gateway/wire fault is the deterministic
/// half — a malformed settings.json would either crash the CLI outright or leave it permanently blocked, and either
/// shows up as a non-Succeeded, non-gateway-infra status, which this test treats as a REAL miss (never a silent
/// skip), mirroring <see cref="RealModelAgentInjectionE2ETests"/>'s exact classification. Whether the LIVE MODEL
/// actually acts on the feedback is a capability-dependent behavior — reported (<c>gating: false</c>), never
/// blocking main, the same report-only posture the skill-usage test uses for the same reason. A no-creds / no-CLI
/// run self-skips LOUDLY (skip ≠ pass). POSIX-only. <c>[Category=RealModel]</c> so it runs ONLY on the real-model
/// lane.</para>
///
/// <para><b>This does NOT prove the control plane is bypassed — the opposite.</b> The task's <c>Acceptance</c>
/// contract is the SAME field the control-plane grader independently re-verifies after the run settles; nothing
/// here short-circuits that. This test only proves the IN-LOOP half fires and can help; <c>InLoopAcceptanceHook</c>'s
/// own doc comment states the invariant, and a grep-level check (no code path from the hook into
/// <c>AcceptancePassed</c>) is the structural proof that the control plane's verdict is untouched.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
[Trait("Surface", "Engine")]
public sealed class RealModelStopHookE2ETests : IDisposable
{
    private const string Provider = "Anthropic";

    private readonly PostgresFixture _fixture;
    private readonly List<string> _tempDirs = new();

    public RealModelStopHookE2ETests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_real_claude_agent_reacts_to_the_stop_hooks_feedback_and_creates_the_missing_file()
    {
        if (await EnsureLiveOrSkipAsync() is not { } live) return;   // skip ≠ pass (surfaced loudly)

        // REPORT-ONLY (gating: false), the same posture the skill-usage E2E uses: whether the LIVE MODEL reacts to
        // the hook's feedback is a capability-dependent behavior, not a deterministic wiring guarantee — never blocks
        // main. A wiring-level regression (a malformed settings.json breaking the CLI outright) would surface as a
        // repeated non-completing, non-gateway-infra verdict across runs — reviewed manually against the real log,
        // the same process this arc uses for every RealModel check, rather than a hard in-test assertion here.
        await RealModelGate.AssessLiveAsync(Provider, () => DriveOnceAsync(live), gating: false);
    }

    // ─── shared drive ──────────────────────────────────────────────────────────

    /// <summary>Seed a fresh credential + workspace and run ONE real claude agent whose acceptance check only passes once a file the goal never mentions exists — the natural first stop attempt is guaranteed to fail it, guaranteeing the Stop hook fires. Returns whether the file was created by the time the run settled, plus a diagnostic verdict. A run that did not COMPLETE is gateway/exec infra (an <see cref="AgentExecutionInfraException"/> → the gate's non-gating skip), never a false miss.</summary>
    private async Task<(bool Created, string Verdict)> DriveOnceAsync(LiveContext live)
    {
        var credId = await SeedAgentCredentialAsync(live.TeamId, live.BaseUrl, live.ApiKey);
        var workspace = NewGitWorkspace();

        var task = new AgentTask
        {
            Goal = "Reply with the single word done, then stop. Do not create, edit, or read any files unless the situation genuinely requires it.",
            Harness = "claude-code",
            Model = live.Model,
            ModelCredentialId = credId,
            WorkspaceDirectory = workspace,
            Autonomy = AgentAutonomyLevel.Trusted,
            Permissions = AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Trusted),
            // The goal never mentions this file — the model's natural first stop attempt is guaranteed to fail this
            // check, guaranteeing the Stop hook fires at least once. The check's OWN stderr is what the hook surfaces
            // back as the block reason (InLoopAcceptanceHook's actionable-output feature), not a generic notice.
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
                throw new AgentExecutionInfraException($"the claude run did not complete — gateway/exec infra (non-gating skip): {reason}");

            return (false, $"{Provider} '{live.Model}': the run did NOT complete and is not classified as gateway infra — worth reviewing the real log for a Stop-hook wiring regression: {reason}");
        }

        var created = File.Exists(Path.Combine(workspace, "STOPHOOK-PROOF.txt"));
        var verdict = $"{Provider} '{live.Model}': the run completed; STOPHOOK-PROOF.txt was {(created ? "CREATED — the model reacted to the Stop hook's in-loop feedback" : "still missing — the model did not act on the hook's feedback in this attempt (report-only, model-capability-dependent)")}.";

        return (created, verdict);
    }

    // ─── gate + seeding ────────────────────────────────────────────────────────

    private readonly record struct LiveContext(Guid TeamId, string BaseUrl, string ApiKey, string Model);

    /// <summary>Resolve the live-model preconditions (creds + a real claude CLI + a seeded team) or self-skip LOUDLY (skip ≠ pass). Returns null when the run cannot go live.</summary>
    private async Task<LiveContext?> EnsureLiveOrSkipAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => !string.IsNullOrWhiteSpace(v));
        if (present == 0) { RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)"); return null; }
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three (base url / api key / model id) or none; a partial config would otherwise self-skip green proving nothing.");

        if (OperatingSystem.IsWindows()) return null;
        if (!await ClaudeReadyAsync()) { RealModelGate.ReportSkipped(Provider, "the `claude` coding-agent CLI is not installed — the in-loop verify E2E needs the harness binary (skip ≠ pass)"); return null; }

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
            Id = credId, TeamId = teamId, Provider = Provider, DisplayName = "stop hook e2e agent cred",
            EncryptedApiKey = encryptor.Encrypt(apiKey), BaseUrl = baseUrl, Status = CredentialStatus.Active,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return credId;
    }

    /// <summary>A fresh git-initialised temp workspace, mirroring the injection E2E's own helper. Tracked for teardown.</summary>
    private string NewGitWorkspace()
    {
        var ws = Path.Combine(Path.GetTempPath(), "cs-claude-stophook-" + Guid.NewGuid().ToString("N"), "ws");
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

    private static async Task<bool> ClaudeReadyAsync()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "claude", Args = new[] { "--version" }, TimeoutSeconds = 15 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup of the gated run's temp workspaces */ }
    }
}
