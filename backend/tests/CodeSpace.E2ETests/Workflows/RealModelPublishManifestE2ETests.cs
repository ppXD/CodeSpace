using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// HIGH fidelity (Rule 12): the live behavioral proof of the publish-manifest arc's I1/I2/I3 invariants PLUS PR-2's
/// publish guard chain — a REAL <c>claude</c> CLI, driven by a REAL model, edits a REAL git clone; the branch is
/// force-pushed over REAL git to a REAL local bare repo (no network — <c>file://</c>, mirroring
/// <see cref="AgentBranchPushFlowTests.BareRemote"/>); and a <see cref="PublishManifest"/> row lands with the pushed
/// branch + the changed file, independently verifiable against the bare remote's own refs. Nothing on the publish
/// path is mocked: the model, the harness process, git clone/commit/push, Postgres, and the artifact store are all
/// real production components.
///
/// <para><b>Why a bare local repo, not a mocked push:</b> <see cref="IWorkspacePushHandle.PushChangesAsync(string,System.Threading.CancellationToken)"/>
/// needs a real remote to force-push against; a <c>file://</c> bare repo gives REAL git push/clone semantics (a
/// genuine transport, genuine ref update) with zero network dependency and zero flakiness — the same trick
/// <see cref="AgentBranchPushFlowTests"/> already uses for the (non-real-model) push-flow gate.</para>
///
/// <para><b>Gate policy:</b> file-creation-then-push is near-deterministic (no progressive-disclosure judgement call
/// like a skill), so this GATES the blessed Anthropic wire via <see cref="RealModelGate.AssessLiveBestOfNAsync(string,System.Func{System.Threading.Tasks.Task{System.ValueTuple{bool,string}}},int?)"/> —
/// a persistent failure to record + push REDs main. A non-Succeeded run is classified exactly like the injection
/// E2Es: a GATEWAY/transport/auth failure is a non-gating LOUD skip; any other fault is a REAL miss the gate REDS on
/// (a regression in capture/offload/push/manifest-write, never silently absorbed). A no-creds / no-CLI / no-git run
/// self-skips LOUDLY (skip ≠ pass). POSIX-only. <c>[Category=RealModel]</c> so it runs ONLY on the real-model lane.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
[Trait("Surface", "Engine")]
public sealed class RealModelPublishManifestE2ETests
{
    private const string Provider = "Anthropic";

    private readonly PostgresFixture _fixture;

