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
/// resolver <c>WorkflowEngine.StageAgentRunAsync</c> runs for an <c>agent.run</c> node — proving the spawn
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

            // B1: the per-subtask goal stays the CLEAN planned instruction, distinct per agent; the persona rides its own
            // SystemPrompt channel (the merge ran on the real path, routing the persona natively, NOT into the goal).
            var goals = tasks.Select(t => t.Goal).OrderBy(g => g).ToList();
            goals.ShouldBe(new[] { "do alpha", "do beta" });   // each subtask's goal is its CLEAN planned instruction — no persona baked in
            tasks.ShouldAllBe(t => t.SystemPrompt == PersonaPrompt, "the persona is stamped on SystemPrompt (its native channel) for every spawned agent");

            // The denormalized Harness column also reflects the profile harness (not the codex-cli default).
            spawned.ShouldAllBe(r => r.Harness == ProfileHarness, "the spawned run's harness is the profile harness");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task A_transient_brain_fault_on_consecutive_turns_is_retried_and_the_spawn_stages_each_agent_exactly_once()
    {
        // Slice A, the real-engine proof: a TRANSIENT gateway fault on the brain call mid-orchestration used to
        // terminalize the whole durable run (the supervisor node's default single attempt). The production
        // RetryingSupervisorDeciderDecorator that wraps the (scripted) decider must now recover it IN PLACE — and the
        // recovered spawn must still stage each agent EXACTLY ONCE (the brain-call retries re-ask the decision, they
        // never re-execute it), reusing the same multi-agent staging path the persona-merge test drives.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedRepositoryAsync(teamId);
        var (credentialId, _) = await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, PersonaModel);
        var personaId = await SeedPersonaAsync(teamId, PersonaPrompt, PersonaModel, $"[\"{PersonaTool}\"]");
        var conversationId = Guid.NewGuid();

        var workflowId = await CreateWorkflowAsync(teamId, userId, repoId, personaId, conversationId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;   // the binary-less harness must not run; we inspect the staged agents

        // A transient on the PLAN turn (once) AND the SPAWN turn (twice) — both within the default 3-attempt budget.
        SupervisorDecisionScript script;
        using (var s = _fixture.BeginScope())
        {
            script = s.Resolve<SupervisorDecisionScript>();
            script.FailTransientlyOnTurn(0, 1);
            script.FailTransientlyOnTurn(1, 2);
        }

        try
        {
            await RunEngineAsync(runId);          // turn 0 plan — throws transient once, the retry recovers
            await ResolveSelfAdvanceAsync(runId);
            await RunEngineAsync(runId);          // turn 1 spawn — throws transient twice, the third attempt stages both

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            // Every injected fault was actually thrown → the decider really faulted on both turns (not a silent no-op).
            script.RemainingTransientFaults(0).ShouldBe(0, "the plan-turn transient was thrown");
            script.RemainingTransientFaults(1).ShouldBe(0, "both spawn-turn transients were thrown");

            // The retry recovered both, and the spawn staged EXACTLY 2 agents — the two spawn-turn retries re-ask the
            // decision but never re-execute it, so neither subtask's agent is staged twice.
            var spawned = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).ToListAsync();
            spawned.Count.ShouldBe(2, "the recovered spawn staged each subtask's agent exactly once despite two brain-call retries");

            // Both decisions are recorded terminal Succeeded — recovered cleanly, never stranded Running, never Failed.
            var plan = await db.SupervisorDecisionRecord.AsNoTracking().SingleAsync(d => d.SupervisorRunId == runId && d.DecisionKind == SupervisorDecisionKinds.Plan);
            plan.Status.ShouldBe(SupervisorDecisionStatus.Succeeded, "the plan turn recovered from its transient and completed");

            var spawn = await db.SupervisorDecisionRecord.AsNoTracking().SingleAsync(d => d.SupervisorRunId == runId && d.DecisionKind == SupervisorDecisionKinds.Spawn);
            spawn.Status.ShouldBe(SupervisorDecisionStatus.Succeeded, "the spawn recovered from two transients and completed — not Failed, not stranded Running");
        }
        finally
        {
            using (var s = _fixture.BeginScope()) s.Resolve<SupervisorDecisionScript>().ClearTransientFaults();
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
            task.Goal.ShouldBe("do alpha", "the goal stays the clean planned instruction");
            task.SystemPrompt.ShouldBe("You are a security reviewer.", "the DISPATCH persona's system prompt rides SystemPrompt (the merge ran on the dispatched persona, not the profile one)");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task A_spawn_authoring_an_unknown_persona_slug_is_rejected_re_authorably_and_the_run_survives()
    {
        // This test previously pinned the OPPOSITE: an unknown slug threw and terminalized the run, aligned by intent with
        // the out-of-pool MODEL case. That alignment was wrong, and it cost four real-model runs — 2026-08-19 10:16 through
        // 2026-08-20 01:12 all died byte-identically on the invented slug 'metis-coder', with agents=0 after the plan and
        // the dependency staging had already succeeded, and the whole-loop gate reported each as a CODE regression.
        //
        // The two cases are genuinely different. An out-of-pool persona is GOVERNANCE — the operator forbade it, so it must
        // fail closed and stay non-bypassable (ApplyDispatchAgentPool still throws; the test below still pins it). A slug
        // that resolves to NOTHING carries no governance content at all: it is a model naming something that does not
        // exist, exactly like an unknown subtask id, which this repo already rejects re-authorably. So persona now sits
        // with that sibling.
        //
        // The properties the old test protected all survive: no agent is staged, the decision is not left stranded Running,
        // and the slug is NOT silently swapped for the profile persona. What changes is that the brain gets told what it
        // got wrong and can re-author, instead of the run dying for a typo.
        using (var s = _fixture.BeginScope()) s.Resolve<SupervisorDecisionScript>().PlanSpawnBadPersonaStop();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var workflowId = await CreateConfigWorkflowAsync(teamId, userId, """{ "goal": "ship it" }""");
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();

        await RunEngineAsync(runId);
        await ResolveSelfAdvanceAsync(runId);
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var spawn = await db.SupervisorDecisionRecord.AsNoTracking().SingleAsync(d => d.SupervisorRunId == runId && d.DecisionKind == SupervisorDecisionKinds.Spawn);
        spawn.Status.ShouldNotBe(SupervisorDecisionStatus.Running, "the decision must never be left stranded — that property is unchanged");
        spawn.Status.ShouldNotBe(SupervisorDecisionStatus.Failed, "an invented slug is a model miss, and a model miss must not terminalize a decision the brain can simply re-author");

        (await db.AgentRun.AsNoTracking().CountAsync(r => r.WorkflowRunId == runId)).ShouldBe(0, "no agent staged — the whole spawn is rejected, never a partial fan-out under the profile persona");

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldNotBe(WorkflowRunStatus.Failure, "the run survives a slug the model made up — this is the regression the four live runs exposed");
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
    public async Task A_spawn_whose_only_out_of_pool_harness_is_the_platform_default_stages_on_an_admitted_one()
    {
        // WHY THIS TEST CHANGED ALIGNMENT. It used to assert that this exact config — allowedAgents set, agentProfile
        // absent — FAILED the spawn closed, and it passed. But nobody authored the harness it failed on: with no profile
        // harness the task carries AgentHarnessDefaults.DefaultHarness (codex-cli), so "allow only claude-code" made
        // EVERY spawn of the run die on the platform floor, and the node schema invites exactly that shape (allowedAgents
        // is a standalone Guardrails array; agentProfile.harness is optional and documented as "Defaults to codex-cli").
        // The property the old test actually protected — no agent ever runs on a harness outside the pool — is kept
        // verbatim below; only the disposition of an UNAUTHORED out-of-pool kind changed, from killing the run to being
        // clamped into the operator's own list. A MODEL-authored one still fails closed: the sibling test below.
        using (var s = _fixture.BeginScope()) s.Resolve<SupervisorDecisionScript>().PlanSpawnStop();
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateConfigWorkflowAsync(teamId, userId, """{ "goal": "ship it", "allowedAgents": ["claude-code"] }""");
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

            var spawn = await db.SupervisorDecisionRecord.AsNoTracking().SingleAsync(d => d.SupervisorRunId == runId && d.DecisionKind == SupervisorDecisionKinds.Spawn);
            spawn.Status.ShouldBe(SupervisorDecisionStatus.Succeeded, "the operator's allow-list constrains the platform default; it does not collide with it");

            var staged = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).ToListAsync();
            staged.Count.ShouldBe(2, "both planned units stage — the run is no longer killed by its own guardrail");

            foreach (var run in staged)
                JsonSerializer.Deserialize<AgentTask>(run.TaskJson, AgentJson.Options)!.Harness
                    .ShouldBe("claude-code", "the ONLY admitted kind — the property the old assertion protected: no agent runs outside the pool");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task A_spawn_whose_MODEL_AUTHORED_harness_is_outside_allowedAgents_fails_closed_before_staging()
    {
        // The governance half the rewrite above must not weaken: PlanSpawnDispatchStop authors Harness="claude-code" on
        // the first dispatch, and the operator admitted only codex-cli. That boundary is the OPERATOR's, so it stays a
        // fail-closed throw the brain cannot re-author around — raised by the pre-flight screen, before anything stages.
        using (var s = _fixture.BeginScope()) s.Resolve<SupervisorDecisionScript>().PlanSpawnDispatchStop();
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateConfigWorkflowAsync(teamId, userId, """{ "goal": "ship it", "allowedAgents": ["codex-cli"] }""");
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();

        await RunEngineAsync(runId);
        await ResolveSelfAdvanceAsync(runId);
        try { await RunEngineAsync(runId); } catch { }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var spawn = await db.SupervisorDecisionRecord.AsNoTracking().SingleAsync(d => d.SupervisorRunId == runId && d.DecisionKind == SupervisorDecisionKinds.Spawn);
        spawn.Status.ShouldBe(SupervisorDecisionStatus.Failed);
        spawn.Error.ShouldContain("allowed harness pool", Case.Insensitive);
        spawn.Error.ShouldContain("registered adapter", Case.Insensitive, "the record must say the kind was REAL and un-admitted — an invented kind is the separate, re-authorable case");
        (await db.AgentRun.AsNoTracking().CountAsync(r => r.WorkflowRunId == runId)).ShouldBe(0, "including the second dispatch, whose own harness was fine");
    }

    [Fact]
    public async Task The_execution_time_reconciler_cannot_repair_a_spawned_agent_onto_a_harness_outside_allowedAgents()
    {
        // The allow-list was enforced only where the spawn STAMPS a harness. The adapter that actually runs is chosen
        // again at execution: AgentRunExecutor.ExecuteAsync calls IHarnessModelReconciler.ReconcileAsync, which selected
        // from the UNCLAMPED registry — so on this exact config (admit only codex-cli; the team's default model is
        // Anthropic, which codex cannot drive) the reconciler repaired the admitted codex-cli agent onto claude-code and
        // ran it. The clamp was authoring-time only, while the field documented itself as non-bypassable.
        //
        // The assertion drives the SAME ReconcileAsync call the executor makes, on the SAME persisted TaskJson the spawn
        // produced — not a hand-built task — so it fails if the allow-list stops reaching execution for any reason.
        using (var s = _fixture.BeginScope()) s.Resolve<SupervisorDecisionScript>().PlanSpawnStop();
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedDefaultPoolModelAsync(teamId, "claude-opus", "Anthropic");

        var workflowId = await CreateConfigWorkflowAsync(teamId, userId, """{ "goal": "ship it", "allowedAgents": ["codex-cli"] }""");
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

            var taskJson = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).Select(r => r.TaskJson).FirstAsync();
            var task = JsonSerializer.Deserialize<AgentTask>(taskJson, AgentJson.Options)!;

            task.Harness.ShouldBe("codex-cli", "the spawn stamps an admitted kind — this test is about what happens AFTER that");

            var reconciled = await verify.Resolve<IHarnessModelReconciler>().ReconcileAsync(task, teamId, CancellationToken.None);

            reconciled.HarnessKind.ShouldBe("codex-cli", "the operator admitted ONLY codex-cli; execution-time repair may not step outside that list even to reach a driveable harness");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    /// <summary>Seed an enabled, DEFAULT pool model under a fresh active credential of <paramref name="provider"/> — this is what <c>ResolveTeamDefaultProviderAsync</c> reads, so it decides the provider an UNPINNED agent reconciles against.</summary>
    private async Task SeedDefaultPoolModelAsync(Guid teamId, string modelId, string provider)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var credentialId = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = credentialId, TeamId = teamId, Provider = provider, DisplayName = provider + " cred",
            EncryptedApiKey = scope.Resolve<CodeSpace.Core.Services.Credentials.IPayloadEncryptor>().Encrypt("k"), Status = CredentialStatus.Active,
        });
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = Guid.NewGuid(), ModelCredentialId = credentialId, ModelId = modelId, Enabled = true, IsDefault = true });

        await db.SaveChangesAsync();
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

    /// <summary>Assert one spawned task is a REAL team agent: every profile field + the persona-merged model / tools / credential — what an agent.run node with the same config would produce.</summary>
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

    [Fact]
    public async Task A_spawn_with_no_effective_model_still_dispatches_from_a_one_model_pool()
    {
        // The Spawn.cs regression this pins: when neither a per-agent dispatch NOR the profile/persona names a
        // model, ApplyDispatchModelAsync used to early-return the task unchanged (null effective model = "no name
        // to gate"), so the agent fell through to ModelCredentialResolver's UNBOUNDED full-team-pool default at
        // execution — an "Agent model pool" of exactly one model never actually forced it. No persona, no profile
        // model, no per-agent dispatch model — ONLY the pool (deliberately non-default: IsDefault ranking would
        // have masked a pool bound that silently widened to the whole team).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (credentialId, rowId) = await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "pool-only-model");

        var workflowId = await CreatePoolOnlyWorkflowAsync(teamId, userId, rowId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;   // inspect the staged TaskJson; no harness binary runs

        try
        {
            // Turn 0 plan → self-advance → turn 1 spawn[both] stages 2 real agent runs, neither carrying a model name.
            await RunEngineAsync(runId);
            await ResolveSelfAdvanceAsync(runId);
            await RunEngineAsync(runId);

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            var spawned = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).ToListAsync();
            spawned.Count.ShouldBe(2, "spawn[both] staged exactly 2 real agent runs despite no model anywhere in the dispatch");

            var tasks = spawned.Select(r => JsonSerializer.Deserialize<AgentTask>(r.TaskJson, AgentJson.Options)!).ToList();

            tasks.ShouldAllBe(t => t.Model == "pool-only-model", "the pool's one model filled the dispatch — not left null for the unbounded team default to pick up later");
            tasks.ShouldAllBe(t => t.ModelCredentialId == credentialId, "the credential comes from the SAME pool row the model resolved to");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    private async Task<Guid> CreatePoolOnlyWorkflowAsync(Guid teamId, Guid userId, Guid allowedRowId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var config = $$"""{ "goal": "ship it", "allowedModelIds": ["{{allowedRowId}}"] }""";
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "sup-pool-only-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = SupervisorDefinitionWithConfig(config),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
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
