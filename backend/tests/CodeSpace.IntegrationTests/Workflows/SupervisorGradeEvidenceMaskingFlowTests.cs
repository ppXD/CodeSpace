using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The supervisor lane's half of "a grade keeps no workflow secret", end to end: the REAL <see cref="ISupervisorTurnService"/>
/// folding a unit's acceptance with the REAL <see cref="SupervisorAcceptanceGrader"/>, which clones a real git remote,
/// runs the check for real, and stores its evidence in the real Postgres artifact store.
///
/// <para>The unit's sealed task handed it a deploy key, which it committed into its branch. The model authored an
/// acceptance check that both prints that file and carries a second value the supervisor node's scope resolved. The
/// check runs with the exact values; the evidence artifact (argv line and output, which a Viewer downloads), the tail
/// on the decision tape and the baseline's evidence must hold only the mask. The supervisor lane used to hand the grader
/// no secrets at all, so both values were stored verbatim.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorGradeEvidenceMaskingFlowTests(PostgresFixture fixture)
{
    private const string NodeId = "sup";
    private const string Goal = "ship the feature";

    [Fact]
    public async Task A_units_grade_keeps_neither_its_sealed_secret_nor_the_scope_secret_its_check_carries()
    {
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        // MUTATION this pins: drop WorkflowSecrets from the unit lane's request (or stop opening the unit's sealed task,
        // or stop reading the spec's scope secrets) → the evidence artifact stores a canary → red.
        var unitSecret = $"CANARY-G2-unit-{Guid.NewGuid():N}";
        var specSecret = $"CANARY-G2-scope-{Guid.NewGuid():N}";
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new() { ["README.md"] = "base\n" });
        var baseSha = await remote.HeadShaAsync();
        await remote.CommitOnBranchAsync("candidate", new() { ["deploy.env"] = $"DEPLOY_KEY={unitSecret}\n" });

        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);
        var agentId = Guid.NewGuid();
        await SeedSealedUnitAsync(teamId, agentId, unitSecret);

        var check = new[] { "sh", "-c", $"cat deploy.env; echo token={specSecret}; exit 1" };
        await SeedDecisionAsync(runId, teamId, 1, SupervisorDecisionKinds.Plan, JsonSerializer.Serialize(new { goal = Goal, subtasks = new[] { new { id = "s1", title = "s1", instruction = "do s1", acceptance = new { command = check } } } }, AgentJson.Options), "{}");
        await SeedDecisionAsync(runId, teamId, 2, SupervisorDecisionKinds.Spawn, """{"subtaskIds":["s1"]}""", JsonSerializer.Serialize(new { agentRunIds = new[] { agentId }, agentCount = 1, agentResults = new[] { new SupervisorAgentResult { AgentRunId = agentId, Status = "Succeeded", Summary = "did it", ProducedBranch = "candidate" } } }, AgentJson.Options));
        await SeedManifestAsync(teamId, agentId, repoId, "candidate", baseSha);

        SupervisorTurnContext context;
        using (var scope = fixture.BeginScope())
        using (LlmCallContext.Push(NodeScope(scope, runId, teamId, specSecret)))
            context = await scope.Resolve<ISupervisorTurnService>().RehydrateFromDecisionLogAsync(runId, teamId, NodeId, Goal, new SupervisorGoalConfig { Goal = Goal, AgentProfile = new SupervisorAgentProfile { RepositoryId = repoId } }, CancellationToken.None);

        var unit = SupervisorOutcome.ReadAgentResults(context.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn).OutcomeJson).Single();
        unit.AcceptancePassed.ShouldBe(false, $"fixture check: the real check ran against the real clone and failed as written — detail '{unit.AcceptanceDetail}'");

        var tape = await LedgerSpawnOutcomeAsync(runId, teamId);
        tape.ShouldNotContain(unitSecret, Case.Sensitive, "the decision tape keeps the evidence tail; it must not keep the unit's secret");
        tape.ShouldNotContain(specSecret, Case.Sensitive, "nor the scope secret the check carried");

        var evidence = await TeamArtifactTextsAsync(teamId);
        evidence.Count(text => text.Contains("$ sh -c", StringComparison.Ordinal)).ShouldBeGreaterThanOrEqualTo(2, "fixture check: the candidate grade and its baseline both stored evidence");
        evidence.ShouldAllBe(text => !text.Contains(unitSecret) && !text.Contains(specSecret), "no stored evidence may carry either secret");
        evidence.ShouldContain(text => text.Contains($"DEPLOY_KEY={PersistenceSecretRedactor.Marker}") && text.Contains($"token={PersistenceSecretRedactor.Marker}"),
            "the candidate's evidence printed both values — masked, which also proves they were really there");
    }

    // ── chassis ──────────────────────────────────────────────────────────────────

    /// <summary>The ambient call scope the engine pushes for the supervisor node; its capture redactor holds the node scope's secrets — here one the check carries and one nothing carries.</summary>
    private static LlmCallScope NodeScope(ILifetimeScope scope, Guid runId, Guid teamId, string specSecret) =>
        new(runId, teamId, NodeId, "", "agent.supervisor", scope.Resolve<IRunRecordLogger>(), scope.Resolve<IArtifactOffloader>(), CaptureRedactor: new PersistenceSecretRedactor([specSecret, $"CANARY-G2-unused-{Guid.NewGuid():N}"]));

    /// <summary>The unit as the agent-run store persists a task that embeds a workflow secret: masked task_jsonb, the exact copy sealed beside it.</summary>
    private async Task SeedSealedUnitAsync(Guid teamId, Guid agentRunId, string unitSecret)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var task = new AgentTask { Goal = $"do s1; deploy key {unitSecret}", Harness = "codex-cli", WorkflowSecrets = [unitSecret] };
        var stored = AgentTaskSecrets.Seal(task, scope.Resolve<WorkflowSensitivePayloadSealer>());

        stored.Ciphertext.ShouldNotBeNull("fixture check: the unit's task is sealed, so its secret is reachable only by opening it");
        db.AgentRun.Add(new AgentRun { Id = agentRunId, TeamId = teamId, Harness = task.Harness, Status = AgentRunStatus.Succeeded, TaskJson = stored.PublicJson, SealedTaskCiphertext = stored.Ciphertext });
        await db.SaveChangesAsync();
    }

    private async Task<List<string>> TeamArtifactTextsAsync(Guid teamId)
    {
        using var scope = fixture.BeginScope();
        var ids = await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifact.AsNoTracking().Where(a => a.TeamId == teamId).Select(a => a.Id).ToListAsync();
        var store = scope.Resolve<IArtifactStore>();
        var texts = new List<string>();

        foreach (var id in ids)
            if (await store.GetBytesAsync(teamId, id, CancellationToken.None) is { } bytes) texts.Add(Encoding.UTF8.GetString(bytes.Bytes));

        return texts;
    }

    private async Task SeedDecisionAsync(Guid runId, Guid teamId, int sequence, string kind, string payloadJson, string outcomeJson)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;

        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId, Sequence = sequence,
            DecisionKind = kind, IdempotencyKey = $"{kind}-{Guid.NewGuid():N}", InputHash = "test",
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payloadJson, OutcomeJson = outcomeJson,
            FenceEpoch = 1, CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    private async Task<string> LedgerSpawnOutcomeAsync(Guid runId, Guid teamId)
    {
        using var scope = fixture.BeginScope();

        return (await scope.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Spawn)
            .Select(d => d.OutcomeJson).SingleAsync()) ?? "";
    }

    private async Task SeedManifestAsync(Guid teamId, Guid agentRunId, Guid repositoryId, string branch, string baseSha)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.PublishManifest.Add(new PublishManifest
        {
            Id = Guid.NewGuid(), TeamId = teamId, Kind = PublishManifestKind.Agent, AgentRunId = agentRunId, RepositoryId = repositoryId,
            RepositoryAlias = "primary", Branch = branch, BaseSha = baseSha, PublishStateValue = PublishState.Pushed,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedBoundRepositoryAsync(Guid teamId, string cloneUrl)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var instanceId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var repoId = Guid.NewGuid();

        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "local", BaseUrl = "https://local" });
        db.Credential.Add(new Credential
        {
            Id = credentialId, TeamId = teamId, ProviderInstanceId = instanceId, AuthType = AuthType.Pat, DisplayName = "clone cred",
            EncryptedPayload = scope.Resolve<IPayloadEncryptor>().Encrypt(scope.Resolve<ICredentialPayloadSerializer>().Serialize(new PatPayload { Token = "grade-mask-token" })),
            Status = CredentialStatus.Active,
        });
        db.Repository.Add(new Repository
        {
            Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId, ExternalId = repoId.ToString(), NamespacePath = "org", Name = "repo",
            FullPath = "org/repo", DefaultBranch = "main", CloneUrlHttps = cloneUrl, WebUrl = "https://local/org/repo",
        });
        await db.SaveChangesAsync();

        return repoId;
    }

    private async Task<Guid> SeedSupervisorRunAsync(Guid teamId, Guid userId)
    {
        using var scope = fixture.BeginScopeAs(userId, teamId, Messages.Constants.Roles.Admin);
        var workflowId = await scope.Resolve<MediatR.IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "sup-grade-mask-" + Guid.NewGuid().ToString("N")[..6],
            Definition = new Messages.Dtos.Workflows.WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes =
                [
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = NodeId, TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json("""{"goal":"ship the feature"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                ],
                Edges = [new() { From = "start", To = NodeId }, new() { From = NodeId, To = "end" }],
            },
            Activations = [],
            Enabled = true,
        });

        return await WorkflowsTestSeed.SeedManualRunAsync(fixture, workflowId, teamId);
    }

    private static async Task<bool> GitAvailableAsync()
    {
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = ["--version"], TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>A bare git remote on local disk with a base commit and a candidate branch — what the grader clones.</summary>
    private sealed class BareRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-grade-mask-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;
        private readonly string _seed;

        public BareRemote()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
            _seed = Path.Combine(_root, "seed");
        }

        public string Url => new Uri(_bare).AbsoluteUri;

        public async Task SeedBaseAsync(Dictionary<string, string> files)
        {
            await Git(_root, "init", "--bare", "-b", "main", _bare);
            Directory.CreateDirectory(_seed);
            await Git(_seed, "clone", _bare, _seed);
            await Git(_seed, "config", "user.email", "test@codespace.dev");
            await Git(_seed, "config", "user.name", "Test");
            await Git(_seed, "config", "commit.gpgsign", "false");
            await CommitAsync(files, "seed");
            await Git(_seed, "push", "origin", "main");
        }

        public async Task<string> HeadShaAsync() => (await Git(_seed, "rev-parse", "HEAD")).Trim();

        public async Task CommitOnBranchAsync(string branch, Dictionary<string, string> files)
        {
            await Git(_seed, "checkout", "-b", branch);
            await CommitAsync(files, "candidate work");
            await Git(_seed, "push", "origin", branch);
        }

        private async Task CommitAsync(Dictionary<string, string> files, string message)
        {
            foreach (var (name, content) in files) await File.WriteAllTextAsync(Path.Combine(_seed, name), content);

            await Git(_seed, "add", "-A");
            await Git(_seed, "commit", "-m", message);
        }

        private static async Task<string> Git(string workdir, params string[] args)
        {
            var result = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);

            if (result.Status != SandboxStatus.Success || result.ExitCode != 0)
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed (exit {result.ExitCode}): {result.Stderr}");

            return result.Stdout;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