    public RealModelPublishManifestE2ETests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_real_agent_runs_diff_is_captured_pushed_and_recorded_in_the_publish_manifest_with_no_opt_in()
    {
        if (await EnsureLiveOrSkipAsync() is not { } live) return;   // skip ≠ pass (surfaced loudly)

        await RealModelGate.AssessLiveBestOfNAsync(Provider, async () =>
        {
            var token = "PUBLISHOK" + Guid.NewGuid().ToString("N")[..8];
            var fileName = $"agent-proof-{token}.txt";

            using var remote = new BareRemote();
            await remote.SeedWithOneCommitAsync();

            var teamId = live.TeamId;
            var repositoryId = await SeedBoundRepositoryAsync(teamId, remote.Url, defaultBranch: "main", publishMode: RepositoryPublishMode.Branch);
            var credId = await SeedAgentCredentialAsync(teamId, live.BaseUrl, live.ApiKey);

            var task = new AgentTask
            {
                Goal = $"Create a new file at the repository root named exactly '{fileName}' containing the single line 'published'. Do not modify or create any other file.",
                Harness = "claude-code",
                Model = live.Model,
                ModelCredentialId = credId,
                RepositoryId = repositoryId,
                Autonomy = AgentAutonomyLevel.Trusted,
                Permissions = AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Trusted),
                // NO PushProducedBranch set at all — PR-2's default flip: push is default-ON for a non-empty diff,
                // no per-run opt-in needed. This is the load-bearing proof that the flip works end to end with a
                // REAL model + REAL git, not just the fake-harness integration tests.
                TimeoutSeconds = 180,
            };

            Guid runId;
            using (var scope = _fixture.BeginScope())
                runId = (await scope.Resolve<IAgentRunService>().CreateAsync(task, teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;

            using (var scope = _fixture.BeginScope())
                await scope.Resolve<IAgentRunExecutor>().ExecuteAsync(runId, CancellationToken.None);

            using var read = _fixture.BeginScope();
            var run = await read.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);

            if (run.Status != AgentRunStatus.Succeeded)
            {
                var reason = $"status={run.Status}; exitReason={RealModelRunClassifier.ExitReasonOf(run)}; error={run.Error ?? "(none)"}";

                if (RealModelRunClassifier.IsGatewayInfra(run))
                    throw new AgentExecutionInfraException($"the claude run did not complete — gateway/exec infra (non-gating skip): {reason}");

                return (false, $"{Provider} '{live.Model}': the real claude agent's run did NOT complete — likely a publish-path regression, not gateway infra: {reason}");
            }

            var expectedBranch = AgentRunExecutor.BuildBranchName(runId);

            var manifest = (await read.Resolve<IPublishManifestStore>().ListForAgentRunAsync(runId, teamId, CancellationToken.None)).FirstOrDefault();

            if (manifest is null)
                return (false, $"{Provider} '{live.Model}': the run Succeeded but NO PublishManifest row was recorded — I1 (record) violated");

            if (manifest.PublishStateValue != PublishState.Pushed || manifest.Branch != expectedBranch)
                return (false, $"{Provider} '{live.Model}': the manifest row did not resolve to Pushed/{expectedBranch} (state={manifest.PublishStateValue}, branch={manifest.Branch ?? "(none)"}, error={manifest.PublishError ?? "(none)"})");

            var recordedFiles = manifest.ChangedFilesJson is { Length: > 0 } json ? JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>() : Array.Empty<string>();

            if (!recordedFiles.Contains(fileName))
                return (false, $"{Provider} '{live.Model}': the manifest's changed-files list [{string.Join(", ", recordedFiles)}] does not contain the expected '{fileName}'");

            // The independent, ground-truth check: the bare remote (real git, not our own DB row) actually carries the
            // pushed branch AND the file — proving the manifest's claim matches reality, not just our own bookkeeping.
            if (!await remote.HasBranchAsync(expectedBranch))
                return (false, $"{Provider} '{live.Model}': the manifest claims branch '{expectedBranch}' but the bare remote has no such branch — I2 (reference) is lying");

            if (!await remote.BranchContainsFileAsync(expectedBranch, fileName))
                return (false, $"{Provider} '{live.Model}': the pushed branch '{expectedBranch}' exists but does not contain '{fileName}'");

            var verdict = $"{Provider} '{live.Model}': the real agent's diff was captured, pushed to '{expectedBranch}', and correctly recorded in the publish manifest (independently confirmed against the real git remote)";
            Console.WriteLine($"[publish-manifest-e2e] {verdict}");
            return (true, verdict);
        });
    }

