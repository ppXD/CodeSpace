using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// 🟢 THE live-brain whole-loop gate (P0-b.2b) — the headline join: a REAL model drives the REAL durable engine end to
/// end. It is <see cref="SupervisorWholeLoopE2ETests"/> (the deterministic skeleton) with the scripted decider SWAPPED
/// for the production <see cref="CodeSpace.Core.Services.Supervisor.Deciders.LlmSupervisorDecider"/> (via the
/// <see cref="SupervisorDeciderMode"/> seam, P0-b.2a), the brain's credential resolved from a SEEDED, encrypted
/// <c>ModelCredential</c> row (the live decider reads its key from the DB, never in-process). The live model authors
/// plan → spawn → (inspect/retry) → merge → stop on its own; the spawned agents are REAL OS processes that EDIT REAL
/// FILES (<see cref="FileWritingFakeCli"/>), the executor captures REAL patches, the merge integrates them on a bare
/// <c>file://</c> remote, and the terminal stop is graded against a real <c>check.sh</c> acceptance floor — so a green
/// verdict means a LIVE brain really drove real agents to a verified, integrated, accepted patch.
///
/// <para>Honestly gated: self-skips when the <c>CODESPACE_LLM_*</c> secrets are absent (CI/forks stay green at zero
/// cost), and runs only in the dedicated Postgres+secrets real-model lane. The blessed wire (Anthropic) GATES via
/// <see cref="RealModelGate"/>; only the CLI's intelligence (the fake codex) and the agent COUNT are bounded — the
/// brain, the engine, the runner, the git, and the acceptance are all real.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
[Trait("Surface", "Engine")]
public sealed class RealModelSupervisorWholeLoopE2ETests : IDisposable
{
    private const string NodeId = "sup";
    private const string Provider = "Anthropic";   // the blessed brain wire (RealModelGate gates it)

    private readonly PostgresFixture _fixture;
    private readonly string? _laneBefore;
    private readonly string? _integrateBefore;

    public RealModelSupervisorWholeLoopE2ETests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _laneBefore = Environment.GetEnvironmentVariable(SupervisorLane.EnabledEnvVar);
        _integrateBefore = Environment.GetEnvironmentVariable(AgentRunExecutor.IntegrateBranchEnabledEnvVar);

