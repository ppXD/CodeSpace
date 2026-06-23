using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Tasks.Phases.Sources.Supervisor;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Phases;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres + the REAL <see cref="SupervisorPhaseSource"/> resolved from DI): the C2 phase
/// projection end-to-end — a supervisor run whose plan authored semantic phases surfaces those phases through the SAME
/// run-phases source the tasks-phases / scorecard API already consumes (interface-first, no new endpoint, no UI). Seeds
/// a phased plan + a spawn + its real AgentRun rows, then asserts ContributeAsync (the DB-read half + the projection)
/// returns the per-decision tape PLUS the authored phases, each grouping the agents that ran its subtasks with their
/// GROUND-TRUTH status.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorPhaseProjectionFlowTests
{
    private readonly PostgresFixture _fixture;

    public SupervisorPhaseProjectionFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    private const string NodeId = "sup";

    [Fact]
    public async Task A_phased_plan_surfaces_its_semantic_phases_through_the_run_phase_source()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);

        var agentA = Guid.NewGuid();
        var agentB = Guid.NewGuid();
        await SeedAgentRunAsync(runId, teamId, agentA, AgentRunStatus.Succeeded);
        await SeedAgentRunAsync(runId, teamId, agentB, AgentRunStatus.Failed);

        // A plan that grouped sa → Implement, sb → Verify (with a per-phase acceptance) + the spawn that fanned [sa,sb] → [A,B].
        await SeedDecisionAsync(runId, teamId, 1, SupervisorDecisionKinds.Plan, "{}",
            """{"planned":[],"count":2,"phases":[{"id":"impl","title":"Implement","subtaskIds":["sa"]},{"id":"verify","title":"Verify","subtaskIds":["sb"],"acceptance":{"command":["sh","check.sh"]}}]}""");
        await SeedDecisionAsync(runId, teamId, 2, SupervisorDecisionKinds.Spawn, """{"subtaskIds":["sa","sb"]}""",
            $$"""{"agentCount":2,"agentRunIds":["{{agentA}}","{{agentB}}"]}""");

        IReadOnlyList<RunPhase> phases;
        using (var scope = _fixture.BeginScope())
            phases = await scope.Resolve<SupervisorPhaseSource>().ContributeAsync(new RunPhaseContext { RunId = runId, TeamId = teamId }, CancellationToken.None);

        var authored = phases.Where(p => p.Kind == "phase").ToList();
        authored.Select(p => p.Label).ShouldBe(new[] { "Implement", "Verify" }, "the supervisor's semantic phases surface through the existing run-phase source");

        var implement = authored.Single(p => p.Label == "Implement");
        implement.Agents.Single().AgentRunId.ShouldBe(agentA, "the Implement phase groups the agent that ran its subtask (read from real AgentRun rows)");
        implement.Status.ShouldBe(PhaseStatus.Succeeded, "agent A really succeeded → the phase succeeded (ground-truth, not a self-report)");

        var verify = authored.Single(p => p.Label == "Verify");
        verify.Agents.Single().AgentRunId.ShouldBe(agentB);
        verify.Status.ShouldBe(PhaseStatus.Failed, "agent B really failed → the phase failed");
        verify.Summary.ShouldBe("sh check.sh", "the phase's acceptance command surfaces for the board");
    }

    [Fact]
    public async Task A_flat_plan_run_surfaces_no_authored_phases()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);

        await SeedDecisionAsync(runId, teamId, 1, SupervisorDecisionKinds.Plan, "{}", """{"planned":[],"count":0}""");
        await SeedDecisionAsync(runId, teamId, 2, SupervisorDecisionKinds.Stop, "{}", null);

        using var scope = _fixture.BeginScope();
        var phases = await scope.Resolve<SupervisorPhaseSource>().ContributeAsync(new RunPhaseContext { RunId = runId, TeamId = teamId }, CancellationToken.None);

        phases.ShouldNotContain(p => p.Kind == "phase", "a flat plan adds no semantic phases — the board is the per-decision tape verbatim");
        phases.Select(p => p.Label).ShouldBe(new[] { "Plan", "Stop" });
    }

    [Fact]
    public async Task A_spawn_surfaces_each_agents_model_and_token_rollup_through_the_source()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);

        var agentA = Guid.NewGuid();
        await SeedAgentRunAsync(runId, teamId, agentA, AgentRunStatus.Succeeded);

        // The spawn outcome carries the folded agentResults compact (model + realized tokens) — exactly as the rehydrate writes it.
        var outcome = JsonSerializer.Serialize(new
        {
            agentCount = 1,
            agentRunIds = new[] { agentA.ToString() },
            agentResults = new[] { new SupervisorAgentResult { AgentRunId = agentA, Status = "Succeeded", Model = "claude-opus-4", InputTokens = 9000, OutputTokens = 2100 } },
        }, AgentJson.Options);

        await SeedDecisionAsync(runId, teamId, 1, SupervisorDecisionKinds.Spawn, """{"subtaskIds":["sa"]}""", outcome);

        IReadOnlyList<RunPhase> phases;
        using (var scope = _fixture.BeginScope())
            phases = await scope.Resolve<SupervisorPhaseSource>().ContributeAsync(new RunPhaseContext { RunId = runId, TeamId = teamId }, CancellationToken.None);

        var agent = phases.Single(p => p.Kind == SupervisorDecisionKinds.Spawn).Agents.Single();
        agent.AgentRunId.ShouldBe(agentA);
        agent.Model.ShouldBe("claude-opus-4", "the folded model survives the jsonb round-trip + ReadAgentResults");
        agent.InputTokens.ShouldBe(9000);
        agent.OutputTokens.ShouldBe(2100);
    }

    [Fact]
    public async Task A_spawn_surfaces_each_agents_duration_and_governed_tool_count_through_the_source()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);

        var started = DateTimeOffset.UtcNow.AddMinutes(-3);

        // Agent A: terminal, ran 85s; made 2 GOVERNED tool calls + 1 decision.request (a HITL ask — must NOT count).
        var agentA = Guid.NewGuid();
        await SeedAgentRunAsync(runId, teamId, agentA, AgentRunStatus.Succeeded, started, started.AddSeconds(85));
        await SeedToolCallAsync(teamId, agentA, "git.open_pr");
        await SeedToolCallAsync(teamId, agentA, "run_command");
        await SeedToolCallAsync(teamId, agentA, DecisionToolKinds.DecisionRequest);

        // Agent B: still running (no CompletedAt) → live elapsed > 0; made no tool calls → a real 0.
        var agentB = Guid.NewGuid();
        await SeedAgentRunAsync(runId, teamId, agentB, AgentRunStatus.Running, DateTimeOffset.UtcNow.AddSeconds(-30), completedAt: null);

        await SeedDecisionAsync(runId, teamId, 1, SupervisorDecisionKinds.Spawn, """{"subtaskIds":["sa","sb"]}""",
            $$"""{"agentCount":2,"agentRunIds":["{{agentA}}","{{agentB}}"]}""");

        IReadOnlyList<RunPhase> phases;
        using (var scope = _fixture.BeginScope())
            phases = await scope.Resolve<SupervisorPhaseSource>().ContributeAsync(new RunPhaseContext { RunId = runId, TeamId = teamId }, CancellationToken.None);

        var agents = phases.Single(p => p.Kind == SupervisorDecisionKinds.Spawn).Agents;

        var refA = agents.Single(a => a.AgentRunId == agentA);
        refA.DurationMs.ShouldBe(85_000, "a terminal agent's duration is CompletedAt − StartedAt, exact");
        refA.ToolCount.ShouldBe(2, "the 2 governed calls count; the decision.request envelope is excluded");

        var refB = agents.Single(a => a.AgentRunId == agentB);
        refB.DurationMs!.Value.ShouldBeGreaterThan(20_000, "a still-running agent carries live elapsed (now − StartedAt), not its final time");
        refB.ToolCount.ShouldBe(0, "no tool rows → a real 0, not null, for a supervisor agent");
    }

    // ─── Seeding ───

    private async Task<Guid> SeedRunAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Messages.Constants.Roles.Admin);
        var workflowId = await scope.Resolve<MediatR.IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "sup-phase-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = new Messages.Dtos.Workflows.WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<Messages.Dtos.Workflows.NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = NodeId, TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json("""{"goal":"ship"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                },
                Edges = new List<Messages.Dtos.Workflows.EdgeDefinition> { new() { From = "start", To = NodeId }, new() { From = NodeId, To = "end" } },
            },
            Activations = new List<Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });

        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    private async Task SeedAgentRunAsync(Guid runId, Guid teamId, Guid agentRunId, AgentRunStatus status, DateTimeOffset? startedAt = null, DateTimeOffset? completedAt = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.AgentRun.Add(new AgentRun
        {
            Id = agentRunId, TeamId = teamId, WorkflowRunId = runId, NodeId = NodeId, IterationKey = $"{NodeId}#turn1",
            Harness = "codex-cli", Status = status, TaskJson = "{}", StartedAt = startedAt, CompletedAt = completedAt,
            CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedToolCallAsync(Guid teamId, Guid agentRunId, string toolKind)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.ToolCallLedger.Add(new ToolCallLedger
        {
            Id = Guid.NewGuid(), TeamId = teamId, AgentRunId = agentRunId, ToolKind = toolKind,
            IdempotencyKey = $"{toolKind}:{Guid.NewGuid():N}", InputHash = new string('0', 64), Status = ToolCallLedgerStatus.Succeeded,
            FenceEpoch = 1, CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedDecisionAsync(Guid runId, Guid teamId, long sequence, string kind, string payloadJson, string? outcomeJson)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId, Sequence = sequence, DecisionKind = kind,
            IdempotencyKey = $"{kind}-{sequence}-{Guid.NewGuid():N}", InputHash = "test", Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = payloadJson, OutcomeJson = outcomeJson, FenceEpoch = 1,
            CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }
}
