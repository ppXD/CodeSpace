using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Reconciliation;
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
/// 🟢 THE PR-E E3 CROWN JEWEL (high fidelity — REAL engine + REAL <see cref="SupervisorTurnService"/> +
/// <see cref="RealSupervisorActionExecutor"/> + REAL <see cref="AgentRunService"/> + REAL completion notifier /
/// barrier over real Postgres; the scripted decider stands in for the LLM, the agent completion is simulated
/// like <see cref="MapAgentResumeFlowTests"/> — no real CLI). The supervisor lane DRIVES real agents
/// end-to-end:
/// <list type="bullet">
///   <item>an <c>agent.supervisor</c> run: turn 0 = plan(2 subtasks) → SELF-ADVANCES; turn 1 =
///         spawn[both] → stages 2 REAL agent runs + parks 2 <c>AgentRun</c> waits keyed <c>#turn1#0/#turn1#1</c>;
///         the WAIT-FOR-ALL barrier holds the supervisor Suspended until BOTH agents complete, then resumes →
///         turn 2 = stop → run Success. The ledger has plan/spawn/stop in order; the 2 spawned AgentRun rows
///         exist + are terminal.</item>
///   <item>RESTART-MID-SPAWN replay: drive to the spawn park, simulate a re-dispatch of the Suspended run
///         (the lost post-commit window), assert NO double-spawn — still exactly 2 agent runs (the E1 claim
///         hop replays the settled spawn, never re-stages).</item>
/// </list>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SupervisorSpawnFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;

    public SupervisorSpawnFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;

        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanSpawnStop();   // the E3 arc: plan → spawn → stop
    }

    public void Dispose()
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanThenStop();   // restore the default for sibling tests
    }

    [Fact]
    public async Task Supervisor_plans_then_spawns_two_real_agents_then_the_barrier_resumes_to_stop()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;   // the binary-less harness must not run; we simulate completion below

        try
        {
            // ── Turn 0: plan (synchronous) + self-advance. ──
            await RunEngineAsync(runId);
            (await LedgerKinds(runId, teamId)).ShouldBe(new[] { SupervisorDecisionKinds.Plan }, "turn 0 recorded the plan");

            // Self-advance → turn 1 (spawn). With AutoExecute off (the binary-less harness must not run), the
            // post-commit re-dispatch enqueue is record-only, so resolve the turn-0 self-advance wait directly
            // (the exact entry point the engine enqueues), then drive the engine to run turn 1 (which spawns).
            await ResolveSelfAdvanceAsync(runId);
            await RunEngineAsync(runId);

            Guid agent0, agent1;
            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();

                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                    .ShouldBe(WorkflowRunStatus.Suspended, "the spawn turn parks on the staged agent waits — NOT a self-advance");

                var agentWaits = await db.WorkflowRunWait.AsNoTracking()
                    .Where(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.AgentRun && w.Status == WorkflowWaitStatuses.Pending)
                    .OrderBy(w => w.IterationKey).ToListAsync();
                agentWaits.Count.ShouldBe(2, "spawn[both] staged exactly 2 real AgentRun waits");
                agentWaits.Select(w => w.IterationKey).ShouldBe(new[] { "sup#turn1#0", "sup#turn1#1" }, "the per-turn-per-spawn keys <nodeId>#turn{N}#{k}");

                // No self-advance SupervisorDecision wait for the spawn turn — the agents drive the resume.
                (await db.WorkflowRunWait.AsNoTracking().CountAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.SupervisorDecision && w.Status == WorkflowWaitStatuses.Pending))
                    .ShouldBe(0, "the spawn turn does NOT park a self-advance wait — only the agent waits");

                agent0 = Guid.Parse(agentWaits[0].Token);
                agent1 = Guid.Parse(agentWaits[1].Token);
                agent0.ShouldNotBe(agent1);

                // Both spawned agent runs exist (Queued), team-inherited, linked to the supervisor run + node.
                var spawned = await db.AgentRun.AsNoTracking().Where(r => r.Id == agent0 || r.Id == agent1).ToListAsync();
                spawned.Count.ShouldBe(2);
                spawned.ShouldAllBe(r => r.TeamId == teamId && r.WorkflowRunId == runId && r.NodeId == "sup" && r.Status == AgentRunStatus.Queued);

                // D4: both spawns of turn 1 carry the TURN-grain owning cell key (<nodeId>#turn{N}) — addressable to
                // the turn that spawned them. (The finer per-spawn #{k} grain lives on the wait rows above; the agent
                // run records the turn cell.) A reverted/empty key would fail here.
                spawned.Select(r => r.IterationKey).Distinct().ShouldBe(new[] { "sup#turn1" });

                // Both were dispatched to the executor on suspend (the post-commit DispatchPendingAgentRunAsync).
                var dispatched = jobClient.Calls.Where(c => c.ServiceType == typeof(IAgentRunExecutor) && c.MethodName == nameof(IAgentRunExecutor.ExecuteAsync)).Select(c => c.RunId).ToList();
                dispatched.ShouldContain(agent0);
                dispatched.ShouldContain(agent1);
            }

            // ── Agent 1 completes FIRST: the wait-for-all barrier holds the supervisor Suspended. ──
            await SimulateAgentCompletionAsync(agent1, "BETA-DONE");

            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();
                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                    .ShouldBe(WorkflowRunStatus.Suspended, "one of two spawned agents finishing does NOT advance the supervisor (the barrier)");
                (await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.Token == agent0.ToString())).Status
                    .ShouldBe(WorkflowWaitStatuses.Pending, "agent 0's wait is untouched by agent 1's completion");
            }

            // ── Agent 0 completes: the LAST wait → the barrier resolves it + flips the run Pending. ──
            await SimulateAgentCompletionAsync(agent0, "ALPHA-DONE");
            // The barrier's re-dispatch enqueue is record-only (AutoExecute off); drive the engine to run turn 2.
            await RunEngineAsync(runId);

            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();

                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                    .ShouldBe(WorkflowRunStatus.Success, "once both agents completed, the supervisor resumed → turn 2 stop → the run completes");

                (await LedgerKinds(runId, teamId)).ShouldBe(
                    new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Spawn, SupervisorDecisionKinds.Stop },
                    "the decision ledger has plan/spawn/stop in Sequence order");

                // The spawned agent rows are terminal (the simulated completion drove them Succeeded).
                var spawned = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).ToListAsync();
                spawned.Count.ShouldBe(2, "exactly the two spawned agent runs — no extra runs");
                spawned.ShouldAllBe(r => r.Status == AgentRunStatus.Succeeded);
            }
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task A_spawn_with_per_agent_dispatch_stages_two_agents_with_distinct_roles_and_overrides()
    {
        // L4 arc B headline: ONE spawn fans out TWO agents the MODEL shaped differently — the per-agent role folds into
        // each goal, and the harness/autonomy requests reach the persisted task (autonomy clamped to the ceiling).
        using (var s = _fixture.BeginScope()) s.Resolve<SupervisorDecisionScript>().PlanSpawnDispatchStop();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            await RunEngineAsync(runId);
            await ResolveSelfAdvanceAsync(runId);
            await RunEngineAsync(runId);   // turn 1: spawn with agents[]

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            var tasks = (await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).Select(r => r.TaskJson).ToListAsync())
                .Select(j => JsonSerializer.Deserialize<AgentTask>(j!, AgentJson.Options)!)
                .ToList();
            tasks.Count.ShouldBe(2, "the spawn staged two real agent runs");

            var backend = tasks.Single(t => t.Goal.Contains("backend implementer"));
            backend.Goal.ShouldBe("As the backend implementer, do alpha", "the role folds into the agent's goal + the planned instruction");
            backend.Harness.ShouldBe("claude-code", "the per-agent harness override reached the persisted task");
            backend.Autonomy.ShouldBe(AgentAutonomyLevel.Confined, "the per-agent autonomy request (≤ the Standard ceiling) reached the persisted task");

            var frontend = tasks.Single(t => t.Goal.Contains("frontend adapter"));
            frontend.Goal.ShouldBe("As the frontend adapter, do beta");
            frontend.Harness.ShouldBe("codex-cli", "no override → the default harness");
            frontend.Autonomy.ShouldBe(AgentAutonomyLevel.Standard, "no autonomy request → the default ceiling");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task A_dispatch_to_an_unbound_repo_fails_the_spawn_cleanly_without_stranding_the_run()
    {
        // L4 arc B safety: a model-authored dispatch targeting a repo the operator did NOT bind throws the repo clamp;
        // the turn service must terminalize the spawn as a CLEAN Failed (not a stranded-Running decision), staging no agents.
        using (var s = _fixture.BeginScope()) s.Resolve<SupervisorDecisionScript>().PlanSpawnBadRepoStop();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            await RunEngineAsync(runId);
            await ResolveSelfAdvanceAsync(runId);
            try { await RunEngineAsync(runId); } catch { /* the clamp failure surfaces through the node; the decision terminalized below */ }

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            var spawn = (await db.SupervisorDecisionRecord.AsNoTracking().Where(r => r.SupervisorRunId == runId && r.DecisionKind == SupervisorDecisionKinds.Spawn).ToListAsync()).Single();
            spawn.Status.ShouldBe(SupervisorDecisionStatus.Failed, "the repo clamp violation terminalized the spawn as a CLEAN failure — never stranded Running");
            spawn.Error.ShouldContain("did not bind", Case.Insensitive, "the failure reason is the legible clamp message");

            (await db.AgentRun.AsNoTracking().CountAsync(r => r.WorkflowRunId == runId)).ShouldBe(0, "no agent was staged — the clamp rejected the dispatch before any run was created");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task A_dispatch_authoring_a_model_outside_the_pool_fails_the_spawn_cleanly()
    {
        // Option B: a model-authored dispatch whose model is NOT a credentialed model in the operator's allowed pool
        // (here: "rogue-model", out of the seeded one-row pool) fails the dispatch resolution; the turn service
        // terminalizes the spawn as a CLEAN Failed (never stranded Running), staging no agents. Also proves the config
        // round-trip: allowedModelIds on the node config threads into the turn context's pool.
        using (var s = _fixture.BeginScope()) s.Resolve<SupervisorDecisionScript>().PlanSpawnBadModelStop();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (_, allowedRowId) = await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "allowed-model");   // the only model in the pool; the dispatch authors "rogue-model" (out of pool)
        var workflowId = await CreateWorkflowAsync(teamId, userId, $$"""{"goal":"ship the feature","allowedModelIds":["{{allowedRowId}}"]}""");
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            await RunEngineAsync(runId);
            await ResolveSelfAdvanceAsync(runId);
            try { await RunEngineAsync(runId); } catch { /* the clamp failure surfaces through the node; the decision terminalized below */ }

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            var spawn = (await db.SupervisorDecisionRecord.AsNoTracking().Where(r => r.SupervisorRunId == runId && r.DecisionKind == SupervisorDecisionKinds.Spawn).ToListAsync()).Single();
            spawn.Status.ShouldBe(SupervisorDecisionStatus.Failed, "the model clamp violation terminalized the spawn as a CLEAN failure — never stranded Running");
            spawn.Error.ShouldContain("allowed model pool", Case.Insensitive, "the failure reason is the legible clamp message");

            (await db.AgentRun.AsNoTracking().CountAsync(r => r.WorkflowRunId == runId)).ShouldBe(0, "no agent was staged — the clamp rejected the dispatch before any run was created");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task A_restart_mid_spawn_does_not_double_spawn()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            // Drive to the spawn park: turn 0 plan → self-advance → turn 1 spawn stages 2 agents + parks.
            await RunEngineAsync(runId);
            await ResolveSelfAdvanceAsync(runId);
            await RunEngineAsync(runId);

            int agentsAfterSpawn;
            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();
                agentsAfterSpawn = await db.AgentRun.AsNoTracking().CountAsync(r => r.WorkflowRunId == runId);
                agentsAfterSpawn.ShouldBe(2, "the spawn staged exactly 2 agent runs");
                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Suspended);
            }

            // SIMULATE A RESTART MID-SPAWN: the agents never completed (no SimulateAgentCompletion). Re-dispatch
            // the still-Suspended run as a crash-recovery would. The supervisor node re-enters: the spawn turn's
            // decision is a SETTLED terminal ledger row, so the rehydrate REPLAYS it (TurnNumber advances) and the
            // claim hop never re-runs the spawn side effect → NO new agent runs. The run re-suspends on the SAME
            // 2 agent waits (still pending).
            using (var scope = _fixture.BeginScope())
            {
                // Flip Suspended → Enqueued (the state a recovery re-dispatch lands the run in — the engine's
                // ExecuteRunAsync claims Enqueued → Running), then re-run the engine to re-enter the node.
                await scope.Resolve<CodeSpaceDbContext>().WorkflowRun
                    .Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, WorkflowRunStatus.Enqueued));
            }
            await RunEngineAsync(runId);

            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();

                (await db.AgentRun.AsNoTracking().CountAsync(r => r.WorkflowRunId == runId))
                    .ShouldBe(2, "NO double-spawn — the restart replayed the settled spawn decision, never re-staged its agents");

                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                    .ShouldBe(WorkflowRunStatus.Suspended, "the run re-suspended on the same 2 agent waits (still pending)");

                (await db.WorkflowRunWait.AsNoTracking().CountAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.AgentRun && w.Status == WorkflowWaitStatuses.Pending))
                    .ShouldBe(2, "exactly the original 2 agent waits remain — none leaked, none duplicated");

                // The spawn decision is still EXACTLY ONE ledger row (no second spawn claim).
                var rows = await Ledger(db, runId, teamId);
                rows.Count(r => r.DecisionKind == SupervisorDecisionKinds.Spawn).ShouldBe(1, "exactly one spawn decision row — the claim hop deduped the replay");
            }
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task A_fault_injected_crash_mid_fan_out_recovers_to_exactly_K_agents_without_orphans_or_double_spawn()
    {
        // 🟢 HIGH fidelity (REAL executor write path + REAL Pending→Running claim hop + REAL AgentRunService over
        // real Postgres). Unlike the sibling planted-residue test, the crash residue here is AUTHENTIC: a
        // ThrowingAgentRunService decorator delegates to the real service but THROWS on the 2nd CreateAsync, so the
        // executor's spawn loop commits agent 1 for real (CreateAsync saves each), then aborts BEFORE staging wait 2
        // and BEFORE the single end-of-loop SaveChanges that flushes the waits. The Pending→Running claim already
        // ran (the must-fix-#2 gate, before the side effect), so the spawn decision is left STUCK Running by the
        // REAL code path — no hand-fabricated decision row, no manual db.Add. We then re-execute WITHOUT the fault
        // and assert recovery lands EXACTLY 2 agents + 2 waits + 1 terminal spawn decision, no double-spawn.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            // ── Drive turn 0 (plan) to settle + self-advance — turn 1 (spawn) is what we crash. ──
            await RunEngineAsync(runId);
            await ResolveSelfAdvanceAsync(runId);

            // ── INJECT THE FAULT: run turn 1 (spawn[both]) through the REAL turn service with a decorator that
            // throws on the 2nd CreateAsync. The executor commits agent 0 (its own SaveChanges), then the 2nd
            // CreateAsync throws → the loop aborts before staging wait 1 + before the end-of-loop wait flush, and
            // before the terminal record. The exception propagates out (the turn service doesn't catch it). ──
            var ex = await Should.ThrowAsync<InvalidOperationException>(() => RunTurnWithSpawnFaultAsync(runId, teamId, throwOnCall: 2));
            ex.Message.ShouldBe(ThrowingAgentRunService.FaultMessage, "the throw is OUR injected fault, not an incidental failure — proves the decorator reached the executor's spawn loop");

            // ── Sanity: the AUTHENTIC mid-fan-out crash residue the REAL code path left — 1 committed orphan, 0
            // waits, the spawn decision stuck Running (flipped by the real claim hop before the side effect threw).
            // No hand-fabricated decision row, no manual db.Add. ──
            Guid orphanAgentId;
            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();

                var orphans = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).ToListAsync();
                orphans.Count.ShouldBe(1, "the fault committed exactly one agent before the 2nd CreateAsync threw — the authentic mid-fan-out orphan");
                orphans[0].Status.ShouldBe(AgentRunStatus.Queued, "the orphan never advanced past Queued — its wait was never staged");
                orphanAgentId = orphans[0].Id;

                (await db.WorkflowRunWait.AsNoTracking().CountAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.AgentRun))
                    .ShouldBe(0, "the throw aborted before the end-of-loop SaveChanges that flushes the waits");

                (await Ledger(db, runId, teamId)).Single(r => r.DecisionKind == SupervisorDecisionKinds.Spawn).Status
                    .ShouldBe(SupervisorDecisionStatus.Running, "the REAL claim hop flipped the spawn decision Running before the side effect threw — it's stuck Running, not hand-built");
            }

            // ── RECOVER: re-dispatch the run WITHOUT the fault (a normal scope). The node re-enters with ZERO
            // pending agent waits, re-runs turn 1, re-claims the stuck-Running spawn (InFlight, not Duplicate), and
            // re-executes under the existing Running claim. The executor RECLAIMS the orphan for slot 0 + creates
            // slot 1 → exactly 2 agents, 2 waits, no leaked orphan, no double-spawn. ──
            using (var scope = _fixture.BeginScope())
            {
                await scope.Resolve<CodeSpaceDbContext>().WorkflowRun
                    .Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, WorkflowRunStatus.Enqueued));
            }
            await RunEngineAsync(runId);

            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();

                var agents = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).Select(r => r.Id).ToListAsync();
                agents.Count.ShouldBe(2, "recovery landed EXACTLY 2 agents — the orphan was reclaimed, one new agent created (no double-spawn)");
                agents.ShouldContain(orphanAgentId, "the reclaimed orphan is one of the two — not leaked");

                var waits = await db.WorkflowRunWait.AsNoTracking()
                    .Where(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.AgentRun && w.Status == WorkflowWaitStatuses.Pending)
                    .OrderBy(w => w.IterationKey).ToListAsync();
                waits.Count.ShouldBe(2, "exactly 2 agent waits staged on recovery");
                waits.Select(w => w.IterationKey).ShouldBe(new[] { "sup#turn1#0", "sup#turn1#1" }, "the per-turn-per-spawn keys, k=0 reusing the orphan");
                waits.Select(w => Guid.Parse(w.Token)).ShouldBe(agents, ignoreOrder: true, "every wait token is one of the two agents — no dangling token");

                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                    .ShouldBe(WorkflowRunStatus.Suspended, "the run parked on the 2 agent waits — it did NOT self-advance past the spawn");

                var rows = await Ledger(db, runId, teamId);
                rows.Count(r => r.DecisionKind == SupervisorDecisionKinds.Spawn).ShouldBe(1, "still exactly one spawn decision row — no double-spawn claim");
                rows.Single(r => r.DecisionKind == SupervisorDecisionKinds.Spawn).Status
                    .ShouldBe(SupervisorDecisionStatus.Succeeded, "the recovered spawn recorded terminal — no longer stuck Running");
            }
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task A_crash_mid_fan_out_recovers_to_exactly_K_agents_without_orphans_or_double_spawn()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            // ── Drive turn 0 (plan) to settle — the spawn turn (turn 1) is what we crash. ──
            await RunEngineAsync(runId);
            await ResolveSelfAdvanceAsync(runId);

            // ── PLANT THE CRASH-MID-FAN-OUT RESIDUE (must-fix #1's State A) ──
            // A spawn fan-out creates each agent run one-at-a-time (CreateAsync saves each) and flushes the K
            // waits together at the END. A crash AFTER an agent was created but BEFORE the waits committed leaves:
            //   • the spawn decision stuck Running (TryBeginExecution flipped it before the side effect; the
            //     terminal record never ran), and
            //   • orphan Queued agent run(s) with NO wait pointing at them.
            // Reproduce that exact durable state: an orphan Queued agent (the real CreateAsync, so it's a faithful
            // orphan through the admission gate) + a Running spawn decision row at turn 1, and ZERO agent waits.
            Guid orphanAgentId;
            using (var scope = _fixture.BeginScope())
            {
                var runs = scope.Resolve<IAgentRunService>();
                orphanAgentId = (await runs.CreateAsync(new AgentTask { Goal = "do alpha", Harness = "codex-cli", Autonomy = AgentAutonomyLevel.Standard }, teamId, runId, "sup", iterationKey: "", cancellationToken: CancellationToken.None)).Id;

                var db = scope.Resolve<CodeSpaceDbContext>();
                db.SupervisorDecisionRecord.Add(StuckRunningSpawnDecision(runId, teamId, turnNumber: 1));
                await db.SaveChangesAsync();
            }

            // Sanity: the planted residue is exactly what a mid-fan-out crash leaves — 1 orphan, 0 waits, spawn Running.
            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();
                (await db.AgentRun.AsNoTracking().CountAsync(r => r.WorkflowRunId == runId)).ShouldBe(1, "the planted crash residue: one orphan agent");
                (await db.WorkflowRunWait.AsNoTracking().CountAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.AgentRun)).ShouldBe(0, "no agent waits committed before the crash");
                (await Ledger(db, runId, teamId)).Single(r => r.DecisionKind == SupervisorDecisionKinds.Spawn).Status.ShouldBe(SupervisorDecisionStatus.Running, "the spawn decision is stuck Running");
            }

            // ── RECOVER: re-dispatch the run. The node re-enters with ZERO pending agent waits (its barrier guard
            // doesn't trip), runs turn 1, re-claims the stuck-Running spawn (InFlight, not Duplicate), and the turn
            // service RE-EXECUTES under the existing Running claim instead of self-advancing. The executor RECLAIMS
            // the orphan for slot 0 + creates slot 1 → exactly 2 agents, 2 waits, no leaked orphan, no double-spawn. ──
            using (var scope = _fixture.BeginScope())
            {
                await scope.Resolve<CodeSpaceDbContext>().WorkflowRun
                    .Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, WorkflowRunStatus.Enqueued));
            }
            await RunEngineAsync(runId);

            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();

                var agents = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).Select(r => r.Id).ToListAsync();
                agents.Count.ShouldBe(2, "recovery landed EXACTLY 2 agents — the orphan was reclaimed, one new agent created (no double-spawn)");
                agents.ShouldContain(orphanAgentId, "the reclaimed orphan is one of the two — not leaked");

                var waits = await db.WorkflowRunWait.AsNoTracking()
                    .Where(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.AgentRun && w.Status == WorkflowWaitStatuses.Pending)
                    .OrderBy(w => w.IterationKey).ToListAsync();
                waits.Count.ShouldBe(2, "exactly 2 agent waits staged on recovery");
                waits.Select(w => w.IterationKey).ShouldBe(new[] { "sup#turn1#0", "sup#turn1#1" }, "the per-turn-per-spawn keys, k=0 reusing the orphan");
                waits.Select(w => Guid.Parse(w.Token)).ShouldBe(agents, ignoreOrder: true, "every wait token is one of the two agents — no dangling token");

                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                    .ShouldBe(WorkflowRunStatus.Suspended, "the run parked on the 2 agent waits — it did NOT self-advance past the spawn");

                var rows = await Ledger(db, runId, teamId);
                rows.Count(r => r.DecisionKind == SupervisorDecisionKinds.Spawn).ShouldBe(1, "still exactly one spawn decision row");
                rows.Single(r => r.DecisionKind == SupervisorDecisionKinds.Spawn).Status
                    .ShouldBe(SupervisorDecisionStatus.Succeeded, "the recovered spawn recorded terminal — no longer stuck Running");
            }
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task A_worker_death_mid_decision_is_recovered_by_the_real_reconciler_to_success_without_double_spawn()
    {
        // 🟢 THE PR-E P1-2 CROWN JEWEL (high fidelity — REAL fault residue + REAL reconciler via the mediator
        // command + REAL engine re-walk + REAL agent barrier over Postgres). A worker died MID-supervisor-decision:
        // the fault decorator leaves the spawn decision stuck Running with one committed orphan + zero waits, and we
        // stamp the WORKER-DEATH SIGNATURE the engine's catch never wrote — run Status=Running, StartedAt + latest
        // ledger backdated past the thresholds. The reconciler must RE-DISPATCH it (NOT fail it), and the engine
        // re-walk must replay the frozen in-flight spawn exactly-once (reclaim the orphan → exactly 2 agents), then
        // — once both agents complete — resume to turn 2 stop → Success. The recovery is driven by the REAL
        // reconciler (mediator ReconcileStuckRunsCommand), never a manual Enqueue flip (Rule 12.4 / 12.9).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            // Drive turn 0 (plan) + self-advance, then crash turn 1 (spawn) mid-fan-out via the real turn service.
            await RunEngineAsync(runId);
            await ResolveSelfAdvanceAsync(runId);
            await Should.ThrowAsync<InvalidOperationException>(() => RunTurnWithSpawnFaultAsync(runId, teamId, throwOnCall: 2));

            // Stamp the AUTHENTIC worker-death signature: the run is Running (the engine's catch never fired — the
            // host died), StartedAt + the latest ledger record backdated past the abandoned thresholds. This is the
            // exact durable state a pod that crashed mid-decision leaves behind.
            await StampWorkerDeathSignatureAsync(runId);

            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();
                var spawn = await db.SupervisorDecisionRecord.AsNoTracking().SingleAsync(d => d.SupervisorRunId == runId && d.DecisionKind == SupervisorDecisionKinds.Spawn);
                spawn.Status.ShouldBe(SupervisorDecisionStatus.Running, "precondition: a recoverable non-terminal spawn decision the frozen-replay path can finish");
            }

            // ── RECOVER via the REAL reconciler. It re-dispatches the abandoned-Running supervisor run (Running→
            // Pending→Enqueued) instead of failing it; AutoExecute is off so the engine doesn't auto-run yet. ──
            var summary = await ReconcileAsync();

            summary.RecoveredAbandonedSupervisorRun.ShouldBe(1, "the reconciler must RE-DISPATCH the abandoned-Running supervisor run, not fail it");
            summary.MarkedAbandonedFromRunning.ShouldBe(0, "the recovery sweep ran FIRST + flipped the run out of Running, so the abandoned sweep must not also fail it");
            (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Enqueued, "the recovered run was re-dispatched into Enqueued, waiting for the worker");

            (await CountRecoveryMarkersAsync(runId)).ShouldBe(1, "exactly one durable supervisor.run_recovered marker — the bound counter advanced by one");

            // Drive the engine the way the worker would: the re-walk rehydrates the frozen spawn, reclaims the
            // orphan, stages exactly 2 agents + 2 waits, and re-suspends on them (no double-spawn).
            await RunEngineAsync(runId);

            Guid agent0, agent1;
            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();
                var agents = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).Select(r => r.Id).ToListAsync();
                agents.Count.ShouldBe(2, "the recovery replayed the frozen spawn exactly-once — exactly 2 agents, the orphan reclaimed, no double-spawn");

                var waits = await db.WorkflowRunWait.AsNoTracking()
                    .Where(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.AgentRun && w.Status == WorkflowWaitStatuses.Pending)
                    .OrderBy(w => w.IterationKey).ToListAsync();
                waits.Count.ShouldBe(2, "exactly 2 agent waits staged on recovery");
                agent0 = Guid.Parse(waits[0].Token);
                agent1 = Guid.Parse(waits[1].Token);

                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Suspended, "the recovered run parked on its 2 agent waits");
            }

            // Both agents complete → the barrier resumes → turn 2 stop → the run reaches terminal Success.
            await SimulateAgentCompletionAsync(agent0, "ALPHA");
            await SimulateAgentCompletionAsync(agent1, "BETA");
            await RunEngineAsync(runId);

            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();

                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                    .ShouldBe(WorkflowRunStatus.Success,
                        customMessage: "the worker-death-recovered supervisor run must walk to terminal SUCCESS — if it's Failure the reconciler failed it instead of recovering; if Suspended the re-walk didn't finish");

                (await db.AgentRun.AsNoTracking().CountAsync(r => r.WorkflowRunId == runId))
                    .ShouldBe(2, "still exactly 2 agents end-to-end — no double-spawn across the whole recovery");

                (await Ledger(db, runId, teamId)).Count(r => r.DecisionKind == SupervisorDecisionKinds.Spawn)
                    .ShouldBe(1, "exactly one spawn decision row — the frozen replay deduped, never re-claimed");
            }
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task A_supervisor_run_already_at_the_recovery_cap_is_NOT_recovered_and_falls_through_to_failure()
    {
        // 🟢 THE LOOP-GUARD PROOF. A deterministically-crashing supervisor run does NOT advance TurnNumber and a
        // re-dispatch RESETS StartedAt, so an unbounded recovery would re-dispatch it forever. We simulate a run
        // that has ALREADY been recovered MaxSupervisorRunRecoveries times (plant that many durable
        // supervisor.run_recovered markers) + the worker-death signature + a still-stuck-Running decision. The
        // reconciler must NOT recover it again (the cap is reached) — instead it falls through to the abandoned-
        // Running sweep, which marks it Failure. This proves the loop terminates: K recoveries, then a clean fail.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            // Leave an authentic stuck-Running spawn decision via the fault, then stamp the worker-death signature.
            await RunEngineAsync(runId);
            await ResolveSelfAdvanceAsync(runId);
            await Should.ThrowAsync<InvalidOperationException>(() => RunTurnWithSpawnFaultAsync(runId, teamId, throwOnCall: 2));
            await StampWorkerDeathSignatureAsync(runId);

            // Plant the recovery budget as ALREADY SPENT: MaxSupervisorRunRecoveries durable recovery markers, as if
            // prior reconciler ticks had each re-dispatched this deterministically-crashing run. The candidate query
            // counts these and excludes a run at/over the cap.
            await PlantRecoveryMarkersAsync(runId, StuckRunReconcilerService.MaxSupervisorRunRecoveries);

            var summary = await ReconcileAsync();

            summary.RecoveredAbandonedSupervisorRun.ShouldBe(0,
                "a run already at the recovery cap must NOT be re-dispatched — the loop-guard stops here");
            summary.MarkedAbandonedFromRunning.ShouldBe(1,
                "at the cap the run falls through to the abandoned-Running sweep, which fails it cleanly — the loop TERMINATES");

            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();
                var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
                run.Status.ShouldBe(WorkflowRunStatus.Failure,
                    customMessage: "the deterministically-crashing run terminates in Failure once the recovery budget is exhausted — it does NOT loop forever");
                run.Error.ShouldNotBeNullOrEmpty();
                run.Error!.ShouldContain("abandoned", customMessage: "the failure surfaces the abandoned reason for the operator");

                (await CountRecoveryMarkersAsync(runId)).ShouldBe(StuckRunReconcilerService.MaxSupervisorRunRecoveries,
                    "no NEW recovery marker was appended — the sweep skipped the at-cap run entirely");
            }
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────

    private async Task<ReconcileStuckRunsResponse> ReconcileAsync()
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IMediator>().Send(new ReconcileStuckRunsCommand());
    }

    private async Task<WorkflowRunStatus> ReadStatusAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().Where(r => r.Id == runId).Select(r => r.Status).SingleAsync();
    }

    private async Task<int> CountRecoveryMarkersAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking()
            .CountAsync(rec => rec.RunId == runId && rec.RecordType == WorkflowRunRecordTypes.SupervisorRunRecovered);
    }

    /// <summary>
    /// Stamp the durable WORKER-DEATH signature on a run left mid-supervisor-decision: Status=Running (the host
    /// died, so the engine's catch never failed it), StartedAt + the latest ledger record backdated past the
    /// abandoned thresholds. Raw SQL because EF's audit hook + ExecuteUpdate-bypass would otherwise re-stamp now.
    /// This is what the reconciler's recoverable-candidate query keys on (Running + stale-StartedAt + stale-ledger).
    /// </summary>
    private async Task StampWorkerDeathSignatureAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var stale = DateTimeOffset.UtcNow - (StuckRunReconcilerService.RunningStuckAfter + TimeSpan.FromMinutes(5));

        // status is stored as the enum's string name (HasConversion<string>), so pass the name, not the int.
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE workflow_run SET status = {0}, started_at = {1} WHERE id = {2}", WorkflowRunStatus.Running.ToString(), stale, runId);

        await BackdateLedgerAsync(db, runId, stale);
    }

    /// <summary>
    /// Backdate EVERY ledger record's occurred_at past the liveness window so the run looks abandoned (the candidate
    /// query keys staleness on MAX(occurred_at), and a turn-0 supervisor run emits fresh node.started/run.started).
    /// workflow_run_record is append-only with an UPDATE-rejecting trigger, so we briefly DISABLE the trigger around
    /// the backdate — a test-fixture-only operation to simulate elapsed time, the production guarantee is untouched.
    /// </summary>
    private static async Task BackdateLedgerAsync(CodeSpaceDbContext db, Guid runId, DateTimeOffset occurredAt)
    {
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE workflow_run_record DISABLE TRIGGER workflow_run_record_enforce_immutability");
        try
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE workflow_run_record SET occurred_at = {0} WHERE run_id = {1}", occurredAt, runId);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE workflow_run_record ENABLE TRIGGER workflow_run_record_enforce_immutability");
        }
    }

    /// <summary>Plant <paramref name="count"/> durable supervisor.run_recovered markers, as if prior reconciler ticks had each re-dispatched this run — pre-spends the recovery budget so the cap test can prove the loop terminates.</summary>
    private async Task PlantRecoveryMarkersAsync(Guid runId, int count)
    {
        using var scope = _fixture.BeginScope();
        var logger = scope.Resolve<CodeSpace.Core.Services.Workflows.Lifecycle.IRunRecordLogger>();

        for (var attempt = 1; attempt <= count; attempt++)
            await logger.SupervisorRunRecoveredAsync(runId, attempt, CancellationToken.None);

        // The markers were just written fresh — re-backdate the whole ledger so the run STILL looks abandoned to
        // the post-cap failure sweep (whose liveness check is on MAX(occurred_at) across all this run's records).
        var stale = DateTimeOffset.UtcNow - (StuckRunReconcilerService.RunningStuckAfter + TimeSpan.FromMinutes(5));
        await BackdateLedgerAsync(scope.Resolve<CodeSpaceDbContext>(), runId, stale);
    }

    // The durable residue a crash mid fan-out leaves for the spawn decision: a Running row whose (run, key) +
    // payload match what the scripted decider re-derives on re-entry, so the recovery's re-claim collides on the
    // unique index (InFlight) and re-executes — exactly the real crashed-then-replayed path.
    private static SupervisorDecisionRecord StuckRunningSpawnDecision(Guid runId, Guid teamId, int turnNumber)
    {
        var payloadJson = JsonSerializer.Serialize(new SupervisorSpawnPayload { SubtaskIds = new[] { ScriptedSupervisorDecider.SubtaskA, ScriptedSupervisorDecider.SubtaskB } }, AgentJson.Options);

        return new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            SupervisorRunId = runId,
            DecisionKind = SupervisorDecisionKinds.Spawn,
            IdempotencyKey = SupervisorDecisionLog.DeriveIdempotencyKey(SupervisorDecisionKinds.Spawn, payloadJson, SupervisorTurnService.TurnDiscriminator(turnNumber)),
            InputHash = SupervisorDecisionLog.HashPayload(payloadJson),
            PayloadJson = payloadJson,
            Status = SupervisorDecisionStatus.Running,
            FenceEpoch = turnNumber,
        };
    }


    // Resolve the run's pending SupervisorDecision self-advance wait via the SAME entry point the engine
    // enqueues post-commit (ResumeWaitAsync) — flips the run Pending so the next RunEngineAsync runs the next turn.
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

    private async Task<IReadOnlyList<string>> LedgerKinds(Guid runId, Guid teamId)
    {
        using var verify = _fixture.BeginScope();
        return (await Ledger(verify.Resolve<CodeSpaceDbContext>(), runId, teamId)).Select(r => r.DecisionKind).ToList();
    }

    private static async Task<IReadOnlyList<Core.Persistence.Entities.SupervisorDecisionRecord>> Ledger(CodeSpaceDbContext db, Guid runId, Guid teamId) =>
        await db.SupervisorDecisionRecord.AsNoTracking().Where(d => d.SupervisorRunId == runId && d.TeamId == teamId).OrderBy(d => d.Sequence).ToListAsync();

    // Drive the executor's terminal sequence (MarkRunning → Complete → Notify) without the sandboxed CLI —
    // the exact path AgentRunExecutor follows on a real completion (mirrors MapAgentResumeFlowTests).
    private async Task SimulateAgentCompletionAsync(Guid agentRunId, string summary)
    {
        using var scope = _fixture.BeginScope();
        var runs = scope.Resolve<IAgentRunService>();
        var notifier = scope.Resolve<IAgentRunCompletionNotifier>();

        await runs.MarkRunningAsync(agentRunId, CancellationToken.None);
        await runs.CompleteAsync(agentRunId, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = summary }, CancellationToken.None);
        await notifier.NotifyCompletedAsync(agentRunId, CancellationToken.None);
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, string? supervisorConfig = null)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "sup-spawn-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = SupervisorDefinition(supervisorConfig),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    // Drive ONE supervisor turn through the REAL SupervisorTurnService with a fault-injecting IAgentRunService that
    // throws on the N-th CreateAsync — registered as a child-scope DECORATOR over the real service (Autofac
    // RegisterDecorator wraps the existing registration, so the real AgentRunService is resolved + delegated to,
    // never hand-constructed). Resolving the turn service DIRECTLY from this scope (vs through the engine, whose
    // singleton supervisor node opens its own root-child scope that the override can't reach) means the executor it
    // injects resolves the DECORATED IAgentRunService. So the real turn pipeline runs: rehydrate → decide (spawn) →
    // real Pending→Running claim hop → RealSupervisorActionExecutor.StageAgentsAndParkAsync, whose fan-out commits
    // the leading agent for real then crashes mid-loop on the 2nd CreateAsync — the authentic mid-fan-out crash
    // residue (no production change). The exception propagates out so the terminal record never runs → the spawn
    // decision is left stuck Running by the real code path. Recovery then re-enters through the full engine.
    private async Task RunTurnWithSpawnFaultAsync(Guid runId, Guid teamId, int throwOnCall)
    {
        using var scope = _fixture.BeginScope(b =>
            b.RegisterDecorator<IAgentRunService>((_, _, inner) => new ThrowingAgentRunService(inner, throwOnCall)));

        await scope.Resolve<ISupervisorTurnService>().RunTurnAsync(runId, teamId, "sup", "ship the feature", conversationId: null, goalConfig: null, CancellationToken.None);
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    // manual → sup (agent.supervisor) → terminal
    private static WorkflowDefinition SupervisorDefinition(string? supervisorConfig = null) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "sup", TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json(supervisorConfig ?? """{"goal":"ship the feature"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "sup" },
            new() { From = "sup", To = "end" },
        },
    };
}
