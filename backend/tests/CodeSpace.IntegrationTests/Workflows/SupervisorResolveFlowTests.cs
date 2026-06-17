using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Resolver loop (#379, S2) — the DETERMINISTIC resolve crown jewel, driven against the REAL
/// <see cref="Core.Services.Supervisor.Executors.RealSupervisorActionExecutor"/> from DI over REAL Postgres. Given a
/// recorded conflicted-merge + the prior spawn's agent branches on the decision tape, the <c>resolve</c> verb must
/// stage EXACTLY ONE real resolver <see cref="AgentRun"/> whose persisted <c>TaskJson</c> goal was assembled
/// deterministically by <see cref="SupervisorResolverRecipe"/> from that durable data — the model authored NOTHING
/// (fork #2). And the three fail-safe paths (no conflict / no repo / no branches) must be a no-op that stages no
/// agent — the loop only ever ADDS an attempt, never strands the run.
///
/// <para>Fidelity: real executor + real <c>AgentRunService</c> staging through real Postgres. The agent does not
/// EXECUTE here (that is the proven spawn→real-agent pipeline, covered by <see cref="SupervisorRealAgentE2ETests"/>);
/// this isolates the deterministic SYNTHESIS — the new logic — at the persistence tier.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorResolveFlowTests
{
    private const string NodeId = "sup";
    private const string Goal = "ship the coordinated feature";

    private readonly PostgresFixture _fixture;

    public SupervisorResolveFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Resolve_stages_one_resolver_agent_whose_goal_names_every_branch_and_the_conflicted_files()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var context = ContextWith(runId, teamId,
            repositoryId: Guid.NewGuid(),
            spawn: SpawnWithBranches("codespace/agent/web", "codespace/agent/api"),
            merge: ConflictedMerge("src/Shared.cs"));

        await ExecuteResolveAsync(context);

        var staged = await StagedAgentRunsAsync(runId);
        staged.Count.ShouldBe(1, "resolve stages exactly ONE resolver agent (the K=1 spawn shape)");

        var task = JsonSerializer.Deserialize<AgentTask>(staged[0].TaskJson, AgentJson.Options)!;
        task.Goal.ShouldContain("codespace/agent/web");
        task.Goal.ShouldContain("codespace/agent/api", customMessage: "the resolver's goal names EVERY branch to reconcile — assembled from the spawn's agentResults, not the model");
        task.Goal.ShouldContain("src/Shared.cs", customMessage: "the conflicted file (from the merge's integration block) is named");
        task.Goal.ShouldContain(SupervisorResolverRecipe.TestsPassedMarker);
        task.Goal.ShouldContain(Goal, Case.Insensitive);
        task.PushProducedBranch.ShouldBe(true, "the resolver MUST push its reconciled branch so a downstream PR-open has a head");
    }

    [Theory]
    [InlineData("no-conflict")]   // a clean merge on the tape → nothing to resolve
    [InlineData("no-repo")]       // conflict present but no repository bound
    [InlineData("no-branches")]   // conflict + repo but the agents produced no branches
    public async Task Resolve_is_a_no_op_that_stages_no_agent_when_there_is_nothing_to_resolve(string shape)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var context = ContextWith(runId, teamId,
            repositoryId: shape == "no-repo" ? null : Guid.NewGuid(),
            spawn: shape == "no-branches" ? SpawnWithBranches() : SpawnWithBranches("codespace/agent/web", "codespace/agent/api"),
            merge: shape == "no-conflict" ? CleanMerge() : ConflictedMerge("src/Shared.cs"));

        await ExecuteResolveAsync(context);

        (await StagedAgentRunsAsync(runId)).ShouldBeEmpty($"the '{shape}' fail-safe must stage no agent — resolve is a clean no-op, never a stranded run");
    }

    // ─── Drive the real executor ───────────────────────────────────────────────────

    private async Task ExecuteResolveAsync(SupervisorTurnContext context)
    {
        using var scope = _fixture.BeginScope();
        var executor = scope.Resolve<ISupervisorActionExecutor>();

        var decision = new SupervisorDecision { Kind = SupervisorDecisionKinds.Resolve, PayloadJson = "{}" };

        await executor.ExecuteAsync(decision, context, CancellationToken.None);
    }

    private async Task<IReadOnlyList<AgentRun>> StagedAgentRunsAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking()
            .Where(r => r.WorkflowRunId == runId && r.NodeId == NodeId)
            .ToListAsync();
    }

    private static SupervisorTurnContext ContextWith(Guid runId, Guid teamId, Guid? repositoryId, SupervisorPriorDecision spawn, SupervisorPriorDecision merge) => new()
    {
        Goal = Goal,
        SupervisorRunId = runId,
        TeamId = teamId,
        NodeId = NodeId,
        TurnNumber = 2,
        PriorDecisions = new[] { spawn, merge },
        AgentProfile = repositoryId is null ? null : new SupervisorAgentProfile { RepositoryId = repositoryId },
    };

    /// <summary>A prior spawn decision whose folded agentResults carry the given produced branches (the resolver's re-merge set) — the shape SupervisorOutcome.FoldAgentResults persists.</summary>
    private static SupervisorPriorDecision SpawnWithBranches(params string[] branches)
    {
        var results = branches.Select(b => new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = "Succeeded", ProducedBranch = b }).ToArray();
        var ids = results.Select(r => r.AgentRunId).ToArray();

        return new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 1, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = "{}",
            OutcomeJson = JsonSerializer.Serialize(new { agentRunIds = ids, agentCount = ids.Length, agentResults = results }, AgentJson.Options),
        };
    }

    private static SupervisorPriorDecision ConflictedMerge(params string[] conflictedFiles) => MergeDecision(JsonSerializer.Serialize(new
    {
        integration = new
        {
            status = "Conflicted",
            integratedBranch = (string?)null,
            reason = "a contribution conflicted while integrating",
            outcomes = new[] { new { label = "agent-api", disposition = "Conflicted", conflictedFiles, fallbackBranch = "codespace/agent/api" } },
        },
    }, AgentJson.Options));

    private static SupervisorPriorDecision CleanMerge() => MergeDecision(JsonSerializer.Serialize(new
    {
        integration = new { status = "Clean", integratedBranch = "codespace/integration/x", outcomes = Array.Empty<object>() },
    }, AgentJson.Options));

    private static SupervisorPriorDecision MergeDecision(string outcomeJson) => new()
    {
        Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Merge, Status = SupervisorDecisionStatus.Succeeded,
        PayloadJson = "{}", OutcomeJson = outcomeJson,
    };

    // ─── Seed ──────────────────────────────────────────────────────────────────────

    private async Task<Guid> SeedSupervisorRunAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var workflowId = await scope.Resolve<MediatR.IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "sup-resolve-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = new Messages.Dtos.Workflows.WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<Messages.Dtos.Workflows.NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = NodeId, TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json("""{"goal":"ship the coordinated feature"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                },
                Edges = new List<Messages.Dtos.Workflows.EdgeDefinition> { new() { From = "start", To = NodeId }, new() { From = NodeId, To = "end" } },
            },
            Activations = new List<Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });

        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }
}
