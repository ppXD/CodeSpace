using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 HIGH fidelity (Rule 12): A2 (P4-2) tier escalation on retry, driven through the REAL <see cref="RealSupervisorActionExecutor"/>'s
/// <c>ExecuteRetryAsync</c> against real Postgres — the run's OWN evidence (a self-report/acceptance-grade
/// contradiction, or the run one no-progress decision away from its force-stop cap) raises the retry's model floor
/// above the prior attempt's own effective tier, recorded on the retry's outcome for the next turn to see.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorRetryEscalationFlowTests
{
    private const string NodeId = "sup";
    private const string Goal = "ship the retried feature";

    private readonly PostgresFixture _fixture;

    public SupervisorRetryEscalationFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_retry_following_a_contradiction_escalates_to_the_strongest_available_model()
    {
        var teamId = await SeedTeamAsync();
        var credentialId = await SeedCredentialAsync(teamId);
        await SeedModelAsync(credentialId, "claude-haiku-4-5", ModelCapabilityTier.Basic);
        await SeedModelAsync(credentialId, "claude-sonnet-4-5", ModelCapabilityTier.Strong);
        var runId = await SeedSupervisorRunAsync(teamId);

        var context = Context(runId, teamId,
            Plan("s1"),
            SpawnResult(2, "s1", Guid.NewGuid(), contradiction: "over_claim", model: "claude-haiku-4-5"));

        var (task, outcomeJson) = await ExecuteRetryAsync(context, "s1");

        task.Model.ShouldBe("claude-sonnet-4-5", "the contradiction raises the floor above the prior model's Basic tier");

        var escalation = SupervisorOutcome.ReadEscalation(outcomeJson);
        escalation.ShouldNotBeNull();
        escalation!.From.ShouldBe("claude-haiku-4-5");
        escalation.To.ShouldBe("claude-sonnet-4-5");
        escalation.Reason.ShouldContain("over_claim");
    }

    [Fact]
    public async Task A_retry_one_decision_away_from_the_no_progress_cap_escalates_even_without_a_contradiction()
    {
        var teamId = await SeedTeamAsync();
        var credentialId = await SeedCredentialAsync(teamId);
        await SeedModelAsync(credentialId, "claude-haiku-4-5", ModelCapabilityTier.Basic);
        await SeedModelAsync(credentialId, "claude-sonnet-4-5", ModelCapabilityTier.Strong);
        var runId = await SeedSupervisorRunAsync(teamId);

        var context = Context(runId, teamId,
            Plan("s1"),
            SpawnResult(2, "s1", Guid.NewGuid(), contradiction: null, model: "claude-haiku-4-5"))
            with
        { NoProgressDecisions = 7, MaxNoProgressDecisions = 8 };

        var (task, outcomeJson) = await ExecuteRetryAsync(context, "s1");

        task.Model.ShouldBe("claude-sonnet-4-5", "one no-progress decision away from the force-stop cap escalates even with no contradiction");
        SupervisorOutcome.ReadEscalation(outcomeJson)!.Reason.ShouldContain("no-progress cap");
    }

    [Fact]
    public async Task An_ordinary_retry_with_no_trigger_never_escalates()
    {
        var teamId = await SeedTeamAsync();
        var credentialId = await SeedCredentialAsync(teamId);
        await SeedModelAsync(credentialId, "claude-haiku-4-5", ModelCapabilityTier.Basic);
        await SeedModelAsync(credentialId, "claude-sonnet-4-5", ModelCapabilityTier.Strong);
        var runId = await SeedSupervisorRunAsync(teamId);

        var context = Context(runId, teamId,
            Plan("s1"),
            SpawnResult(2, "s1", Guid.NewGuid(), contradiction: null, model: "claude-haiku-4-5"))
            with
        { NoProgressDecisions = 1, MaxNoProgressDecisions = 8 };

        var (task, outcomeJson) = await ExecuteRetryAsync(context, "s1");

        task.Model.ShouldBeNull("no profile model authored and no escalation — the harness default stands, byte-identical to pre-A2");
        SupervisorOutcome.ReadEscalation(outcomeJson).ShouldBeNull();
    }

    [Fact]
    public async Task A_run_already_over_its_cost_cap_never_attempts_to_escalate()
    {
        var teamId = await SeedTeamAsync();
        var credentialId = await SeedCredentialAsync(teamId);
        await SeedModelAsync(credentialId, "claude-haiku-4-5", ModelCapabilityTier.Basic);
        await SeedModelAsync(credentialId, "claude-sonnet-4-5", ModelCapabilityTier.Strong);
        var runId = await SeedSupervisorRunAsync(teamId);

        var context = Context(runId, teamId,
            Plan("s1"),
            SpawnResult(2, "s1", Guid.NewGuid(), contradiction: "over_claim", model: "claude-haiku-4-5"))
            with
        { MaxCostUsd = 5m, RunSpendUsd = 5.01m };

        var (_, outcomeJson) = await ExecuteRetryAsync(context, "s1");

        SupervisorOutcome.ReadEscalation(outcomeJson).ShouldBeNull("a run already over its cost cap must never spend into escalating — it's about to force-stop anyway");
    }

    [Fact]
    public async Task An_operators_isdefault_pin_wins_over_a_higher_tier_candidate_even_while_escalating()
    {
        var teamId = await SeedTeamAsync();
        var credentialId = await SeedCredentialAsync(teamId);
        await SeedModelAsync(credentialId, "claude-haiku-4-5", ModelCapabilityTier.Basic);
        await SeedModelAsync(credentialId, "claude-sonnet-4-5", ModelCapabilityTier.Strong, isDefault: true);
        await SeedModelAsync(credentialId, "claude-opus-4-8", ModelCapabilityTier.Frontier);
        var runId = await SeedSupervisorRunAsync(teamId);

        var context = Context(runId, teamId,
            Plan("s1"),
            SpawnResult(2, "s1", Guid.NewGuid(), contradiction: "over_claim", model: "claude-haiku-4-5"));

        var (task, _) = await ExecuteRetryAsync(context, "s1");

        task.Model.ShouldBe("claude-sonnet-4-5", "the operator's IsDefault star wins over the higher-tier Frontier candidate — the SAME precedence AgentPlaneModelRanking.Rank gives an unpinned auto-pick");
    }

    [Fact]
    public async Task A_stronger_model_outside_the_allowed_pool_is_never_picked()
    {
        var teamId = await SeedTeamAsync();
        var credentialId = await SeedCredentialAsync(teamId);
        var haikuId = await SeedModelAsync(credentialId, "claude-haiku-4-5", ModelCapabilityTier.Basic);
        await SeedModelAsync(credentialId, "claude-opus-4-8", ModelCapabilityTier.Frontier);   // NOT in the allowed pool below
        var runId = await SeedSupervisorRunAsync(teamId);

        var context = Context(runId, teamId,
            Plan("s1"),
            SpawnResult(2, "s1", Guid.NewGuid(), contradiction: "over_claim", model: "claude-haiku-4-5"))
            with
        { AllowedModelIds = new[] { haikuId } };

        var (task, outcomeJson) = await ExecuteRetryAsync(context, "s1");

        task.Model.ShouldBeNull("the only candidate in the allowed pool is the prior model itself (Basic) — nothing in-pool beats its tier, so no escalation fires and the ordinary (no profile model authored) resolution stands untouched");
        SupervisorOutcome.ReadEscalation(outcomeJson).ShouldBeNull();
    }

    [Fact]
    public async Task A_crash_recovery_replay_reports_the_orphans_own_dispatched_model_not_a_re_guess()
    {
        // Adversarial-sweep-found bug: a reclaimed orphan's TaskJson (and therefore its ACTUAL dispatched model) was
        // fixed by the CRASHED pass — StageAgentsAndParkAsync never re-resolves it. But a naive implementation would
        // freshly recompute the escalation pick on the replay, which can drift if the team's model pool changed in
        // between (a stronger model added, here). The recorded escalation must describe what's ACTUALLY running.
        var teamId = await SeedTeamAsync();
        var credentialId = await SeedCredentialAsync(teamId);
        await SeedModelAsync(credentialId, "claude-haiku-4-5", ModelCapabilityTier.Basic);
        await SeedModelAsync(credentialId, "claude-sonnet-4-5", ModelCapabilityTier.Strong);
        var runId = await SeedSupervisorRunAsync(teamId);

        var context = Context(runId, teamId,
            Plan("s1"),
            SpawnResult(2, "s1", Guid.NewGuid(), contradiction: "over_claim", model: "claude-haiku-4-5"));

        // Simulate the CRASHED first pass: it already escalated to "claude-sonnet-4-5" and persisted that on a
        // Queued AgentRun for this run+node, but crashed before the wait row (and the terminal outcome) committed.
        await SeedOrphanAgentRunAsync(teamId, runId, subtaskId: "s1", model: "claude-sonnet-4-5");

        // The pool changes BETWEEN the crash and this replay — a new, even stronger model is added.
        await SeedModelAsync(credentialId, "claude-opus-4-8", ModelCapabilityTier.Frontier);

        var (task, outcomeJson) = await ExecuteRetryAsync(context, "s1");

        task.Model.ShouldBe("claude-sonnet-4-5", "the reclaimed orphan's OWN persisted model is reused verbatim — never re-resolved");

        var escalation = SupervisorOutcome.ReadEscalation(outcomeJson);
        escalation.ShouldNotBeNull();
        escalation!.To.ShouldBe("claude-sonnet-4-5", "the escalation record must describe what's ACTUALLY dispatched — a naive re-guess would wrongly report the newly-added claude-opus-4-8");
    }

    /// <summary>A Queued AgentRun for this run+node with no staged wait — the crash-recovery orphan <see cref="ReclaimableOrphanAgentIdsAsync"/> finds and reuses verbatim, its TaskJson already carrying <paramref name="model"/> (the CRASHED pass's own escalation pick).</summary>
    private async Task SeedOrphanAgentRunAsync(Guid teamId, Guid runId, string subtaskId, string model)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask { Goal = "retry s1", Harness = "codex-cli", Model = model, SubtaskId = subtaskId },
            teamId, runId, NodeId, iterationKey: "", cancellationToken: CancellationToken.None);
    }

    // ─── Drive the real executor ──────────────────────────────────────────────────

    private async Task<(AgentTask Task, string OutcomeJson)> ExecuteRetryAsync(SupervisorTurnContext context, string subtaskId)
    {
        using var scope = _fixture.BeginScope();
        var executor = scope.Resolve<ISupervisorActionExecutor>();

        var payload = JsonSerializer.Serialize(new SupervisorRetryPayload { SubtaskId = subtaskId }, AgentJson.Options);
        var decision = new SupervisorDecision { Kind = SupervisorDecisionKinds.Retry, PayloadJson = payload };

        var execution = await executor.ExecuteAsync(decision, context, CancellationToken.None);

        var run = await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking()
            .Where(r => r.WorkflowRunId == context.SupervisorRunId && r.NodeId == NodeId)
            .OrderByDescending(r => r.CreatedDate).FirstAsync();

        return (JsonSerializer.Deserialize<AgentTask>(run.TaskJson, AgentJson.Options)!, execution.OutcomeJson);
    }

    // ─── Context / decision-tape builders ─────────────────────────────────────────

    private static SupervisorTurnContext Context(Guid runId, Guid teamId, params SupervisorPriorDecision[] prior) => new()
    {
        Goal = Goal,
        SupervisorRunId = runId,
        TeamId = teamId,
        NodeId = NodeId,
        TurnNumber = prior.Length + 1,
        PriorDecisions = prior,
    };

    private static SupervisorPriorDecision Plan(string subtaskId)
    {
        var payload = JsonSerializer.Serialize(new SupervisorPlanPayload
        {
            Goal = Goal,
            Subtasks = new List<SupervisorPlannedSubtask> { new() { Id = subtaskId, Title = subtaskId, Instruction = $"do {subtaskId}" } },
        }, AgentJson.Options);

        return new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 1, DecisionKind = SupervisorDecisionKinds.Plan, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payload, OutcomeJson = "{}" };
    }

    /// <summary>A prior FAILED spawn recording one unit's self-report×grade contradiction + resolved model — the exact shape <see cref="SupervisorOutcome.FindResultByAgentRunId"/> reads to decide whether/how to escalate a retry of this subtask.</summary>
    private static SupervisorPriorDecision SpawnResult(long seq, string subtaskId, Guid agentRunId, string? contradiction, string model)
    {
        var result = new SupervisorAgentResult { AgentRunId = agentRunId, Status = "Failed", Error = "acceptance failed", Contradiction = contradiction, Model = model };

        var payload = JsonSerializer.Serialize(new SupervisorSpawnPayload { SubtaskIds = new[] { subtaskId } }, AgentJson.Options);
        var outcome = JsonSerializer.Serialize(new { agentRunIds = new[] { agentRunId }, agentCount = 1, agentResults = new[] { result } }, AgentJson.Options);

        return new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = seq, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payload, OutcomeJson = outcome };
    }

    // ─── Seeding (team / model credential + rows / supervisor run) ────────────────

    private async Task<Guid> SeedTeamAsync() => (await WorkflowsTestSeed.SeedTeamAsync(_fixture)).TeamId;

    private async Task<Guid> SeedCredentialAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var id = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = id, TeamId = teamId, Provider = "Anthropic", DisplayName = "test cred",
            EncryptedApiKey = scope.Resolve<IPayloadEncryptor>().Encrypt("test-key"), Status = CredentialStatus.Active,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedModelAsync(Guid credentialId, string modelId, ModelCapabilityTier tier, bool isDefault = false)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var id = Guid.NewGuid();
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = id, ModelCredentialId = credentialId, ModelId = modelId, Enabled = true, IsDefault = isDefault, CapabilityTier = tier, Source = ModelSource.Manual });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedSupervisorRunAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var (_, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scopeAsAdmin = _fixture.BeginScopeAs(userId, teamId, Messages.Constants.Roles.Admin);
        var workflowId = await scopeAsAdmin.Resolve<MediatR.IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "sup-retry-escalation-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = new Messages.Dtos.Workflows.WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<Messages.Dtos.Workflows.NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = NodeId, TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json($$"""{"goal":"{{Goal}}"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
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