        // The only THROWABLE mutation (the DI resolve that flips the decider) runs FIRST, so a ctor throw leaks no
        // process-global; the env-var sets (which cannot throw) follow. Dispose restores all three.
        SetDeciderMode(useLiveModel: true);
        Environment.SetEnvironmentVariable(SupervisorLane.EnabledEnvVar, "1");
        Environment.SetEnvironmentVariable(AgentRunExecutor.IntegrateBranchEnabledEnvVar, "1");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SupervisorLane.EnabledEnvVar, _laneBefore);
        Environment.SetEnvironmentVariable(AgentRunExecutor.IntegrateBranchEnabledEnvVar, _integrateBefore);

        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDeciderMode>().UseLiveModel = false;   // restore the shared-fixture default for siblings
        scope.Resolve<SupervisorDecisionScript>().PlanThenStop();
        scope.Resolve<InMemoryBackgroundJobClient>().AutoExecute = true;
    }

    [Fact]
    public async Task The_real_model_drives_the_whole_loop_to_an_integrated_accepted_patch()
    {
        var baseUrl = Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        if (baseUrl is null || apiKey is null || model is null) return;   // secrets absent → skip (honest CI/fork behaviour)
        if (OperatingSystem.IsWindows()) return;                          // the fake CLI is a /bin/sh script
        if (!await GitReadyAsync()) return;                              // real git is required for clone/capture/integrate

        using var cli = new FileWritingFakeCli();

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new() { ["check.sh"] = "#!/bin/sh\nexit 0\n", ["base.txt"] = "base\n" });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        // The supervisor's brain runs on this seeded credential (key encrypted into the DB row the live decider reads).
        var brainModelId = await SeedBrainModelAsync(teamId, BaseUrlFor(baseUrl), apiKey, model);

        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, repoId, brainModelId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();

        var (ok, note) = await EvaluateAsync(runId, teamId);
        RealModelGate.Assess(Provider, ok, $"{Provider} model '{model}' whole-loop — {note}");
    }

    // ─── Verdict ─────────────────────────────────────────────────────────────────────

    /// <summary>The live brain drove the whole loop soundly iff the run reached Success, at least one real agent produced a real patch, and the terminal stop's objective acceptance PASSED (a green check.sh against the integrated head). Returns a legible note for the gate.</summary>
    private async Task<(bool Ok, string Note)> EvaluateAsync(Guid runId, Guid teamId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        var kinds = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId)
            .OrderBy(d => d.Sequence).Select(d => d.DecisionKind).ToListAsync();

        var patches = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId && r.Status == AgentRunStatus.Succeeded)
            .Select(r => r.ResultJson).ToListAsync();
        var realPatchCount = patches.Count(j => j is not null
            && System.Text.Json.JsonSerializer.Deserialize<AgentRunResult>(j!, AgentJson.Options)?.Patch is { Length: > 0 });

        var stop = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Stop)
            .OrderByDescending(d => d.Sequence).FirstOrDefaultAsync();
        var acceptancePassed = stop is not null && SupervisorOutcome.ReadAcceptanceGradePassed(stop.OutcomeJson) == true;

        var trail = string.Join("→", kinds);
        var ok = run.Status == WorkflowRunStatus.Success && realPatchCount >= 1 && acceptancePassed;
        return (ok, $"status={run.Status}, realPatches={realPatchCount}, acceptancePassed={acceptancePassed}, trajectory={trail}");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>Anthropic's client appends <c>/v1/messages</c> to the host base — pass the gateway host as-is.</summary>
    private static string BaseUrlFor(string baseUrl) => baseUrl.TrimEnd('/');

    private void SetDeciderMode(bool useLiveModel)
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDeciderMode>().UseLiveModel = useLiveModel;
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    /// <summary>Seed a KEYED, structured-capable credentialed-model row for the supervisor brain (the live decider reads its key + base url from this row). Returns the row id → the supervisor's <c>supervisorModelId</c>.</summary>
    private async Task<Guid> SeedBrainModelAsync(Guid teamId, string baseUrl, string apiKey, string modelId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<CodeSpace.Core.Services.Credentials.IPayloadEncryptor>();

        var credId = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = credId, TeamId = teamId, Provider = Provider, DisplayName = "live brain cred",
            EncryptedApiKey = encryptor.Encrypt(apiKey), BaseUrl = baseUrl, Status = CredentialStatus.Active,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        var rowId = Guid.NewGuid();
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = rowId, ModelCredentialId = credId, ModelId = modelId, Source = ModelSource.Manual, SupportsStructuredOutput = true, Enabled = true });

        await db.SaveChangesAsync();
        return rowId;
    }

    private async Task<Guid> CreateWholeLoopWorkflowAsync(Guid teamId, Guid userId, Guid repoId, Guid brainModelId)
    {
        // The live brain (supervisorModelId) authors the arc; its agents clone repoId + push branches, the merge
        // integrates them, and the operator acceptance floor (check.sh) gates the terminal stop. maxRounds caps a
        // runaway live brain so the in-memory drain always terminates.
        var supConfig = $$"""
            {
              "goal": "Add server-side email-format validation to the signup endpoint, with unit tests.",
              "supervisorModelId": "{{brainModelId}}",
              "maxRounds": 10,
              "agentProfile": { "repositoryId": "{{repoId}}", "pushBranch": true, "integrateBranches": true },
              "acceptanceChecks": ["sh", "check.sh"]
            }
            """;

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "sup-livewholeloop-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = new WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = NodeId, TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json(supConfig), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                },
                Edges = new List<EdgeDefinition>
                {
                    new() { From = "start", To = NodeId },
                    new() { From = NodeId, To = "end" },
                },
            },
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task<Guid> SeedBoundRepositoryAsync(Guid teamId, string cloneUrlHttps, string defaultBranch)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "local", BaseUrl = $"https://local/{instanceId:N}" });

        var serializer = scope.Resolve<CodeSpace.Core.Services.Credentials.ICredentialPayloadSerializer>();
        var encryptor = scope.Resolve<CodeSpace.Core.Services.Credentials.IPayloadEncryptor>();
        var payloadJson = serializer.Serialize(new PatPayload { Token = "integration-token" });

        var credentialId = Guid.NewGuid();
        db.Credential.Add(new Credential
        {
            Id = credentialId, TeamId = teamId, ProviderInstanceId = instanceId, AuthType = AuthType.Pat, DisplayName = "clone cred",
            EncryptedPayload = encryptor.Encrypt(payloadJson), Status = CredentialStatus.Active,
        });

        var repoId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId,
            ExternalId = repoId.ToString(), NamespacePath = "org", Name = "repo", FullPath = "org/repo",
            DefaultBranch = defaultBranch, CloneUrlHttps = cloneUrlHttps, WebUrl = "https://local/org/repo",
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    private static async Task<bool> GitReadyAsync()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    private static string? Env(string name) => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)) ? null : Environment.GetEnvironmentVariable(name);

    /// <summary>A bare local repo standing in for the remote — base-seeding + best-effort cleanup.</summary>
    private sealed class BareRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-sup-livewholeloop-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;

        public BareRemote()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
        }

        public string Url => new Uri(_bare).AbsoluteUri;

        public async Task SeedBaseAsync(Dictionary<string, string> files)
        {
            await Git(_root, "init", "--bare", "-b", "main", _bare);
            var seed = Path.Combine(_root, "seed");
            Directory.CreateDirectory(seed);
            await Git(seed, "clone", _bare, seed);
            await Git(seed, "config", "user.email", "test@codespace.dev");
            await Git(seed, "config", "user.name", "Test");
            await Git(seed, "config", "commit.gpgsign", "false");
            foreach (var (name, content) in files) await File.WriteAllTextAsync(Path.Combine(seed, name), content);
            await Git(seed, "add", "-A");
            await Git(seed, "commit", "-m", "seed");
            await Git(seed, "push", "origin", "main");
        }

        private static async Task<string> Git(string workdir, params string[] args)
        {
            var result = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);
            if (result.Status != SandboxStatus.Success) throw new InvalidOperationException($"git {string.Join(' ', args)} failed (exit {result.ExitCode}): {result.Stderr}");
            return result.Stdout;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