    [Fact]
    public async Task A_repository_policy_of_patch_only_blocks_a_real_agents_push()
    {
        if (await EnsureLiveOrSkipAsync() is not { } live) return;   // skip ≠ pass (surfaced loudly)

        await RealModelGate.AssessLiveBestOfNAsync(Provider, async () =>
        {
            var token = "PATCHONLY" + Guid.NewGuid().ToString("N")[..8];
            var fileName = $"agent-proof-{token}.txt";

            using var remote = new BareRemote();
            await remote.SeedWithOneCommitAsync();

            var teamId = live.TeamId;
            var repositoryId = await SeedBoundRepositoryAsync(teamId, remote.Url, defaultBranch: "main", publishMode: RepositoryPublishMode.PatchOnly);
            var credId = await SeedAgentCredentialAsync(teamId, live.BaseUrl, live.ApiKey);

            var task = new AgentTask
            {
                Goal = $"Create a new file at the repository root named exactly '{fileName}' containing the single line 'published'. Do not modify or create any other file.",
                Harness = "claude-code",
                Model = live.Model,
                ModelCredentialId = credId,
                RepositoryId = repositoryId,
                Autonomy = AgentAutonomyLevel.Trusted,
                Permissions = AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Trusted),
                TimeoutSeconds = 180,
            };

            Guid runId;
            using (var scope = _fixture.BeginScope())
                runId = (await scope.Resolve<IAgentRunService>().CreateAsync(task, teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;

            using (var scope = _fixture.BeginScope())
                await scope.Resolve<IAgentRunExecutor>().ExecuteAsync(runId, CancellationToken.None);

            using var read = _fixture.BeginScope();
            var run = await read.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);

            if (run.Status != AgentRunStatus.Succeeded)
            {
                var reason = $"status={run.Status}; exitReason={RealModelRunClassifier.ExitReasonOf(run)}; error={run.Error ?? "(none)"}";

                if (RealModelRunClassifier.IsGatewayInfra(run))
                    throw new AgentExecutionInfraException($"the claude run did not complete — gateway/exec infra (non-gating skip): {reason}");

                return (false, $"{Provider} '{live.Model}': the real claude agent's run did NOT complete — likely a publish-guard regression, not gateway infra: {reason}");
            }

            var blockedBranch = AgentRunExecutor.BuildBranchName(runId);

            var manifest = (await read.Resolve<IPublishManifestStore>().ListForAgentRunAsync(runId, teamId, CancellationToken.None)).FirstOrDefault();

            if (manifest is null)
                return (false, $"{Provider} '{live.Model}': the run Succeeded but NO PublishManifest row was recorded — I1 (record) violated even under a policy skip");

            if (manifest.PublishStateValue != PublishState.PatchOnly || manifest.Branch is not null)
                return (false, $"{Provider} '{live.Model}': the manifest row did not resolve to PatchOnly/no-branch under the repo policy (state={manifest.PublishStateValue}, branch={manifest.Branch ?? "(none)"})");

            if (manifest.Summary != "the repository requires patch-only publishing")
                return (false, $"{Provider} '{live.Model}': the manifest's Summary did not carry the guard's reason (was: {manifest.Summary ?? "(none)"})");

            // The independent, ground-truth check: the real diff was still captured (I1 holds) — the manifest names
            // the file even though nothing was pushed.
            var recordedFiles = manifest.ChangedFilesJson is { Length: > 0 } json ? JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>() : Array.Empty<string>();

            if (!recordedFiles.Contains(fileName))
                return (false, $"{Provider} '{live.Model}': the manifest's changed-files list [{string.Join(", ", recordedFiles)}] does not contain the expected '{fileName}' despite the policy skip — I1 requires capture regardless");

            // And the bare remote — real git ground truth — must NOT carry the blocked branch.
            if (await remote.HasBranchAsync(blockedBranch))
                return (false, $"{Provider} '{live.Model}': the repository policy is PatchOnly but the branch '{blockedBranch}' appeared on the remote anyway — the guard did not actually block the push");

            var verdict = $"{Provider} '{live.Model}': the real agent's diff was captured and recorded (I1) but correctly withheld from the remote under the repository's patch-only policy, independently confirmed absent from the real git remote";
            Console.WriteLine($"[publish-manifest-e2e] {verdict}");
            return (true, verdict);
        });
    }

    // ─── gate + seeding ────────────────────────────────────────────────────────

    private readonly record struct LiveContext(Guid TeamId, string BaseUrl, string ApiKey, string Model);

