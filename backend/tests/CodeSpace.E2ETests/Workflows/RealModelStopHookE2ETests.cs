using System.Diagnostics;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
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
/// Live stop-hook measurement for the real <c>claude</c> CLI, authenticated by a seeded encrypted
/// <see cref="ModelCredential"/>. The goal does not ask for a file, while the acceptance command requires it.
/// The arm reports whether the file exists after successful execution; it does not independently attest that
/// <c>settings.json</c>'s hook fired or caused the model to create it.
/// <para>Model behavior remains report-only (<c>gating: false</c>). A normally returning assessment must carry
/// persisted native CLI session evidence and positive model output usage. Known provider/wire failures and
/// absent credentials/CLI retain the existing explicit skip policy; skipped or informational misses are never
/// qualification success. The control-plane acceptance check still runs independently.</para>
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

    [SkippableFact]
    public async Task A_real_claude_agent_reacts_to_the_stop_hooks_feedback_and_creates_the_missing_file()
    {
        using var evidence = new StopHookExecutionEvidence("claude");
        if (await EnsureLiveOrSkipAsync() is not { } live) return;   // skip ≠ pass (surfaced loudly)

        // Model behavior remains report-only. The instrument must still prove that its real CLI/model ran;
        // admission/startup faults cannot masquerade as successful measurement through the soft gate.
        await evidence.AssessAsync(() => DriveOnceAsync(live, evidence));
    }

    // ─── shared drive ──────────────────────────────────────────────────────────

    /// <summary>Run one real CLI arm and retain execution evidence before applying the existing behavior and infrastructure classifications.</summary>
    private async Task<(bool Created, string Verdict)> DriveOnceAsync(LiveContext live, StopHookExecutionEvidence evidence)
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
        using (var scope = _fixture.BeginScopeAs(live.UserId, live.TeamId))
            runId = (await scope.Resolve<IAgentRunService>().CreateAsync(task, live.TeamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
        evidence.Admitted(runId);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunExecutor>().ExecuteAsync(runId, CancellationToken.None);

        using var read = _fixture.BeginScope();
        var run = await read.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
        evidence.Capture(run, await read.Resolve<IAgentRunService>().GetEventsAsync(runId, live.TeamId, 0, CancellationToken.None));

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

    private readonly record struct LiveContext(Guid TeamId, Guid UserId, string BaseUrl, string ApiKey, string Model);

    /// <summary>Resolve the live-model preconditions (creds + a real claude CLI + a seeded team) or self-skip LOUDLY (skip ≠ pass). Returns null when the run cannot go live.</summary>
    private async Task<LiveContext?> EnsureLiveOrSkipAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => !string.IsNullOrWhiteSpace(v));
        if (present == 0) throw RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)");
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three (base url / api key / model id) or none; a partial config would otherwise self-skip green proving nothing.");

        if (OperatingSystem.IsWindows()) throw RealModelGate.ReportSkipped(Provider, "the stop-hook arm requires a POSIX runtime");
        Environment.GetEnvironmentVariable(ClaudeCodeHarness.CommandEnvVar).ShouldBeNullOrEmpty("a real stop-hook measurement cannot use a command override or fake CLI");
        if (!await ClaudeReadyAsync()) throw RealModelGate.ReportSkipped(Provider, "the `claude` coding-agent CLI is not installed — the in-loop verify E2E needs the harness binary (skip ≠ pass)");

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture, inProcessPool: false);
        return new LiveContext(teamId, userId, baseUrl!.TrimEnd('/'), apiKey!, model!);
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
