using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 THE P2-3 CROWN JEWEL (high fidelity — REAL engine + REAL <see cref="SupervisorTurnService"/> +
/// <see cref="Core.Services.Supervisor.Executors.RealSupervisorActionExecutor"/> + REAL
/// <see cref="Core.Services.Agents.AgentRunService"/> + REAL <see cref="Core.Services.Agents.AgentDefinitionResolver"/>
/// over real Postgres; the scripted decider stands in for the LLM, agent completion is not reached — we inspect
/// the staged <c>AgentRun.TaskJson</c>). A flag-on supervisor whose node config carries a FULL agent profile
/// (repo + harness + model + persona + credential + runner + MCP + tools + conversation) spawns agents whose
/// PERSISTED <see cref="AgentTask"/> inherits every profile field AND has the PERSONA-MERGE applied — the same
/// resolver <c>WorkflowEngine.StageAgentRunAsync</c> runs for an <c>agent.code</c> node — proving the spawn
/// envelope is a REAL team agent, not the bare skeleton pre-P2-3 produced, and that the persona-merge bypass is
/// fixed (system prompt prepended, persona model fills in, persona∪node tools union).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SupervisorRichSpawnFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly string? _flagBefore;

    private const string PersonaPrompt = "You are a careful billing engineer.";
    private const string PersonaModel = "claude-opus";
    private const string PersonaTool = "Read";   // the persona's own tool — unioned with the node's
    private const string ProfileHarness = "claude-code";
    private const string ProfileRunner = "local";

    public SupervisorRichSpawnFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _flagBefore = Environment.GetEnvironmentVariable(SupervisorLane.EnabledEnvVar);
        Environment.SetEnvironmentVariable(SupervisorLane.EnabledEnvVar, "1");

        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanSpawnStop();   // plan(2) → spawn(both) → stop
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SupervisorLane.EnabledEnvVar, _flagBefore);
        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanThenStop();   // restore the default for sibling tests
    }

    [Fact]
    public async Task A_full_profile_supervisor_spawns_real_team_agents_with_the_persona_merge_applied()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedRepositoryAsync(teamId);
        var personaId = await SeedPersonaAsync(teamId, PersonaPrompt, PersonaModel, $"[\"{PersonaTool}\"]");
        var conversationId = Guid.NewGuid();   // the supervisor's approval conversation — a reference, nothing posts on this path

        var workflowId = await CreateWorkflowAsync(teamId, userId, repoId, personaId, conversationId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;   // the binary-less harness must not run; we inspect the staged TaskJson

        try
        {
            // Turn 0 plan → self-advance → turn 1 spawn[both] stages 2 real agent runs.
            await RunEngineAsync(runId);
            await ResolveSelfAdvanceAsync(runId);
            await RunEngineAsync(runId);

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            var spawned = await db.AgentRun.AsNoTracking()
                .Where(r => r.WorkflowRunId == runId).OrderBy(r => r.CreatedDate).ThenBy(r => r.Id).ToListAsync();
            spawned.Count.ShouldBe(2, "spawn[both] staged exactly 2 real agent runs");

            // Each spawned run's PERSISTED AgentTask carries the profile + the persona-merge. The two subtasks
            // ("do alpha" / "do beta") differ only by their per-subtask goal floor; everything else is the profile.
            var tasks = spawned.Select(r => JsonSerializer.Deserialize<AgentTask>(r.TaskJson, AgentJson.Options)!).ToList();

            foreach (var task in tasks)
                AssertRichTeamAgent(task, repoId, personaId, conversationId);

            // The per-subtask goal floor is the persona prompt PREPENDED to the planned instruction (the merge),
            // distinct per agent — proving the goal fold + the persona compose both ran on the real path.
            var goals = tasks.Select(t => t.Goal).OrderBy(g => g).ToList();
            goals.ShouldContain($"{PersonaPrompt}\n\ndo alpha", "the persona prompt is PREPENDED to subtask alpha's planned instruction (the persona-merge ran)");
            goals.ShouldContain($"{PersonaPrompt}\n\ndo beta", "the persona prompt is PREPENDED to subtask beta's planned instruction (the persona-merge ran)");

            // The denormalized Harness column also reflects the profile harness (not the codex-cli default).
            spawned.ShouldAllBe(r => r.Harness == ProfileHarness, "the spawned run's harness is the profile harness");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task A_spawn_referencing_a_non_existent_persona_fails_cleanly_without_stranding_the_decision()
    {
        // A profile authored with a persona id that doesn't exist for the team — the dispatch-time resolver
        // throws AgentDefinitionResolutionException at the spawn turn. Without the fix the exception would
        // escape the walk as a misleading ENGINE-BOOTSTRAP failure with the spawn decision stranded Running;
        // the fix records it as a CLEAN terminal node failure (the decision row flips Failed, not Running).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var missingPersonaId = Guid.NewGuid();   // never seeded → not found for this team

        var workflowId = await CreateBadPersonaWorkflowAsync(teamId, userId, missingPersonaId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();

        // Turn 0 plan → self-advance → turn 1 spawn resolves the missing persona → clean node failure.
        await RunEngineAsync(runId);
        await ResolveSelfAdvanceAsync(runId);
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Failure, "the unresolvable persona fails the run cleanly (a node failure — check the node.failed error, not an engine bootstrap crash)");

        (await db.AgentRun.AsNoTracking().CountAsync(r => r.WorkflowRunId == runId))
            .ShouldBe(0, "no agent run is created when the persona cannot resolve");

        var node = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "sup" && n.IterationKey == "");
        node.Status.ShouldBe(NodeStatus.Failure);
        node.Error.ShouldNotBeNull();
        node.Error!.ShouldContain("agent.supervisor spawn:", customMessage: "the node failure carries the supervisor-spawn-prefixed resolver message, not a generic engine-bootstrap message");
        node.Error.ShouldContain(missingPersonaId.ToString(), customMessage: "the resolver names the missing persona id");

        var spawn = await db.SupervisorDecisionRecord.AsNoTracking()
            .SingleAsync(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Spawn);
        spawn.Status.ShouldBe(SupervisorDecisionStatus.Failed, "the spawn decision is recorded terminal Failed — NOT left stranded Running (which a re-walk would re-enter + re-throw forever)");
        spawn.Error.ShouldNotBeNull();
    }

    /// <summary>Assert one spawned task is a REAL team agent: every profile field + the persona-merged model / tools / credential — what an agent.code node with the same config would produce.</summary>
    private static void AssertRichTeamAgent(AgentTask task, Guid repoId, Guid personaId, Guid conversationId)
    {
        task.Harness.ShouldBe(ProfileHarness, "the profile harness overrides the codex-cli default");
        task.RepositoryId.ShouldBe(repoId, "the profile repo is stamped (the executor clones it)");
        task.RunnerKind.ShouldBe(ProfileRunner, "the profile runner is stamped");
        task.Autonomy.ShouldBe(AgentAutonomyLevel.Trusted, "the profile autonomy tier is stamped");
        task.Permissions.Network.ShouldBe(AgentNetworkAccess.On, "Trusted autonomy DERIVES network-on (the dial drives the real sandbox posture, not just the persisted tier)");
        task.EnableMcpEndpoint.ShouldBe(true, "the profile opts the spawned agent into the MCP fabric");
        task.ApprovalConversationId.ShouldBe(conversationId, "the supervisor's conversation is the approval surface");

        task.AgentDefinitionId.ShouldBe(personaId, "the persona reference is preserved as provenance");
        task.Model.ShouldBe(PersonaModel, "the persona model fills in (the node profile set no model) — the persona-merge ran");
        task.ModelCredentialId.ShouldBeNull("neither the profile nor the persona pinned a credential → team/operator fallback");

        // Tools are the persona's UNIONed with the node's allow-list (supplement, never narrow) — the merge ran.
        task.Tools.ShouldBe(new[] { PersonaTool, "Grep", "Bash" },
            customMessage: "the run's tools are the persona's tools UNIONed with the supervisor's allowedTools — the persona-merge ran");
    }

    // ─── Seeding ────────────────────────────────────────────────────────────────────

    private async Task<Guid> SeedRepositoryAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.Git, DisplayName = "local", BaseUrl = "https://local" });

        var repoId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = null,
            ExternalId = repoId.ToString(), NamespacePath = "org", Name = "repo", FullPath = "org/repo",
            DefaultBranch = "main", CloneUrlHttps = "https://local/org/repo.git", WebUrl = "https://local/org/repo",
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    private async Task<Guid> SeedPersonaAsync(Guid teamId, string systemPrompt, string? model, string? toolsJson)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var now = DateTimeOffset.UtcNow;
        var agent = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Slug = "persona-" + Guid.NewGuid().ToString("N")[..8],
            Name = "Billing persona",
            SystemPrompt = systemPrompt,
            Model = model,
            ToolsJson = toolsJson,
            Origin = AgentDefinitionOrigin.Authored,
            CreatedDate = now,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedDate = now,
            LastModifiedBy = SystemUsers.SeederId,
        };
        db.AgentDefinition.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, Guid repoId, Guid personaId, Guid conversationId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "sup-rich-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = SupervisorDefinition(repoId, personaId, conversationId),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    // manual → sup (agent.supervisor, full agentProfile + allowedTools + conversation) → terminal
    private static WorkflowDefinition SupervisorDefinition(Guid repoId, Guid personaId, Guid conversationId)
    {
        // allowedTools = ["Grep","Bash"] (reused for AgentTask.Tools); the profile pins repo / harness / persona /
        // runner / MCP / Trusted autonomy; no profile model so the persona's model fills in (proves the merge).
        var config = $$"""
            {
              "goal": "ship the billing feature",
              "conversationId": "{{conversationId}}",
              "allowedTools": ["Grep", "Bash"],
              "agentProfile": {
                "repositoryId": "{{repoId}}",
                "harness": "{{ProfileHarness}}",
                "agentDefinitionId": "{{personaId}}",
                "runnerKind": "{{ProfileRunner}}",
                "enableMcp": true,
                "autonomyLevel": "Trusted"
              }
            }
            """;

        return new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "sup", TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json(config), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "sup" },
                new() { From = "sup", To = "end" },
            },
        };
    }

    private async Task<Guid> CreateBadPersonaWorkflowAsync(Guid teamId, Guid userId, Guid missingPersonaId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "sup-bad-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = BadPersonaDefinition(missingPersonaId),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    // manual → sup (agentProfile.agentDefinitionId points at a persona that doesn't exist) → terminal
    private static WorkflowDefinition BadPersonaDefinition(Guid missingPersonaId)
    {
        var config = $$"""
            { "goal": "ship it", "agentProfile": { "agentDefinitionId": "{{missingPersonaId}}" } }
            """;

        return new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "sup", TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json(config), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "sup" },
                new() { From = "sup", To = "end" },
            },
        };
    }

    // ─── Engine driving (mirrors SupervisorSpawnFlowTests) ────────────────────────────

    private async Task ResolveSelfAdvanceAsync(Guid runId)
    {
        Guid waitId;
        using (var verify = _fixture.BeginScope())
        {
            waitId = (await verify.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking()
                .SingleAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.SupervisorDecision && w.Status == WorkflowWaitStatuses.Pending)).Id;
        }

        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowResumeService>().ResumeWaitAsync(runId, waitId, null, CancellationToken.None);
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }
}
