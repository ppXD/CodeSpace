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
    public async Task A_contradiction_graded_by_an_amended_oracle_never_escalates()
    {
        // B5 (A2 ruling): the self-report never disagreed with the CO-SIGNED check, only with the dead one —
        // escalating the retry's model tier on that stale verdict spends real money on evidence everyone agrees
        // was wrong. The retry still runs (it consumes the amendment); only the tier bump is suppressed.
        var teamId = await SeedTeamAsync();
        var credentialId = await SeedCredentialAsync(teamId);
        await SeedModelAsync(credentialId, "claude-haiku-4-5", ModelCapabilityTier.Basic);
        await SeedModelAsync(credentialId, "claude-sonnet-4-5", ModelCapabilityTier.Strong);
        var runId = await SeedSupervisorRunAsync(teamId);

        var amend = SupervisorAmendAcceptance.IntoAskHuman(new SupervisorAmendAcceptancePayload
        {
            SubtaskId = "s1", Reason = "the check invokes missing tooling",
            Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "sh", "verify.sh" } },
        });
        var approvedCard = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 3, DecisionKind = SupervisorDecisionKinds.AskHuman, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = amend.PayloadJson, OutcomeJson = """{"question":"q","answer":"approve"}""",
        };

        var context = Context(runId, teamId,
            Plan("s1"),
            SpawnResult(2, "s1", Guid.NewGuid(), contradiction: "over_claim", model: "claude-haiku-4-5"),
            approvedCard);

        var (task, outcomeJson) = await ExecuteRetryAsync(context, "s1");

        task.Model.ShouldBeNull("the stale contradiction is suppressed — no tier bump, the ordinary resolution stands");
        SupervisorOutcome.ReadEscalation(outcomeJson).ShouldBeNull();
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
    public async Task An_over_cap_retry_still_receives_the_failure_diagnosis_only_escalation_is_suppressed()
    {
        // P5-2 × A2: the cost-cap guard exists to stop escalated-model SPEND; the diagnosis handoff costs nothing
        // and the retry proceeds regardless — an over-cap retry must not also be a blind one.
        var teamId = await SeedTeamAsync();
        var runId = await SeedSupervisorRunAsync(teamId);
        var agentRunId = Guid.NewGuid();

        var gradedResult = new SupervisorAgentResult
        {
            AgentRunId = agentRunId, Status = "Succeeded", ProducedBranch = "codespace/agent/s1",
            AcceptancePassed = false, AcceptanceDetail = "tests-failed-exit-1", AcceptanceEvidenceTail = "exit=1\nFAILED Foo.Bar",
        };
        var spawn = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = JsonSerializer.Serialize(new SupervisorSpawnPayload { SubtaskIds = new[] { "s1" } }, AgentJson.Options),
            OutcomeJson = JsonSerializer.Serialize(new { agentRunIds = new[] { agentRunId }, agentCount = 1, agentResults = new[] { gradedResult } }, AgentJson.Options),
        };

        var context = Context(runId, teamId, Plan("s1"), spawn) with { MaxCostUsd = 5m, RunSpendUsd = 5.01m };

        var (task, outcomeJson) = await ExecuteRetryAsync(context, "s1");

        task.Goal.ShouldContain("| FAILED Foo.Bar", customMessage: "the diagnosis folds even over the cap");
        SupervisorOutcome.ReadEscalation(outcomeJson).ShouldBeNull("escalation stays suppressed over the cap");
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

        task.Model.ShouldBeNull("the only candidate in the allowed pool is the prior model itself (Basic) — nothing in-pool beats its tier, so the ordinary (no profile model authored) resolution stands untouched");

        // D3: the DISPATCH is untouched, but the ATTEMPT is recorded. Before, this returned nothing at all and the
        // next turn's brain read a still-failing retry with no way to tell "reaching higher was impossible" from
        // "nobody reached" — and re-asked for the same retry.
        var escalation = SupervisorOutcome.ReadEscalation(outcomeJson);
        escalation.ShouldNotBeNull();
        escalation!.To.ShouldBeNull("nothing in the bounded pool beat the prior tier");
        escalation.From.ShouldBe("claude-haiku-4-5");
        escalation.Reason.ShouldContain("over_claim");
    }

    [Fact]
    public async Task A_one_model_team_records_the_no_op_escalation_and_the_decider_prompt_says_so()
    {
        // The one-model case end to end: the trigger fires, the team has literally nothing stronger, and the fact
        // reaches the BRAIN — the retry's outcome carries it and the next turn's rendered prompt names it.
        var teamId = await SeedTeamAsync();
        var credentialId = await SeedCredentialAsync(teamId);
        await SeedModelAsync(credentialId, "claude-haiku-4-5", ModelCapabilityTier.Basic);
        var runId = await SeedSupervisorRunAsync(teamId);

        var context = Context(runId, teamId,
            Plan("s1"),
            SpawnResult(2, "s1", Guid.NewGuid(), contradiction: "over_claim", model: "claude-haiku-4-5"));

        var (task, outcomeJson) = await ExecuteRetryAsync(context, "s1");

        task.Model.ShouldBeNull("a one-model team keeps its only model — the dispatch is untouched");

        var escalation = SupervisorOutcome.ReadEscalation(outcomeJson);
        escalation.ShouldNotBeNull();
        escalation!.To.ShouldBeNull();

        // Replay the recorded retry decision into the NEXT turn's prompt, exactly as production gets there: the
        // staged agent finishes and its result is folded onto this same outcome (SupervisorOutcome.FoldAgentResults,
        // which must preserve the escalation block it never wrote), then the decider renders the tape.
        var stagedAgentRunId = SupervisorOutcome.ReadStagedAgentRunIds(outcomeJson).ShouldHaveSingleItem();
        var foldedOutcome = SupervisorOutcome.FoldAgentResults(outcomeJson, new[]
        {
            new SupervisorAgentResult { AgentRunId = stagedAgentRunId, Status = "Failed", Error = "still failing", Model = "claude-haiku-4-5" },
        });

        var retryDecision = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 3, DecisionKind = SupervisorDecisionKinds.Retry, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = JsonSerializer.Serialize(new { subtaskId = "s1" }, AgentJson.Options), OutcomeJson = foldedOutcome,
        };

        var prompt = CodeSpace.Core.Services.Supervisor.Deciders.LlmSupervisorDecider.BuildUserPromptForTest(Context(runId, teamId, Plan("s1"), retryDecision));

        prompt.ShouldContain("no stronger model in this team's pool", customMessage: "the brain must be told escalating again buys nothing");
        prompt.ShouldContain("claude-haiku-4-5");
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

    [Fact]
    public async Task A_retry_after_a_failed_grade_hands_the_oracle_output_to_the_retried_agent()
    {
        // P5-2 (diagnosis-driven repair), proven through the REAL ExecuteRetryAsync over real Postgres: the prior
        // attempt's folded WORK-classed failure (verdict + bounded oracle tail on the tape) lands in the retried
        // agent's GOAL — the worker starts from what the check NAMED, not a re-discovery run. The lookup keys off
        // the same tape row the escalation trigger reads (one prior-result resolve, same attempt).
        var teamId = await SeedTeamAsync();
        var runId = await SeedSupervisorRunAsync(teamId);
        var agentRunId = Guid.NewGuid();

        var gradedResult = new SupervisorAgentResult
        {
            AgentRunId = agentRunId, Status = "Succeeded", ProducedBranch = "codespace/agent/s1",
            AcceptancePassed = false, AcceptanceDetail = "tests-failed-exit-1",
            AcceptanceEvidenceTail = "exit=1\nFAILED FooServiceTests.Bar_returns_42: expected 42 but was 41",
        };
        var spawn = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = JsonSerializer.Serialize(new SupervisorSpawnPayload { SubtaskIds = new[] { "s1" } }, AgentJson.Options),
            OutcomeJson = JsonSerializer.Serialize(new { agentRunIds = new[] { agentRunId }, agentCount = 1, agentResults = new[] { gradedResult } }, AgentJson.Options),
        };

        var (task, _) = await ExecuteRetryAsync(Context(runId, teamId, Plan("s1"), spawn), "s1");

        task.Goal.ShouldContain("Your prior attempt FAILED its acceptance check (tests-failed-exit-1)", customMessage: "the verdict reaches the worker");
        task.Goal.ShouldContain("| FAILED FooServiceTests.Bar_returns_42: expected 42 but was 41", customMessage: "the oracle's own output reaches the worker, line-fenced as evidence");
        task.DisplayTitle.ShouldNotContain("FAILED FooServiceTests", customMessage: "the diagnosis folds into the GOAL only — the card title stays the subtask's own work");
    }

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
