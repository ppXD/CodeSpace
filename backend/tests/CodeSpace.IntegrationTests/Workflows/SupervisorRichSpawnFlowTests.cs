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
/// the staged <c>AgentRun.TaskJson</c>). A supervisor whose node config carries a FULL agent profile
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

    private const string PersonaPrompt = "You are a careful billing engineer.";
    private const string PersonaModel = "claude-opus";
    private const string PersonaTool = "Read";   // the persona's own tool — unioned with the node's
    private const string ProfileHarness = "claude-code";
    private const string ProfileRunner = "local";

    public SupervisorRichSpawnFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;

        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanSpawnStop();   // plan(2) → spawn(both) → stop
    }

    public void Dispose()
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanThenStop();   // restore the default for sibling tests
    }

    [Fact]
    public async Task A_full_profile_supervisor_spawns_real_team_agents_with_the_persona_merge_applied()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedRepositoryAsync(teamId);
        // The persona's model must be a credentialed pool row (option B) — seed it; the spawned agent runs on THIS
        // credential (proving the dispatched-agent credential comes from the matched pool row, not the persona/profile).
        var (credentialId, _) = await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, PersonaModel);
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
                AssertRichTeamAgent(task, repoId, personaId, conversationId, credentialId);

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

    [Fact]
    public async Task A_spawn_authoring_a_per_agent_persona_slug_resolves_and_overrides_the_profile_persona()
    {
        // P3 — the model authors a DISTINCT persona for this agent via the dispatch's AgentDefinition slug. The server
        // resolves it to the team AgentDefinitionId and merges it (system prompt prepended), OVERRIDING the run-level
        // profile persona — so the brain can give each agent a specialist persona, not just the homogeneous default.
        using (var s = _fixture.BeginScope()) s.Resolve<SupervisorDecisionScript>().PlanSpawnPersonaStop();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var profilePersonaId = await SeedPersonaAsync(teamId, "Profile persona prompt.", model: null, toolsJson: null);
        var dispatchPersonaId = await SeedPersonaAsync(teamId, "You are a security reviewer.", model: null, toolsJson: null, slug: ScriptedSupervisorDecider.DispatchPersonaSlug);

        var workflowId = await CreateConfigWorkflowAsync(teamId, userId, $$"""{ "goal": "ship it", "agentProfile": { "agentDefinitionId": "{{profilePersonaId}}" } }""");
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            await RunEngineAsync(runId);
            await ResolveSelfAdvanceAsync(runId);
            await RunEngineAsync(runId);

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            var spawned = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).ToListAsync();
            spawned.Count.ShouldBe(1, "the persona spawn staged exactly one agent");

            var task = JsonSerializer.Deserialize<AgentTask>(spawned[0].TaskJson, AgentJson.Options)!;
            task.AgentDefinitionId.ShouldBe(dispatchPersonaId, "the model-authored per-agent persona OVERRODE the run-level profile persona");
            task.Goal.ShouldBe("You are a security reviewer.\n\ndo alpha", "the DISPATCH persona's system prompt is prepended (the merge ran on the dispatched persona, not the profile one)");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task A_spawn_authoring_an_unknown_persona_slug_fails_closed_without_stranding_the_decision()
    {
        // P3 — the brain only authors slugs the catalog lists; a slug NO active team persona has must fail closed (a
        // clean terminal, like an out-of-pool model), NOT silently fall back to the profile persona.
        using (var s = _fixture.BeginScope()) s.Resolve<SupervisorDecisionScript>().PlanSpawnBadPersonaStop();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var workflowId = await CreateConfigWorkflowAsync(teamId, userId, """{ "goal": "ship it" }""");
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();

        await RunEngineAsync(runId);
        await ResolveSelfAdvanceAsync(runId);
        try { await RunEngineAsync(runId); } catch { /* the clamp failure surfaces through the node; asserted below */ }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var spawn = await db.SupervisorDecisionRecord.AsNoTracking().SingleAsync(d => d.SupervisorRunId == runId && d.DecisionKind == SupervisorDecisionKinds.Spawn);
        spawn.Status.ShouldBe(SupervisorDecisionStatus.Failed, "an unknown per-agent persona slug terminalized the spawn — a clean Failed, not a stranded Running");
        spawn.Error.ShouldNotBeNull();
        spawn.Error!.ShouldContain(ScriptedSupervisorDecider.MissingPersonaSlug, Case.Insensitive);

        (await db.AgentRun.AsNoTracking().CountAsync(r => r.WorkflowRunId == runId)).ShouldBe(0, "no agent staged — the persona clamp rejected the unknown slug");
    }

    [Fact]
    public async Task A_spawn_authoring_a_persona_outside_the_allowed_pool_fails_closed_without_stranding()
    {
        // The persona pool is the persona analogue of the model pool: a model-authored slug whose persona is REAL +
        // team-owned but NOT in the operator's allowedAgentDefinitionIds must FAIL CLOSED at dispatch (a clean terminal,
        // like an out-of-pool model) — the pool is not bypassable via a model-authored slug. The dispatch gate is the
        // security floor (the catalog clamp is the UX half; the scripted decider bypasses the catalog on purpose).
        using (var s = _fixture.BeginScope()) s.Resolve<SupervisorDecisionScript>().PlanSpawnPersonaStop();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var dispatchPersonaId = await SeedPersonaAsync(teamId, "You are a security reviewer.", model: null, toolsJson: null, slug: ScriptedSupervisorDecider.DispatchPersonaSlug);
        var inPoolPersonaId = await SeedPersonaAsync(teamId, "An allowed persona.", model: null, toolsJson: null);

        // Pool = ONLY the other persona → the dispatched persona is out of pool.
        var workflowId = await CreateConfigWorkflowAsync(teamId, userId, $$"""{ "goal": "ship it", "allowedAgentDefinitionIds": ["{{inPoolPersonaId}}"] }""");
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();

        await RunEngineAsync(runId);
        await ResolveSelfAdvanceAsync(runId);
        try { await RunEngineAsync(runId); } catch { /* the pool gate surfaces through the node; asserted below */ }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var spawn = await db.SupervisorDecisionRecord.AsNoTracking().SingleAsync(d => d.SupervisorRunId == runId && d.DecisionKind == SupervisorDecisionKinds.Spawn);
        spawn.Status.ShouldBe(SupervisorDecisionStatus.Failed, "an out-of-pool persona terminalized the spawn — a clean Failed, not a stranded Running");
        spawn.Error.ShouldNotBeNull();
        spawn.Error!.ShouldContain("allowed agent pool", Case.Insensitive);

        (await db.AgentRun.AsNoTracking().CountAsync(r => r.WorkflowRunId == runId)).ShouldBe(0, "no agent staged — the persona pool gate rejected the out-of-pool persona");
    }

    [Fact]
    public async Task A_spawn_with_the_dispatched_persona_IN_the_allowed_pool_dispatches_normally()
    {
        // The positive path: the same model-authored persona slug, but the pool INCLUDES its id → the gate passes and the
        // agent stages normally (proving the gate doesn't false-reject an in-pool persona).
        using (var s = _fixture.BeginScope()) s.Resolve<SupervisorDecisionScript>().PlanSpawnPersonaStop();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var dispatchPersonaId = await SeedPersonaAsync(teamId, "You are a security reviewer.", model: null, toolsJson: null, slug: ScriptedSupervisorDecider.DispatchPersonaSlug);

        var workflowId = await CreateConfigWorkflowAsync(teamId, userId, $$"""{ "goal": "ship it", "allowedAgentDefinitionIds": ["{{dispatchPersonaId}}"] }""");
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            await RunEngineAsync(runId);
            await ResolveSelfAdvanceAsync(runId);
            await RunEngineAsync(runId);

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            var spawned = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).ToListAsync();
            spawned.Count.ShouldBe(1, "an in-pool persona dispatches normally — the gate passes");

            var task = JsonSerializer.Deserialize<AgentTask>(spawned[0].TaskJson, AgentJson.Options)!;
            task.AgentDefinitionId.ShouldBe(dispatchPersonaId, "the in-pool dispatched persona is stamped on the spawned agent");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task A_spawn_with_a_profile_default_persona_outside_the_allowed_pool_fails_closed()
    {
        // Defense-in-depth: the single post-resolution gate bounds the RESOLVED persona, so even the run-level PROFILE
        // DEFAULT persona (NO model-authored slug) must be in the pool. An operator who sets a profile persona outside
        // their own pool is rejected SERVER-side (the frontend keeps it in-pool, but the server is the floor) — proving
        // the gate is not just for model-authored slugs.
        using (var s = _fixture.BeginScope()) s.Resolve<SupervisorDecisionScript>().PlanSpawnStop();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var profilePersonaId = await SeedPersonaAsync(teamId, "Profile persona.", model: null, toolsJson: null);
        var inPoolPersonaId = await SeedPersonaAsync(teamId, "An allowed persona.", model: null, toolsJson: null);

        // Profile default = profilePersonaId; pool = ONLY the other persona → the profile default is out of pool.
        var workflowId = await CreateConfigWorkflowAsync(teamId, userId, $$"""{ "goal": "ship it", "allowedAgentDefinitionIds": ["{{inPoolPersonaId}}"], "agentProfile": { "agentDefinitionId": "{{profilePersonaId}}" } }""");
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();

        await RunEngineAsync(runId);
        await ResolveSelfAdvanceAsync(runId);
        try { await RunEngineAsync(runId); } catch { /* the pool gate surfaces through the node; asserted below */ }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var spawn = await db.SupervisorDecisionRecord.AsNoTracking().SingleAsync(d => d.SupervisorRunId == runId && d.DecisionKind == SupervisorDecisionKinds.Spawn);
        spawn.Status.ShouldBe(SupervisorDecisionStatus.Failed, "the out-of-pool PROFILE DEFAULT persona terminalized the spawn — the gate bounds the profile default, not just model-authored slugs");
        spawn.Error.ShouldNotBeNull();
        spawn.Error!.ShouldContain("allowed agent pool", Case.Insensitive);

        (await db.AgentRun.AsNoTracking().CountAsync(r => r.WorkflowRunId == runId)).ShouldBe(0, "no agent staged — the profile-default persona was out of pool");
    }

    private async Task<Guid> CreateConfigWorkflowAsync(Guid teamId, Guid userId, string config)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "sup-p3-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = SupervisorDefinitionWithConfig(config),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    /// <summary>Assert one spawned task is a REAL team agent: every profile field + the persona-merged model / tools / credential — what an agent.code node with the same config would produce.</summary>
    private static void AssertRichTeamAgent(AgentTask task, Guid repoId, Guid personaId, Guid conversationId, Guid expectedCredentialId)
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
        task.ModelCredentialId.ShouldBe(expectedCredentialId, "option B: the effective (persona) model resolved to its credentialed pool row → the agent runs on THAT row's credential");

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

    private async Task<Guid> SeedPersonaAsync(Guid teamId, string systemPrompt, string? model, string? toolsJson, string? slug = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var now = DateTimeOffset.UtcNow;
        var agent = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Slug = slug ?? "persona-" + Guid.NewGuid().ToString("N")[..8],
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

    [Fact]
    public async Task A_persona_model_outside_the_allowed_pool_fails_the_spawn_closed()
    {
        // S4 backstop: the operator's pool must gate the PERSONA model too. A plain spawn (no per-agent model) lets the
        // dispatch-time resolver fill the profile persona's model AFTER the pre-resolution clamp — so a persona that
        // references a pool-EXCLUDED model must still fail closed, else the pool is bypassable via a persona reference.
        using (var s = _fixture.BeginScope()) s.Resolve<SupervisorDecisionScript>().PlanSpawnStop();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var personaId = await SeedPersonaAsync(teamId, PersonaPrompt, PersonaModel, toolsJson: null);   // PersonaModel = "claude-opus"

        // The pool allows a DIFFERENT credentialed model than the persona's → the resolved persona model is out of pool.
        var (_, allowedRowId) = await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "only-allowed-model");
        var workflowId = await CreatePersonaPoolWorkflowAsync(teamId, userId, personaId, allowedRowId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            await RunEngineAsync(runId);
            await ResolveSelfAdvanceAsync(runId);
            try { await RunEngineAsync(runId); } catch { /* the clamp failure surfaces through the node; asserted below */ }

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            var spawn = await db.SupervisorDecisionRecord.AsNoTracking().SingleAsync(d => d.SupervisorRunId == runId && d.DecisionKind == SupervisorDecisionKinds.Spawn);
            spawn.Status.ShouldBe(SupervisorDecisionStatus.Failed, "the persona's pool-excluded model terminalized the spawn — the pool is NOT bypassable via a persona reference");
            spawn.Error.ShouldContain("allowed model pool", Case.Insensitive);

            (await db.AgentRun.AsNoTracking().CountAsync(r => r.WorkflowRunId == runId)).ShouldBe(0, "no agent staged — the post-resolution clamp rejected the persona model");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    private async Task<Guid> CreatePersonaPoolWorkflowAsync(Guid teamId, Guid userId, Guid personaId, Guid allowedRowId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var config = $$"""{ "goal": "ship it", "allowedModelIds": ["{{allowedRowId}}"], "agentProfile": { "agentDefinitionId": "{{personaId}}" } }""";
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "sup-pool-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = SupervisorDefinitionWithConfig(config),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private static WorkflowDefinition SupervisorDefinitionWithConfig(string config) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "sup", TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json(config), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "sup" }, new() { From = "sup", To = "end" } },
    };

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
