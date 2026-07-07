using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Reconciliation;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 THE P1.3 CROWN JEWEL (high fidelity — REAL reconciler via the mediator command + REAL engine re-walk over
/// real Postgres). The gap this closes: a worker that dies INSIDE <c>ChooseDecisionAsync</c> BEFORE the decider
/// even replies — the very first turn's very first decide — leaves NO <c>SupervisorDecisionRecord</c> row at all
/// (nothing was ever claimed). <see cref="StuckRunReconcilerService.RecoverAbandonedSupervisorRunsAsync"/>'s
/// original candidate query required a NON-TERMINAL ledger row as its "this run is a supervisor turn in flight"
/// proxy, so this exact residue matched NOTHING there and fell through to <c>MarkAbandonedRunningAsync</c> — a
/// clean Failure for a crash that cost NOTHING (no claim, no side effect) and would have recovered for free.
///
/// We inject the crash by calling <see cref="ISupervisorTurnService"/> DIRECTLY (bypassing the engine's own
/// catch/retry, exactly mirroring <c>SupervisorSpawnFlowTests</c>' worker-death pattern) with a decider decorator
/// that throws a PLAIN exception — never an <see cref="CodeSpace.Core.Services.Workflows.Llm.LlmApiException"/>,
/// so neither the production <c>RetryingSupervisorDeciderDecorator</c> nor the P1.1 infra-park catch swallow it —
/// simulating the worker process itself dying, not a model/gateway fault. This leaves ZERO ledger rows: the
/// authentic pre-claim residue.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SupervisorPreClaimCrashRecoveryFlowTests
{
    private readonly PostgresFixture _fixture;

    public SupervisorPreClaimCrashRecoveryFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_worker_death_before_any_claim_is_recovered_by_the_real_reconciler_to_success()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        // Stamp the node.started ledger record the ENGINE writes at the top of ExecuteNodeAsync, BEFORE it ever
        // calls RunAsync — the authentic precondition a real crash leaves (workflow_run_node is a VIEW over this
        // ledger, so "sup" now reads Running/unsettled exactly as it would mid-flight).
        await StampNodeStartedAsync(runId);

        // The worker dies INSIDE ChooseDecisionAsync's very first decide — before ANY claim. Direct call (bypassing
        // the engine) so the exception propagates uncaught, exactly like a killed process — no node.failed, no
        // run.failed, nothing but whatever the DB already held.
        await Should.ThrowAsync<InvalidOperationException>(() =>
            _fixture.BeginScope(b => b.RegisterDecorator<ISupervisorDecider>((_, _, _) => new CrashingDecider()))
                .Resolve<ISupervisorTurnService>()
                .RunTurnAsync(runId, teamId, "sup", "ship the feature", conversationId: null, goalConfig: null, CancellationToken.None));

        await StampWorkerDeathSignatureAsync(runId);

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();

            (await db.SupervisorDecisionRecord.AsNoTracking().CountAsync(d => d.SupervisorRunId == runId))
                .ShouldBe(0, "precondition: the crash happened before any claim — the ORIGINAL gap's exact ledger shape");
        }

        // ── RECOVER via the REAL reconciler. ──
        var summary = await ReconcileAsync();

        summary.RecoveredAbandonedSupervisorRun.ShouldBe(1, "a pre-claim crash on a genuine agent.supervisor node must be recovered, not failed");
        summary.MarkedAbandonedFromRunning.ShouldBe(0, "recovered BEFORE the abandoned-Running sweep sees it");
        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Enqueued, "re-dispatched, waiting for a worker");
        (await CountRecoveryMarkersAsync(runId)).ShouldBe(1);

        // The re-walk re-enters "sup" fresh: rehydrate finds an EMPTY ledger (nothing to replay), the real
        // (non-crashing) decider is resolved this time, turn 0 plans normally.
        await RunEngineAsync(runId);
        await ResolveSelfAdvanceAsync(runId);
        await RunEngineAsync(runId); // turn 1: stop

        using var final = _fixture.BeginScope();
        var finalDb = final.Resolve<CodeSpaceDbContext>();

        (await finalDb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Success, customMessage: "the recovered run must complete normally — the crash cost nothing but one wasted decide");

        var decisions = await finalDb.SupervisorDecisionRecord.AsNoTracking().Where(d => d.SupervisorRunId == runId).OrderBy(d => d.Sequence).ToListAsync();
        decisions.Select(d => d.DecisionKind).ShouldBe(new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Stop }, "a clean turn 0/1 — no phantom decision from the crashed attempt");
    }

    [Fact]
    public async Task A_pre_claim_crash_on_a_non_supervisor_node_is_left_to_the_conservative_default()
    {
        // The SAME durable residue (Running, stale, zero SupervisorDecisionRecord rows — trivially true, since
        // this workflow has no agent.supervisor node at all) on a DIFFERENT node type must NOT be swept by the
        // supervisor recovery path — proving the fix is scoped to agent.supervisor, not "any zero-decision crash".
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateNonSupervisorWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await StampWorkerDeathSignatureAsync(runId);

        var summary = await ReconcileAsync();

        summary.RecoveredAbandonedSupervisorRun.ShouldBe(0, "a non-supervisor node's crash must never be blindly redispatched — no positive proof it's safe to re-run");
        summary.MarkedAbandonedFromRunning.ShouldBe(1, "it falls through to the conservative default: fail cleanly");

        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Failure);
    }

    [Fact]
    public async Task A_run_mid_grade_with_a_fresh_heartbeat_is_neither_recovered_nor_abandoned()
    {
        // P1.3(b) — a stop decision under grade sits Running (non-terminal) for the WHOLE (potentially
        // multi-minute) grading call, so it WOULD otherwise match the abandoned-supervisor-run candidate query too
        // (the ledger-proxy for "a turn is in flight"). Stamp exactly that shape — Running run, non-terminal stop
        // decision, an otherwise-stale ledger — but with ONE fresh record (what RunGradingHeartbeatLoopAsync emits
        // every 90s in production). The genuinely-alive run must be swept by NEITHER path: not abandoned (it's not
        // dead) and not "recovered" (nothing crashed — recovering it would yank a live grade out from under its
        // own worker, per the design note on RecoverAbandonedSupervisorRunsAsync).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await SeedNonTerminalStopDecisionAsync(runId, teamId);
        await StampWorkerDeathSignatureAsync(runId); // backdates the WHOLE ledger + flips the run Running/stale

        // The heartbeat lands AFTER the backdate — exactly the timing a live grading loop produces (it ticks every
        // 90s throughout the grade, well after whatever ledger activity preceded it).
        await SeedFreshHeartbeatRecordAsync(runId, "sup");

        var summary = await ReconcileAsync();

        summary.RecoveredAbandonedSupervisorRun.ShouldBe(0, "nothing crashed — a live grading run must never be redispatched out from under its own worker");
        summary.MarkedAbandonedFromRunning.ShouldBe(0, "the fresh heartbeat proves the run is alive — it must not be failed");
        (await ReadStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Running, "untouched by either sweep");
    }

    // ─── Helpers (mirrors SupervisorSpawnFlowTests' established crash-recovery pattern) ─────────────────

    private sealed class CrashingDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated worker death — the process died mid-decide, before any claim");
    }

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

    /// <summary>Stamp the durable WORKER-DEATH signature: Status=Running, StartedAt + the latest ledger record backdated past the abandoned thresholds — the exact durable state a crashed pod leaves behind (mirrors SupervisorSpawnFlowTests).</summary>
    private async Task StampWorkerDeathSignatureAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var stale = DateTimeOffset.UtcNow - (StuckRunReconcilerService.RunningStuckAfter + TimeSpan.FromMinutes(5));

        await db.Database.ExecuteSqlRawAsync(
            "UPDATE workflow_run SET status = {0}, started_at = {1} WHERE id = {2}", WorkflowRunStatus.Running.ToString(), stale, runId);

        await db.Database.ExecuteSqlRawAsync("ALTER TABLE workflow_run_record DISABLE TRIGGER workflow_run_record_enforce_immutability");
        try
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE workflow_run_record SET occurred_at = {0} WHERE run_id = {1}", stale, runId);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE workflow_run_record ENABLE TRIGGER workflow_run_record_enforce_immutability");
        }
    }

    /// <summary>Seed a NON-TERMINAL "stop" decision — the exact ledger shape a run sits in for the whole duration of its acceptance grade (claimed, executing, not yet recorded terminal).</summary>
    private async Task SeedNonTerminalStopDecisionAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.SupervisorDecisionRecord.Add(new CodeSpace.Core.Persistence.Entities.SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            SupervisorRunId = runId,
            DecisionKind = SupervisorDecisionKinds.Stop,
            IdempotencyKey = $"stop:{Guid.NewGuid():N}",
            InputHash = Guid.NewGuid().ToString("N"),
            PayloadJson = "{}",
            Status = SupervisorDecisionStatus.Running,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>Insert one FRESH `log` ledger record — exactly the record RunGradingHeartbeatLoopAsync emits — proving the reconciler's liveness check reads it and excludes this run from every stale-run sweep.</summary>
    private async Task SeedFreshHeartbeatRecordAsync(Guid runId, string nodeId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.WorkflowRunRecord.Add(new CodeSpace.Core.Persistence.Entities.WorkflowRunRecord
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            RecordType = WorkflowRunRecordTypes.Log,
            NodeId = nodeId,
            IterationKey = string.Empty,
            PayloadJson = """{"level":"Info","message":"Supervisor stop acceptance grading is still in progress."}""",
            OccurredAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
    }

    private async Task StampNodeStartedAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var logger = scope.Resolve<CodeSpace.Core.Services.Workflows.Lifecycle.IRunRecordLogger>();

        await logger.NodeStartedAsync(runId, "sup", "", new Dictionary<string, System.Text.Json.JsonElement>(), new Dictionary<string, System.Text.Json.JsonElement>(), CancellationToken.None);
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "sup-preclaim-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = SupervisorDefinition(),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task<Guid> CreateNonSupervisorWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "non-sup-preclaim-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = NonSupervisorDefinition(),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    // manual → sup (agent.supervisor) → terminal
    private static WorkflowDefinition SupervisorDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "sup", TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json("""{"goal":"ship the feature"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "sup" },
            new() { From = "sup", To = "end" },
        },
    };

    // manual → sleep (flow.sleep, a genuine suspend-capable non-supervisor node) → terminal
    private static WorkflowDefinition NonSupervisorDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "wait", TypeKey = "flow.sleep", Config = WorkflowsTestSeed.Json("""{"seconds":999999}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "wait" },
            new() { From = "wait", To = "end" },
        },
    };
}