    /// <summary>Resolve the live-model + git preconditions or self-skip LOUDLY (skip ≠ pass). Returns null when the run cannot go live.</summary>
    private async Task<LiveContext?> EnsureLiveOrSkipAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => !string.IsNullOrWhiteSpace(v));
        if (present == 0) { RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)"); return null; }   // skip ≠ pass
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three (base url / api key / model id) or none; a partial config would otherwise self-skip green proving nothing.");

        if (OperatingSystem.IsWindows()) return null;   // the harness + sandbox are /bin/sh based
        if (!await ClaudeReadyAsync()) { RealModelGate.ReportSkipped(Provider, "the `claude` coding-agent CLI is not installed (skip ≠ pass)"); return null; }
        if (!await GitAvailableAsync()) { RealModelGate.ReportSkipped(Provider, "git is not installed (skip ≠ pass)"); return null; }

        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        return new LiveContext(teamId, baseUrl!.TrimEnd('/'), apiKey!, model!);
    }

    /// <summary>Seed an encrypted gateway <see cref="ModelCredential"/> the executor resolves via <c>ModelCredentialId</c>.</summary>
    private async Task<Guid> SeedAgentCredentialAsync(Guid teamId, string baseUrl, string apiKey)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();

        var credId = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = credId, TeamId = teamId, Provider = Provider, DisplayName = "publish-manifest e2e agent cred",
            EncryptedApiKey = encryptor.Encrypt(apiKey), BaseUrl = baseUrl, Status = CredentialStatus.Active,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return credId;
    }

    /// <summary>Seed a Repository bound to a PAT Credential so the clone carries a push token (an anonymous clone short-circuits the push to null by design) — a file:// remote ignores the token itself, but LocalGitWorkspaceProvider still requires ONE to take the authenticated push path. Mirrors <see cref="AgentBranchPushFlowTests.SeedBoundRepositoryAsync"/>.</summary>
    private async Task<Guid> SeedBoundRepositoryAsync(Guid teamId, string cloneUrl, string defaultBranch, RepositoryPublishMode publishMode = RepositoryPublishMode.Branch)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "local", BaseUrl = "https://local" });

        var serializer = scope.Resolve<ICredentialPayloadSerializer>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var payloadJson = serializer.Serialize(new PatPayload { Token = "publish-manifest-e2e-token" });

        var credentialId = Guid.NewGuid();
        db.Credential.Add(new Credential
        {
            Id = credentialId, TeamId = teamId, ProviderInstanceId = instanceId,
            AuthType = AuthType.Pat, DisplayName = "clone cred",
            EncryptedPayload = encryptor.Encrypt(payloadJson), Status = CredentialStatus.Active,
        });

        var repoId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId,
            ExternalId = repoId.ToString(), NamespacePath = "org", Name = "repo", FullPath = "org/repo",
            DefaultBranch = defaultBranch, CloneUrlHttps = cloneUrl, WebUrl = "https://local/org/repo",
            PublishMode = publishMode,
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    private static async Task<bool> ClaudeReadyAsync()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "claude", Args = new[] { "--version" }, TimeoutSeconds = 15 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    private static async Task<bool> GitAvailableAsync()
    {
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>A bare local repo standing in for the agent's remote, plus ref inspection via <c>git --git-dir</c> — real git ground truth. GUID-suffixed; IDisposable best-effort cleans every dir even on the failure path. Mirrors <see cref="AgentBranchPushFlowTests.BareRemote"/>.</summary>
    private sealed class BareRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-publish-manifest-e2e-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;

        public BareRemote()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
        }

        public string Url => new Uri(_bare).AbsoluteUri;

        public async Task SeedWithOneCommitAsync()
        {
            await RunGitAsync(_root, "init", "--bare", "-b", "main", _bare);

            var seed = Path.Combine(_root, "seed");
            Directory.CreateDirectory(seed);
            await RunGitAsync(seed, "clone", _bare, seed);
            await RunGitAsync(seed, "config", "user.email", "test@codespace.dev");
            await RunGitAsync(seed, "config", "user.name", "Test");
            await RunGitAsync(seed, "config", "commit.gpgsign", "false");
            await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "base");
            await RunGitAsync(seed, "add", ".");
            await RunGitAsync(seed, "commit", "-m", "seed");
            await RunGitAsync(seed, "push", "origin", "main");
        }

        public async Task<bool> HasBranchAsync(string branch) =>
            (await RunGitAsync(_root, "--git-dir", _bare, "branch", "--list", branch)).Trim().Length > 0;

        public async Task<bool> BranchContainsFileAsync(string branch, string file) =>
            (await RunGitAsync(_root, "--git-dir", _bare, "ls-tree", "-r", "--name-only", branch)).Split('\n').Any(l => l.Trim() == file);

        private static async Task<string> RunGitAsync(string workdir, params string[] args)
        {
            var result = await new LocalProcessRunner().RunAsync(
                new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);

            if (result.Status != SandboxStatus.Success)
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");

            return result.Stdout;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
