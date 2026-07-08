using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Arbiter;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres + the REAL <see cref="SupervisorTurnService"/> + real
/// <see cref="Core.Services.Supervisor.Executors.RealSupervisorActionExecutor"/> merge): loopability slice 4
/// ("局部綠≠整合綠") end-to-end — a merge withholds a per-unit-REJECTED unit's branch. A prior spawn produced two
/// units, one of which FAILED its per-unit acceptance (slice 3); the merge turn folds ONLY the accepted unit's
/// contribution, and the rejected unit's branch never reaches the reviewable head. The withhold filter itself is
/// pinned in isolation by <c>SupervisorMergeWithholdTests</c>; this proves the wiring through the real executor +
/// the real AgentRun load over real Postgres.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorMergeWithholdFlowTests
{
    private readonly PostgresFixture _fixture;

    public SupervisorMergeWithholdFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    private const string NodeId = "sup";
    private const string Goal = "ship the feature";

    [Fact]
    public async Task A_merge_folds_only_the_accepted_unit_and_withholds_the_rejected_branch()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var acceptedId = Guid.NewGuid();
        var rejectedId = Guid.NewGuid();

        // A prior spawn whose folded agentResults carry the per-unit verdicts: accepted unit PASSED, rejected unit FAILED.
        await SeedSpawnAsync(runId, teamId, sequence: 1,
            Unit(acceptedId, "codespace/agent/accepted", acceptancePassed: true),
            Unit(rejectedId, "codespace/agent/rejected", acceptancePassed: false));

        // Both agents' real terminal rows exist (so the load would find EITHER) — proving the filter, not a missing row,
        // is what withholds the rejected one.
        await SeedAgentRunAsync(acceptedId, teamId, runId, "codespace/agent/accepted");
        await SeedAgentRunAsync(rejectedId, teamId, runId, "codespace/agent/rejected");

        var merge = await RunMergeTurnAsync(runId, teamId);

        var outcome = JsonDocument.Parse(merge!).RootElement;
        outcome.GetProperty("count").GetInt32().ShouldBe(1, "only the accepted unit is folded — the rejected one is withheld");

        var branches = outcome.GetProperty("merged").EnumerateArray()
            .Select(e => e.GetProperty("producedBranch").GetString()).ToList();
        branches.Count.ShouldBe(1, "the merged head carries ONLY the accepted unit — the rejected one never reaches it");
        branches[0].ShouldBe("codespace/agent/accepted", "the surviving branch is the accepted unit's");
    }

    [Fact]
    public async Task A_merge_of_an_all_ungraded_wave_folds_every_unit_byte_identical_to_pre_slice()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, sequence: 1, Unit(a, "codespace/agent/a", null), Unit(b, "codespace/agent/b", null));
        await SeedAgentRunAsync(a, teamId, runId, "codespace/agent/a");
        await SeedAgentRunAsync(b, teamId, runId, "codespace/agent/b");

        var merge = await RunMergeTurnAsync(runId, teamId);

        JsonDocument.Parse(merge!).RootElement.GetProperty("count").GetInt32()
            .ShouldBe(2, "no per-unit verdicts → every unit folds, exactly as before the slice");
    }

    // ─── Helpers ───

    private static SupervisorAgentResult Unit(Guid agentRunId, string producedBranch, bool? acceptancePassed) =>
        new() { AgentRunId = agentRunId, Status = "Succeeded", Summary = "did it", ProducedBranch = producedBranch, AcceptancePassed = acceptancePassed };

    private async Task<string?> RunMergeTurnAsync(Guid runId, Guid teamId)
    {
        using (var scope = _fixture.BeginScope())
        {
            var service = new SupervisorTurnService(
                scope.Resolve<ISupervisorDecisionLog>(),
                new MergeDecider(),
                scope.Resolve<ISupervisorActionExecutor>(),
                scope.Resolve<CodeSpaceDbContext>(),
                scope.Resolve<ISupervisorAcceptanceGrader>(),
                scope.Resolve<IDecisionQueueService>(),
                scope.Resolve<IDecisionArbiter>(),
                scope.Resolve<IDecisionAnswerService>(),
                scope.Resolve<CodeSpace.Core.Services.Plans.IWorkPlanService>(),
                scope.Resolve<CodeSpace.Core.Services.Workflows.Lifecycle.IRunRecordLogger>(), scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(), scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IPublishManifestStore>(), scope.Resolve<ILogger<SupervisorTurnService>>());

            await service.RunTurnAsync(runId, teamId, NodeId, Goal, conversationId: null, GoalConfig(), CancellationToken.None);
        }

        using var verify = _fixture.BeginScope();
        return await verify.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Merge)
            .Select(d => d.OutcomeJson)
            .SingleAsync();
    }

    private async Task SeedSpawnAsync(Guid runId, Guid teamId, int sequence, params SupervisorAgentResult[] units)
    {
        var ids = units.Select(u => u.AgentRunId).ToArray();
        var outcome = SupervisorOutcome.FoldAgentResults(
            JsonSerializer.Serialize(new { agentRunIds = ids, agentCount = ids.Length }, AgentJson.Options), units);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId, Sequence = sequence,
            DecisionKind = SupervisorDecisionKinds.Spawn, IdempotencyKey = $"spawn-{Guid.NewGuid():N}", InputHash = "test",
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = """{"subtaskIds":["s1","s2"]}""", OutcomeJson = outcome,
            FenceEpoch = 1, CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedAgentRunAsync(Guid agentRunId, Guid teamId, Guid runId, string producedBranch)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var resultJson = JsonSerializer.Serialize(new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = "did it",
            ChangedFiles = new[] { "a.cs" }, ProducedBranch = producedBranch,
        }, AgentJson.Options);

        db.AgentRun.Add(new AgentRun
        {
            Id = agentRunId, TeamId = teamId, WorkflowRunId = runId, NodeId = NodeId, Harness = "codex-cli",
            Status = AgentRunStatus.Succeeded, TaskJson = "{}", ResultJson = resultJson,
        });
        await db.SaveChangesAsync();
    }

    private static SupervisorGoalConfig GoalConfig() => new() { Goal = Goal, AgentProfile = new SupervisorAgentProfile { RepositoryId = Guid.NewGuid() } };

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
            Name = "sup-merge-withhold-" + Guid.NewGuid().ToString("N")[..6],
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

    /// <summary>A decider that emits a single MERGE decision — drives the real merge executor over the seeded prior spawn.</summary>
    private sealed class MergeDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new SupervisorDecision
            {
                Kind = SupervisorDecisionKinds.Merge,
                PayloadJson = JsonSerializer.Serialize(new SupervisorMergePayload { SynthesisInstruction = "combine" }, AgentJson.Options),
            });
    }
}
