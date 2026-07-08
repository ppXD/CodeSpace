using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 HIGH fidelity (Rule 12): the S1 handoff — driven through the REAL <see cref="RealSupervisorActionExecutor"/>
/// against real Postgres, with REAL producer agents (real <see cref="AgentRunExecutor"/> + a real local bare git
/// remote) so the producers' <see cref="PublishManifest"/> rows carry GENUINE branches/patches, never hand-faked
/// ones. Proves the root cause of run 28fec923 is closed: a dependent subtask's clone ref is resolved from its
/// producer(s)' recorded manifest, never a fresh clone of the repository's default branch.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorDependencyStagingFlowTests
{
    private const string NodeId = "sup";
    private const string Goal = "ship the coordinated feature";

    private readonly PostgresFixture _fixture;

    public SupervisorDependencyStagingFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_single_producer_with_a_pushed_branch_stages_the_dependent_from_that_branch()
    {
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        var (producerRunId, _) = await RunProducerAsync(teamId, repoId, "printf 'by producer\\n' > producer.txt; echo edited");
        var manifest = await SingleManifestAsync(producerRunId, teamId);
        manifest.Branch.ShouldNotBeNull("PublishMode=Branch + a bound credential → PR-2's default-on push actually pushed");

        var context = ContextWith(runId, teamId, repoId,
            plan: Plan(("producer", null), ("dependent", new[] { "producer" })),
            priorSpawns: await SucceededSpawn(teamId, ("producer", producerRunId)));

        await ExecuteSpawnAsync(context, "dependent");

        var task = await SingleStagedTaskAsync(runId);
        task.Workspace.ShouldNotBeNull("a dependency handoff pins an explicit clone ref");
        task.Workspace!.Repositories.Single().Ref.ShouldBe(manifest.Branch, "the dependent clones the producer's OWN branch, never the repository default");
        task.Goal.ShouldContain(manifest.Branch!, customMessage: "the server-authored handoff block names the producer's branch in the agent's prompt");
    }

    [Fact]
    public async Task A_single_patch_only_producer_still_hands_off_via_integration()
    {
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        // PatchOnly blocks the push (PR-2's RepositoryPolicyPublishGuard) — the producer's work lives ONLY in its recorded patch.
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.PatchOnly);
        var runId = await SeedSupervisorRunAsync(teamId);

        // A diff over the 8KB inline-offload threshold, so the manifest genuinely carries a PatchArtifactId (a
        // small diff never gets offloaded — the artifact-store round-trip is what this test proves).
        var (producerRunId, _) = await RunProducerAsync(teamId, repoId, "head -c 9000 /dev/zero | tr '\\0' 'x' > patch-only.txt; echo edited");
        var manifest = await SingleManifestAsync(producerRunId, teamId);
        manifest.Branch.ShouldBeNull("the repo policy blocked the push");
        manifest.PatchArtifactId.ShouldNotBeNull("the diff exceeded the inline-offload threshold, so it was captured as an artifact");

        var context = ContextWith(runId, teamId, repoId,
            plan: Plan(("producer", null), ("dependent", new[] { "producer" })),
            priorSpawns: await SucceededSpawn(teamId, ("producer", producerRunId)));

        await ExecuteSpawnAsync(context, "dependent");

        var task = await SingleStagedTaskAsync(runId);
        var integratedRef = task.Workspace!.Repositories.Single().Ref!;
        integratedRef.ShouldNotBe(manifest.Branch);

        (await remote.FileOnBranchAsync(integratedRef, "patch-only.txt")).Trim().Length.ShouldBe(9000,
            "the producer's RECORDED PATCH (resolved back from the artifact store) was applied onto a fresh integration branch even though it never pushed a branch of its own");
    }

    [Fact]
    public async Task Two_disjoint_producers_integrate_onto_one_branch_the_dependent_clones()
    {
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        // The integrator is PATCH-based (a pushed branch is informational-only, never fetched from) — each diff must
        // exceed the 8KB inline-offload threshold so the manifest genuinely carries a PatchArtifactId to integrate from.
        var (p1, _) = await RunProducerAsync(teamId, repoId, "head -c 9000 /dev/zero | tr '\\0' 'p' > p1.txt; echo edited");
        var (p2, _) = await RunProducerAsync(teamId, repoId, "head -c 9000 /dev/zero | tr '\\0' 'q' > p2.txt; echo edited");

        var context = ContextWith(runId, teamId, repoId,
            plan: Plan(("p1", null), ("p2", null), ("dependent", new[] { "p1", "p2" })),
            priorSpawns: await SucceededSpawn(teamId, ("p1", p1), ("p2", p2)));

        await ExecuteSpawnAsync(context, "dependent");

        var task = await SingleStagedTaskAsync(runId);
        var integratedRef = task.Workspace!.Repositories.Single().Ref!;

        (await remote.FileOnBranchAsync(integratedRef, "p1.txt")).Trim().ShouldBe(new string('p', 9000), "both disjoint producers' changes are combined onto the one branch the dependent clones");
        (await remote.FileOnBranchAsync(integratedRef, "p2.txt")).Trim().ShouldBe(new string('q', 9000));
    }

    [Fact]
    public async Task Two_conflicting_producers_block_the_spawn_and_the_existing_resolve_verb_can_reconcile_it()
    {
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync("shared.txt", "original\n");
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        // Each producer replaces the WHOLE file with a large, mutually-conflicting blob — over the 8KB inline-offload
        // threshold, so both manifests genuinely carry a PatchArtifactId the integrator can apply (patch-based, never
        // fetched from the pushed branch), and the two patches conflict on the very same lines.
        var (p1, _) = await RunProducerAsync(teamId, repoId, "head -c 9000 /dev/zero | tr '\\0' 'p' > shared.txt; echo edited");
        var (p2, _) = await RunProducerAsync(teamId, repoId, "head -c 9000 /dev/zero | tr '\\0' 'q' > shared.txt; echo edited");

        var priorSpawn = await SucceededSpawn(teamId, ("p1", p1), ("p2", p2));
        var context = ContextWith(runId, teamId, repoId,
            plan: Plan(("p1", null), ("p2", null), ("dependent", new[] { "p1", "p2" })),
            priorSpawns: priorSpawn);

        var spawnDecision = await ExecuteSpawnAsync(context, "dependent");

        (await StagedAgentRunsAsync(runId)).ShouldBeEmpty("a conflict-blocked spawn stages ZERO agents — never a partial fan-out");

        var integration = SupervisorOutcome.ReadIntegration(spawnDecision.OutcomeJson);
        integration.ShouldNotBeNull();
        integration!.IsConflicted.ShouldBeTrue();
        integration.ConflictedFiles.ShouldContain("shared.txt");

        var p1Manifest = await SingleManifestAsync(p1, teamId);
        var p2Manifest = await SingleManifestAsync(p2, teamId);
        // All-or-nothing apply: the FIRST contribution (p1) applies cleanly in the trial before the SECOND (p2) hits
        // the textual conflict and the whole set rolls back — only the contribution that actually conflicted gets a
        // FallbackBranch (mirrors LocalGitBranchIntegratorFlowTests' own conflict assertions, which check the SAME
        // shape). The resolver's actual reconciliation input (asserted below) is unaffected either way — it reads
        // EVERY prior spawn's branches via CollectAgentBranches, not just this set.
        integration.PreservedBranches.ShouldContain(p2Manifest.Branch!, customMessage: "the contribution that actually hit the textual conflict is preserved for review");

        // The blocked SPAWN decision (not a merge) is now on the tape — resolve must reconcile it via the SAME
        // widened conflict reader, proving "conflicts → the EXISTING resolve verb" is genuinely wired, not just documented.
        var resolveContext = context with { PriorDecisions = new[] { priorSpawn, spawnDecision } };
        await ExecuteResolveAsync(resolveContext);

        var resolverTasks = await StagedTasksAsync(runId);
        resolverTasks.Count.ShouldBe(1, "resolve stages exactly ONE resolver agent (the K=1 shape)");
        resolverTasks[0].Goal.ShouldContain(p1Manifest.Branch!, customMessage: "the resolver's goal names BOTH conflicting producers' branches — assembled deterministically, not model-authored");
        resolverTasks[0].Goal.ShouldContain(p2Manifest.Branch!);
    }

    [Fact]
    public async Task A_producer_manifest_missing_a_patch_blocks_the_spawn_never_silently_defaulting()
    {
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        // A defensive, should-never-happen state per I1: a manifest row recording a diff but with NEITHER a branch
        // NOR a patch artifact. Seeded directly (bypassing the normal capture path) to prove the fail-closed guard.
        var producerRunId = Guid.NewGuid();
        await SeedAnomalousManifestAsync(teamId, producerRunId, repoId);

        var context = ContextWith(runId, teamId, repoId,
            plan: Plan(("producer", null), ("dependent", new[] { "producer" })),
            priorSpawns: await SucceededSpawn(teamId, ("producer", producerRunId)));

        var spawnDecision = await ExecuteSpawnAsync(context, "dependent");

        (await StagedAgentRunsAsync(runId)).ShouldBeEmpty("a missing-patch manifest blocks the spawn rather than silently cloning the repository default");

        using var doc = JsonDocument.Parse(spawnDecision.OutcomeJson!);
        doc.RootElement.GetProperty("blockedSubtasks").EnumerateArray().Single().GetProperty("reason").GetString()
            .ShouldContain("neither a branch nor a patch was captured", customMessage: "the loud reason names exactly what went wrong");
    }

    [Fact]
    public async Task A_base_subtask_id_dispatch_override_narrows_a_multi_dependency_subtask_to_one_producer()
    {
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        var (p1, _) = await RunProducerAsync(teamId, repoId, "printf 'from p1\\n' > p1.txt; echo edited");
        var (p2, _) = await RunProducerAsync(teamId, repoId, "printf 'from p2\\n' > p2.txt; echo edited");
        var p1Manifest = await SingleManifestAsync(p1, teamId);

        var context = ContextWith(runId, teamId, repoId,
            plan: Plan(("p1", null), ("p2", null), ("dependent", new[] { "p1", "p2" })),
            priorSpawns: await SucceededSpawn(teamId, ("p1", p1), ("p2", p2)));

        // The plan declares BOTH as dependencies, but the model's per-agent dispatch narrows this spawn to p1 only.
        await ExecuteSpawnAsync(context, "dependent", agents: new[] { new SupervisorAgentDispatch { SubtaskId = "dependent", BaseSubtaskId = "p1" } });

        var task = await SingleStagedTaskAsync(runId);
        task.Workspace!.Repositories.Single().Ref.ShouldBe(p1Manifest.Branch, "the BaseSubtaskId override wins over the plan's two-producer DependsOn — no integration, just p1's own branch");
    }

    // ─── Drive the real executor ──────────────────────────────────────────────────

    /// <summary>Execute a Spawn decision through the real executor, returning it as a TERMINAL <see cref="SupervisorPriorDecision"/> — ready to both inspect (OutcomeJson) and feed back in as a later turn's prior tape (e.g. for resolve to read its conflict).</summary>
    private async Task<SupervisorPriorDecision> ExecuteSpawnAsync(SupervisorTurnContext context, string subtaskId, IReadOnlyList<SupervisorAgentDispatch>? agents = null)
    {
        using var scope = _fixture.BeginScope();
        var executor = scope.Resolve<ISupervisorActionExecutor>();

        var payload = JsonSerializer.Serialize(new SupervisorSpawnPayload { SubtaskIds = new[] { subtaskId }, Agents = agents }, AgentJson.Options);
        var decision = new SupervisorDecision { Kind = SupervisorDecisionKinds.Spawn, PayloadJson = payload };

        var execution = await executor.ExecuteAsync(decision, context, CancellationToken.None);

        return new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 3, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = payload, OutcomeJson = execution.OutcomeJson,
        };
    }

    private async Task ExecuteResolveAsync(SupervisorTurnContext context)
    {
        using var scope = _fixture.BeginScope();
        var executor = scope.Resolve<ISupervisorActionExecutor>();

        var decision = new SupervisorDecision { Kind = SupervisorDecisionKinds.Resolve, PayloadJson = "{}" };

        await executor.ExecuteAsync(decision, context, CancellationToken.None);
    }

    private async Task<AgentTask> SingleStagedTaskAsync(Guid runId) => (await StagedTasksAsync(runId)).ShouldHaveSingleItem();

    private async Task<IReadOnlyList<AgentTask>> StagedTasksAsync(Guid runId) =>
        (await StagedAgentRunsAsync(runId)).Select(r => JsonSerializer.Deserialize<AgentTask>(r.TaskJson, AgentJson.Options)!).ToList();

    private async Task<IReadOnlyList<AgentRun>> StagedAgentRunsAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking()
            .Where(r => r.WorkflowRunId == runId && r.NodeId == NodeId)
            .ToListAsync();
    }

    // ─── Context / decision-tape builders ─────────────────────────────────────────

    private static SupervisorTurnContext ContextWith(Guid runId, Guid teamId, Guid repositoryId, SupervisorPriorDecision plan, SupervisorPriorDecision priorSpawns) => new()
    {
        Goal = Goal,
        SupervisorRunId = runId,
        TeamId = teamId,
        NodeId = NodeId,
        TurnNumber = 2,
        PriorDecisions = new[] { plan, priorSpawns },
        AgentProfile = new CodeSpace.Messages.Dtos.Agents.SupervisorAgentProfile { RepositoryId = repositoryId },
    };

    private static SupervisorPriorDecision Plan(params (string Id, string[]? DependsOn)[] subtasks)
    {
        var payload = JsonSerializer.Serialize(new SupervisorPlanPayload
        {
            Goal = Goal,
            Subtasks = subtasks.Select(s => new SupervisorPlannedSubtask { Id = s.Id, Title = s.Id, Instruction = $"do {s.Id}", DependsOn = s.DependsOn }).ToList(),
        }, AgentJson.Options);

        return new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 1, DecisionKind = SupervisorDecisionKinds.Plan, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payload, OutcomeJson = "{}" };
    }

    /// <summary>
    /// A prior Spawn decision recording each (subtaskId, REAL agentRunId) pair as a Succeeded producer — the
    /// positional subtaskIds[i] ↔ agentResults[i] shape <see cref="SupervisorDependencyGate"/> reads. Each entry's
    /// <c>ProducedBranch</c> is resolved off the producer's REAL manifest (null for a patch-only producer) — the
    /// resolver's OWN branch-collection (<c>CollectAgentBranches</c>) reads this same field, so a decision built
    /// without it would make <c>resolve</c> see no branches to reconcile even after a genuine conflict.
    /// </summary>
    private async Task<SupervisorPriorDecision> SucceededSpawn(Guid teamId, params (string SubtaskId, Guid AgentRunId)[] producers)
    {
        var results = new List<SupervisorAgentResult>();
        foreach (var p in producers)
        {
            using var scope = _fixture.BeginScope();
            var manifests = await scope.Resolve<IPublishManifestStore>().ListForAgentRunAsync(p.AgentRunId, teamId, CancellationToken.None);
            results.Add(new SupervisorAgentResult { AgentRunId = p.AgentRunId, Status = "Succeeded", ProducedBranch = manifests.FirstOrDefault()?.Branch });
        }

        var payload = JsonSerializer.Serialize(new SupervisorSpawnPayload { SubtaskIds = producers.Select(p => p.SubtaskId).ToList() }, AgentJson.Options);
        var outcome = JsonSerializer.Serialize(new { agentRunIds = producers.Select(p => p.AgentRunId).ToArray(), agentCount = producers.Length, agentResults = results }, AgentJson.Options);

        return new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payload, OutcomeJson = outcome };
    }

    // ─── Real producer execution (real AgentRunExecutor + real git) ───────────────

    /// <summary>Run ONE real producer agent (a scripted /bin/sh harness) through the REAL AgentRunExecutor against the real repo — a genuine PublishManifest row + (PublishMode-dependent) a genuine pushed branch results. Returns its AgentRunId + the terminal ResultJson.</summary>
    private async Task<(Guid AgentRunId, string ResultJson)> RunProducerAsync(Guid teamId, Guid repositoryId, string script)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask { Goal = "produce", Harness = "scripted", Model = "test-model", RepositoryId = repositoryId },
            teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);

        var executor = new AgentRunExecutor(
            scope.Resolve<IAgentRunService>(),
            new AgentHarnessRegistry(new IAgentHarness[] { new ScriptedHarness(script) }),
            new HarnessModelReconciler(new AgentHarnessRegistry(new IAgentHarness[] { new ScriptedHarness(script) }), scope.Resolve<IModelPoolSelector>(), scope.Resolve<CodeSpaceDbContext>()),
            scope.Resolve<ISandboxRunnerRegistry>(),
            scope.Resolve<IAgentWorkspaceResolver>(),
            scope.Resolve<IModelCredentialResolver>(),
            scope.Resolve<IWorkspaceProviderRegistry>(),
            scope.Resolve<IAgentRunCompletionNotifier>(),
            scope.Resolve<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            scope.Resolve<CodeSpaceDbContext>(),
            scope.Resolve<CodeSpace.Core.Services.Review.IStructuredCritic>(),
            scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(),
            scope.Resolve<IPublishManifestStore>(),
            scope.Resolve<IEnumerable<IPublishGuard>>(),
            NullLogger<AgentRunExecutor>.Instance);

        await executor.ExecuteAsync(run.Id, CancellationToken.None);

        var finished = await scope.Resolve<IAgentRunService>().GetAsync(run.Id, CancellationToken.None);
        finished.Status.ShouldBe(AgentRunStatus.Succeeded, "the producer must genuinely succeed for its manifest to carry real work");

        return (run.Id, finished.ResultJson!);
    }

    private async Task<PublishManifest> SingleManifestAsync(Guid agentRunId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return (await scope.Resolve<IPublishManifestStore>().ListForAgentRunAsync(agentRunId, teamId, CancellationToken.None)).ShouldHaveSingleItem();
    }

    /// <summary>Seed a manifest row that violates I1 (a diff recorded, but neither a branch nor a patch artifact) — bypassing the normal capture path to prove the staging resolver's fail-closed guard.</summary>
    private async Task SeedAnomalousManifestAsync(Guid teamId, Guid agentRunId, Guid repositoryId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.PublishManifest.Add(new PublishManifest
        {
            Id = Guid.NewGuid(), TeamId = teamId, Kind = PublishManifestKind.Agent, AgentRunId = agentRunId, RepositoryId = repositoryId,
            RepositoryAlias = "primary", BaseSha = "deadbeef", ChangedFileCount = 1, PublishStateValue = PublishState.PatchOnly,
        });

        await db.SaveChangesAsync();
    }

    // ─── Seeding (team / credential / repository / supervisor run) ────────────────

    private async Task<Guid> SeedTeamAsync() => (await WorkflowsTestSeed.SeedTeamAsync(_fixture)).TeamId;

    private async Task<Guid> SeedCredentialAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = await FindOrCreateProviderInstanceAsync(db, teamId);

        var serializer = scope.Resolve<ICredentialPayloadSerializer>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var payloadJson = serializer.Serialize(new PatPayload { Token = "dependency-staging-e2e-token" });

        var credentialId = Guid.NewGuid();
        db.Credential.Add(new Credential
        {
            Id = credentialId, TeamId = teamId, ProviderInstanceId = instanceId,
            AuthType = AuthType.Pat, DisplayName = "clone cred",
            EncryptedPayload = encryptor.Encrypt(payloadJson), Status = CredentialStatus.Active,
        });

        await db.SaveChangesAsync();
        return credentialId;
    }

    private async Task<Guid> FindOrCreateProviderInstanceAsync(CodeSpaceDbContext db, Guid teamId)
    {
        var existing = await db.ProviderInstance.Where(p => p.TeamId == teamId).Select(p => p.Id).FirstOrDefaultAsync();
        if (existing != Guid.Empty) return existing;

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "local", BaseUrl = "https://local" });
        await db.SaveChangesAsync();
        return instanceId;
    }

    private async Task<Guid> SeedRepositoryAsync(Guid teamId, string cloneUrl, Guid credentialId, RepositoryPublishMode publishMode)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = await FindOrCreateProviderInstanceAsync(db, teamId);

        var repoId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId,
            ExternalId = repoId.ToString(), NamespacePath = "org", Name = "repo", FullPath = "org/repo",
            DefaultBranch = "main", CloneUrlHttps = cloneUrl, WebUrl = "https://local/org/repo",
            PublishMode = publishMode,
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    private async Task<Guid> SeedSupervisorRunAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var (_, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scopeAsAdmin = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var workflowId = await scopeAsAdmin.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "sup-dep-staging-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = new WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = NodeId, TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json($$"""{"goal":"{{Goal}}"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                },
                Edges = new List<EdgeDefinition> { new() { From = "start", To = NodeId }, new() { From = NodeId, To = "end" } },
            },
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });

        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    // ─── Git helpers ────────────────────────────────────────────────────────────

    private static async Task<bool> GitAvailableAsync()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>A bare local repo standing in for the agents' remote, plus ref inspection via <c>git --git-dir</c> — real-git ground truth. Mirrors <see cref="PublishGuardChainFlowTests.BareRemote"/>.</summary>
    private sealed class BareRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-dep-staging-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;

        public BareRemote()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
        }

        public string Url => new Uri(_bare).AbsoluteUri;

        public async Task SeedWithOneCommitAsync(string fileName = "README.md", string content = "base")
        {
            await RunGitAsync(_root, "init", "--bare", "-b", "main", _bare);

            var seed = Path.Combine(_root, "seed");
            Directory.CreateDirectory(seed);
            await RunGitAsync(seed, "clone", _bare, seed);
            await RunGitAsync(seed, "config", "user.email", "test@codespace.dev");
            await RunGitAsync(seed, "config", "user.name", "Test");
            await RunGitAsync(seed, "config", "commit.gpgsign", "false");
            await File.WriteAllTextAsync(Path.Combine(seed, fileName), content);
            await RunGitAsync(seed, "add", ".");
            await RunGitAsync(seed, "commit", "-m", "seed");
            await RunGitAsync(seed, "push", "origin", "main");
        }

        public Task<string> FileOnBranchAsync(string branch, string file) => RunGitAsync(_root, "--git-dir", _bare, "show", $"{branch}:{file}");

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

    /// <summary>A CLI-less test harness: builds a /bin/sh invocation from a fixed script. Mirrors <see cref="PublishGuardChainFlowTests.ScriptedHarness"/>.</summary>
    private sealed class ScriptedHarness : IAgentHarness
    {
        private readonly string _script;

        public ScriptedHarness(string script) => _script = script;

        public string Kind => "scripted";
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "test-model" };

        public SandboxSpec BuildInvocation(AgentTask task) => new() { Command = "/bin/sh", Args = new[] { "-c", _script }, WorkingDirectory = task.WorkspaceDirectory, TimeoutSeconds = task.TimeoutSeconds };

        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) =>
            string.IsNullOrWhiteSpace(rawLine) ? Array.Empty<AgentEvent>() : new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = rawLine.Trim() } };

        public AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode) =>
            exitCode == 0
                ? new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = events.Count > 0 ? events[^1].Text : null }
                : new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = $"exit {exitCode}" };
    }
}
