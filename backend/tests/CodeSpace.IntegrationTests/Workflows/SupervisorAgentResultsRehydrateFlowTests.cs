using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 SOTA #2 (supervisor-sees-its-agents) — the rehydrate-fold proof over REAL Postgres + the REAL
/// <see cref="SupervisorTurnService"/>.<c>RehydrateFromDecisionLogAsync</c> (the same method the turn loop calls).
/// Seeds a terminal <c>spawn</c> decision + its spawned <see cref="AgentRun"/> rows directly (one Succeeded with a
/// result, one Cancelled with a ROW error + NULL result — the abandoned-agent shape the slice most needs to
/// surface), then asserts the rehydrate folds each agent's COMPACT result into the spawn decision's outcome so the
/// decider can perceive it.
///
/// <para>Crown jewels: a Failed/Cancelled agent whose ResultJson is null still surfaces its ROW error; the fold is
/// ADDITIVE (agentCount byte-intact → the E5 spawn-cap counter unperturbed); it is PERSISTED once + idempotent on
/// re-rehydrate; a NON-terminal spawn row is NEVER folded; and a real decider prompt built from the rehydrated
/// context contains each agent's status + summary + error.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorAgentResultsRehydrateFlowTests
{
    private readonly PostgresFixture _fixture;

    public SupervisorAgentResultsRehydrateFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    private const string NodeId = "sup";
    private const string Goal = "ship the feature";

    [Fact]
    public async Task Rehydrate_folds_each_spawned_agents_compact_result_into_the_terminal_spawn_outcome()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var okId = Guid.NewGuid();
        var cancelledId = Guid.NewGuid();
        await SeedAgentRunAsync(runId, teamId, okId, AgentRunStatus.Succeeded, rowError: null,
            resultJson: ResultJson(summary: "added the endpoint", changedFiles: new[] { "Api/Foo.cs" }, producedBranch: "codespace/agent/ok"));
        // The abandoned-agent shape: a ROW error, NO ResultJson — the signal the decider most needs.
        await SeedAgentRunAsync(runId, teamId, cancelledId, AgentRunStatus.Cancelled, rowError: "lease expired mid-run", resultJson: null);

        await SeedSpawnDecisionAsync(runId, teamId, sequence: 1, SupervisorDecisionStatus.Succeeded, SpawnOutcome(okId, cancelledId));

        var context = await RehydrateAsync(runId, teamId);

        var spawn = context.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn);
        var results = SupervisorOutcome.ReadAgentResults(spawn.OutcomeJson);
        results.Count.ShouldBe(2, "both spawned agents are folded into the spawn outcome");

        var ok = results.Single(r => r.AgentRunId == okId);
        ok.Status.ShouldBe("Succeeded");
        ok.Summary.ShouldBe("added the endpoint");
        ok.ChangedFiles.ShouldBe(new[] { "Api/Foo.cs" });

        var cancelled = results.Single(r => r.AgentRunId == cancelledId);
        cancelled.Status.ShouldBe("Cancelled");
        cancelled.Error.ShouldBe("lease expired mid-run", "an abandoned agent with NULL ResultJson still surfaces its ROW error");

        // ADDITIVE: agentCount stays byte-intact so the E5 spawn-cap / no-progress counters are unperturbed.
        SupervisorOutcome.ReadStagedAgentCount(spawn.OutcomeJson).ShouldBe(2);
        SupervisorOutcome.ReadStagedAgentRunIds(spawn.OutcomeJson).ShouldBe(new[] { okId, cancelledId });

        // The real decider PROMPT built from the rehydrated context surfaces the work products + the failure.
        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(context);
        prompt.ShouldContain("added the endpoint", Case.Insensitive);
        prompt.ShouldContain("lease expired mid-run", Case.Insensitive, "the decider sees the abandoned agent's failure — the retry signal");
    }

    [Fact]
    public async Task Rehydrate_persists_the_fold_once_and_is_idempotent_on_re_rehydrate()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var agentId = Guid.NewGuid();
        await SeedAgentRunAsync(runId, teamId, agentId, AgentRunStatus.Succeeded, rowError: null, resultJson: ResultJson(summary: "done"));
        var bareOutcome = SpawnOutcome(agentId);
        await SeedSpawnDecisionAsync(runId, teamId, sequence: 1, SupervisorDecisionStatus.Succeeded, bareOutcome);

        await RehydrateAsync(runId, teamId);

        // The fold is PERSISTED onto the durable ledger row (survives restart without re-resolving).
        var afterFirst = await LedgerOutcomeAsync(runId, teamId, SupervisorDecisionKinds.Spawn);
        SupervisorOutcome.ReadAgentResults(afterFirst).Count.ShouldBe(1, "the agent result was folded + persisted onto the ledger row");
        SupervisorOutcome.ReadStagedAgentCount(afterFirst).ShouldBe(1, "agentCount stays intact through the persisted fold");

        // Re-rehydrate → the stored outcome converges to identical bytes (idempotent — same terminal facts re-fold
        // to the same value; jsonb-normalized read-back is stable across rehydrates).
        await RehydrateAsync(runId, teamId);
        (await LedgerOutcomeAsync(runId, teamId, SupervisorDecisionKinds.Spawn)).ShouldBe(afterFirst, "re-rehydrate re-folds to identical stored bytes — converges, no drift");
    }

    [Fact]
    public async Task Rehydrate_does_NOT_fold_a_non_terminal_spawn_row()
    {
        // A Running spawn row carrying agentRunIds (the re-park shape) must NOT be rewritten here — it would be
        // clobbered by the later RecordTerminalAsync. The fold is scoped to TERMINAL rows only.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var agentId = Guid.NewGuid();
        await SeedAgentRunAsync(runId, teamId, agentId, AgentRunStatus.Succeeded, rowError: null, resultJson: ResultJson(summary: "done"));
        var bareOutcome = SpawnOutcome(agentId);
        await SeedSpawnDecisionAsync(runId, teamId, sequence: 1, SupervisorDecisionStatus.Running, bareOutcome);

        var context = await RehydrateAsync(runId, teamId);

        context.InFlight.ShouldNotBeNull("a Running spawn is the in-flight decision");
        SupervisorOutcome.ReadAgentResults(context.InFlight!.OutcomeJson).ShouldBeEmpty("a non-terminal spawn is NOT folded in-memory");
        // The durable row was never rewritten with an agent-results fold (it would be clobbered by RecordTerminalAsync).
        SupervisorOutcome.ReadAgentResults(await LedgerOutcomeAsync(runId, teamId, SupervisorDecisionKinds.Spawn))
            .ShouldBeEmpty("the non-terminal ledger row carries no folded agentResults");
    }

    // ─── Seeding + helpers ─────────────────────────────────────────────────────────────

    private async Task<Guid> SeedSupervisorRunAsync(Guid teamId, Guid userId)
    {
        // A real WorkflowRun id satisfies the FKs the ledger + agent rows reference; the rehydrate reads the tape,
        // not the run shape, so a bare manual run is sufficient anchoring.
        var workflowId = await CreateSupervisorWorkflowAsync(teamId, userId);
        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    private async Task<Guid> CreateSupervisorWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<MediatR.IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "sup-rehydrate-" + Guid.NewGuid().ToString("N")[..6],
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

    private async Task SeedAgentRunAsync(Guid runId, Guid teamId, Guid agentRunId, AgentRunStatus status, string? rowError, string? resultJson)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.AgentRun.Add(new AgentRun
        {
            Id = agentRunId,
            TeamId = teamId,
            WorkflowRunId = runId,
            NodeId = NodeId,
            IterationKey = $"{NodeId}#turn0",
            Harness = "codex-cli",
            Status = status,
            Error = rowError,
            TaskJson = "{}",
            ResultJson = resultJson,
            CreatedDate = now,
            CreatedBy = Guid.Empty,
            LastModifiedDate = now,
            LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedSpawnDecisionAsync(Guid runId, Guid teamId, long sequence, SupervisorDecisionStatus status, string outcomeJson)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            SupervisorRunId = runId,
            Sequence = sequence,
            DecisionKind = SupervisorDecisionKinds.Spawn,
            IdempotencyKey = $"spawn-{sequence}-{Guid.NewGuid():N}",
            InputHash = "test",
            Status = status,
            PayloadJson = """{"subtaskIds":["s1","s2"]}""",
            OutcomeJson = outcomeJson,
            FenceEpoch = 1,
            CreatedDate = now,
            CreatedBy = Guid.Empty,
            LastModifiedDate = now,
            LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    private async Task<SupervisorTurnContext> RehydrateAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ISupervisorTurnService>().RehydrateFromDecisionLogAsync(runId, teamId, NodeId, Goal, goalConfig: null, CancellationToken.None);
    }

    private async Task<string?> LedgerOutcomeAsync(Guid runId, Guid teamId, string kind)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == kind)
            .Select(d => d.OutcomeJson)
            .SingleAsync();
    }

    private static string SpawnOutcome(params Guid[] agentRunIds) =>
        JsonSerializer.Serialize(new { agentRunIds, agentCount = agentRunIds.Length }, AgentJson.Options);

    private static string ResultJson(string? summary = null, string[]? changedFiles = null, string? producedBranch = null) =>
        JsonSerializer.Serialize(new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded,
            ExitReason = "completed",
            Summary = summary,
            ChangedFiles = changedFiles ?? Array.Empty<string>(),
            ProducedBranch = producedBranch,
        }, AgentJson.Options);
}
