using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Arbiter;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres + the REAL <see cref="SupervisorTurnService"/> +
/// <see cref="Core.Services.Supervisor.Executors.RealSupervisorActionExecutor"/> spawn staging): the dependency rail
/// END-TO-END — the server CLAMPS a spawn to its ready frontier. A spawn over a plan with <c>DependsOn</c> stages an
/// agent ONLY for the subtasks whose dependencies are satisfied; a dependent is deferred until its dependency is an
/// accepted success, then staged on a later turn; a flat plan stages every requested subtask (byte-identical). The
/// gate's decision logic is pinned exhaustively in <c>SupervisorDependencyGateTests</c>; this proves the executor
/// actually clamps the real fan-out over real Postgres.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorDependencyOrderingFlowTests
{
    private readonly PostgresFixture _fixture;

    public SupervisorDependencyOrderingFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    private const string NodeId = "sup";
    private const string Goal = "ship the feature";

    [Fact]
    public async Task A_spawn_is_clamped_to_the_subtask_whose_dependency_is_not_yet_satisfied()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        // Plan: a (no deps), b depends on a. a has NOT run yet → b must be deferred.
        await SeedPlanAsync(runId, teamId, sequence: 1, ("a", null, "implement-a"), ("b", new[] { "a" }, "implement-b"));

        // The model proposes spawning BOTH; the server admits only the ready one (a).
        await RunSpawnTurnAsync(runId, teamId, "a", "b");

        var staged = await StagedAgentTasksAsync(runId, teamId);
        staged.Count.ShouldBe(1, "only the ready subtask (a) is staged — b is deferred until a is accepted");
        staged.Single().ShouldContain("implement-a", Case.Insensitive, "the staged agent is a, not the blocked b");
    }

    [Fact]
    public async Task A_dependent_subtask_is_staged_once_its_dependency_is_an_accepted_success()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedPlanAsync(runId, teamId, sequence: 1, ("a", null, "implement-a"), ("b", new[] { "a" }, "implement-b"));
        // A prior spawn already ran a to a non-rejected success → a is now satisfied.
        await SeedPriorSpawnAsync(runId, teamId, sequence: 2, "a");

        await RunSpawnTurnAsync(runId, teamId, "b");

        var staged = await StagedAgentTasksAsync(runId, teamId);
        staged.Count.ShouldBe(1, "b's dependency a is an accepted success → b is now ready and staged");
        staged.Single().ShouldContain("implement-b", Case.Insensitive);
    }

    [Fact]
    public async Task A_non_trailing_deferred_subtask_is_dropped_from_the_persisted_payload_keeping_the_join_aligned()
    {
        // The regression for the positional-join blocker: the model spawns [a, b, c] where the MIDDLE one (b) depends on
        // the not-yet-done a. The clamp must drop b from the PERSISTED payload (not just the staged set), so the recorded
        // subtaskIds match the recorded agents one-for-one — otherwise a later turn's positional subtaskIds[i] <-> results[i]
        // fold would credit the never-run b with c's result.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedPlanAsync(runId, teamId, sequence: 1, ("a", null, "implement-a"), ("b", new[] { "a" }, "implement-b"), ("c", null, "implement-c"));

        await RunSpawnTurnAsync(runId, teamId, "a", "b", "c");

        var payload = JsonDocument.Parse(await SpawnPayloadAsync(runId, teamId)).RootElement;
        payload.GetProperty("subtaskIds").EnumerateArray().Select(e => e.GetString()).ShouldBe(new[] { "a", "c" },
            customMessage: "the deferred non-trailing b is dropped from the persisted payload — subtaskIds now align with the staged agents");

        var staged = await StagedAgentTasksAsync(runId, teamId);
        staged.Count.ShouldBe(2);
        staged.Any(t => t.Contains("implement-b")).ShouldBeFalse("b was deferred and never staged");
    }

    [Fact]
    public async Task A_flat_plan_stages_every_requested_subtask()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedPlanAsync(runId, teamId, sequence: 1, ("a", null, "implement-a"), ("b", null, "implement-b"));

        await RunSpawnTurnAsync(runId, teamId, "a", "b");

        (await StagedAgentTasksAsync(runId, teamId)).Count.ShouldBe(2, "no DependsOn → every requested subtask stages, byte-identical to before the rail");
    }

    [Fact]
    public async Task A_clamped_spawn_preserves_the_model_authored_rationale_on_the_persisted_payload()
    {
        // The genericity regression (adversarial-review CONFIRMED): a spawn the model authored WITH a decision-level
        // rationale, clamped to its ready frontier, must still carry the "why" on the persisted tape — the room/journal
        // reads it off the CLAMPED row. Before the fix the clamp rebuilt the typed spawn payload and silently dropped it.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedPlanAsync(runId, teamId, sequence: 1, ("a", null, "implement-a"), ("b", new[] { "a" }, "implement-b"));

        var rationale = new SupervisorRationale { Why = "fan out the ready frontier now", Evidence = "a has no deps; b waits on a" };
        await RunSpawnTurnAsync(runId, teamId, rationale, "a", "b");

        var payload = await SpawnPayloadAsync(runId, teamId);

        JsonDocument.Parse(payload).RootElement.GetProperty("subtaskIds").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(new[] { "a" }, "b deferred → the spawn clamped to the ready frontier");
        SupervisorOutcome.ReadRationale(payload)
            .ShouldBe(("fan out the ready frontier now", "a has no deps; b waits on a"), "the clamped spawn kept the model's why on the persisted tape");
    }

    // ─── Helpers ───

    private Task RunSpawnTurnAsync(Guid runId, Guid teamId, params string[] subtaskIds) =>
        RunSpawnTurnAsync(runId, teamId, rationale: null, subtaskIds);

    private async Task RunSpawnTurnAsync(Guid runId, Guid teamId, SupervisorRationale? rationale, params string[] subtaskIds)
    {
        using var scope = _fixture.BeginScope();
        var service = new SupervisorTurnService(
            scope.Resolve<ISupervisorDecisionLog>(),
            new SpawnDecider(subtaskIds, rationale),
            scope.Resolve<ISupervisorActionExecutor>(),
            scope.Resolve<CodeSpaceDbContext>(),
            scope.Resolve<ISupervisorAcceptanceGrader>(),
            scope.Resolve<IDecisionQueueService>(),
            scope.Resolve<IDecisionArbiter>(),
            scope.Resolve<IDecisionAnswerService>(),
            scope.Resolve<CodeSpace.Core.Services.Plans.IWorkPlanService>(),
            scope.Resolve<CodeSpace.Core.Services.Workflows.Lifecycle.IRunRecordLogger>(), scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(), scope.Resolve<ILogger<SupervisorTurnService>>());

        await service.RunTurnAsync(runId, teamId, NodeId, Goal, conversationId: null, goalConfig: null, CancellationToken.None);
    }

    private async Task<IReadOnlyList<string>> StagedAgentTasksAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking()
            .Where(r => r.WorkflowRunId == runId && r.TeamId == teamId)
            .Select(r => r.TaskJson!)
            .ToListAsync();
    }

    /// <summary>The persisted payload of the run's spawn decision (the one the model authored this turn) — to assert the clamp narrowed it.</summary>
    private async Task<string> SpawnPayloadAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Spawn && d.Sequence > 1)
            .Select(d => d.PayloadJson)
            .SingleAsync();
    }

    private async Task SeedPlanAsync(Guid runId, Guid teamId, int sequence, params (string Id, string[]? DependsOn, string Instruction)[] subtasks)
    {
        var payload = JsonSerializer.Serialize(new
        {
            goal = Goal,
            subtasks = subtasks.Select(s => s.DependsOn is null
                ? (object)new { id = s.Id, title = s.Id, instruction = s.Instruction }
                : new { id = s.Id, title = s.Id, instruction = s.Instruction, dependsOn = s.DependsOn }),
        }, AgentJson.Options);

        await SeedDecisionAsync(runId, teamId, sequence, SupervisorDecisionKinds.Plan, payload, "{}");
    }

    private async Task SeedPriorSpawnAsync(Guid runId, Guid teamId, int sequence, string subtaskId)
    {
        var result = new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = "Succeeded", ProducedBranch = "b" };
        var outcome = SupervisorOutcome.FoldAgentResults(
            JsonSerializer.Serialize(new { agentRunIds = new[] { result.AgentRunId }, agentCount = 1 }, AgentJson.Options), new[] { result });

        await SeedDecisionAsync(runId, teamId, sequence, SupervisorDecisionKinds.Spawn, $$"""{"subtaskIds":["{{subtaskId}}"]}""", outcome);
    }

    private async Task SeedDecisionAsync(Guid runId, Guid teamId, int sequence, string kind, string payloadJson, string outcomeJson)
    {
        using var scope = _fixture.BeginScope();
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

    private async Task<Guid> SeedSupervisorRunAsync(Guid teamId, Guid userId)
    {
        var workflowId = await CreateSupervisorWorkflowAsync(teamId, userId);
        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    private async Task<Guid> CreateSupervisorWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Messages.Constants.Roles.Admin);
        return await scope.Resolve<MediatR.IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "sup-deporder-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = new Messages.Dtos.Workflows.WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<Messages.Dtos.Workflows.NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = NodeId, TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json("""{"goal":"ship the feature"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                },
                Edges = new List<Messages.Dtos.Workflows.EdgeDefinition>
                {
                    new() { From = "start", To = NodeId },
                    new() { From = NodeId, To = "end" },
                },
            },
            Activations = new List<Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    /// <summary>A decider that emits a single SPAWN over the given subtask ids (optionally with a decision-level rationale) — built the SAME way production does (via the projector), so the clamp sees a faithful root-rationale payload. Drives the real spawn executor + its dependency clamp.</summary>
    private sealed class SpawnDecider : ISupervisorDecider
    {
        private readonly string[] _subtaskIds;
        private readonly SupervisorRationale? _rationale;

        public SpawnDecider(string[] subtaskIds, SupervisorRationale? rationale = null)
        {
            _subtaskIds = subtaskIds;
            _rationale = rationale;
        }

        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken) =>
            Task.FromResult(SupervisorDecisionProjector.Project(new SupervisorModelDecision
            {
                Kind = SupervisorDecisionKinds.Spawn,
                Rationale = _rationale,
                Spawn = new SupervisorSpawnPayload { SubtaskIds = _subtaskIds },
            }));
    }
}
