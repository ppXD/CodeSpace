using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Arbiter;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Dtos.Decisions;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres + the REAL <see cref="SupervisorTurnService"/> rehydrate + drain + the REAL
/// <see cref="DecisionQueueService"/> / <see cref="DecisionAnswerService"/>, with only the arbiter BRAIN faked at the
/// LLM seam): the D4c-2 turn wiring. Proves the rehydrate fold surfaces a parked CHILD <c>decision.request</c> into
/// <see cref="SupervisorTurnContext.PendingChildDecisions"/> (the real <c>ListPendingForAgentRunsAsync</c> over the real
/// staged-id derivation), gated so a no-spawn run never reads; the drain AUTO-ANSWERS a low-risk child via the
/// supervisor-author path (the real ledger CAS settles the row + records the rationale, AC3); the fail-closed FLOOR
/// overrides a wrong arbiter answer on a high-risk child (it stays parked for a human); and a re-drain is idempotent.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorArbiterDrainFlowTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _fixture;

    public SupervisorArbiterDrainFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    private const string NodeId = "sup";
    private const string Goal = "ship the feature";

    [Fact]
    public async Task Rehydrate_surfaces_a_parked_child_decision_in_PendingChildDecisions()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var childRunId = Guid.NewGuid();
        await SeedChildAgentRunAsync(runId, teamId, childRunId);
        await SeedSpawnDecisionAsync(runId, teamId, sequence: 1, SupervisorDecisionStatus.Succeeded, childRunId);
        var decisionId = await SeedChildDecisionAsync(teamId, childRunId, "which migration path?", DecisionRiskLevels.Low, DecisionPolicies.SupervisorFirst);

        // A sibling team's parked decision on a DIFFERENT run must never leak in.
        await SeedChildDecisionAsync(teamId, Guid.NewGuid(), "unrelated run", DecisionRiskLevels.Low, DecisionPolicies.SupervisorFirst);

        var context = await RehydrateAsync(runId, teamId);

        var pending = context.PendingChildDecisions.ShouldHaveSingleItem();
        pending.Id.ShouldBe(decisionId, "the queue handle is the child's tool-ledger id — only THIS run's child decision");
        pending.AgentRunId.ShouldBe(childRunId);
        pending.Question.ShouldBe("which migration path?");
    }

    [Fact]
    public async Task A_no_spawn_run_surfaces_no_pending_child_decisions()
    {
        // The DB-gate: a run whose tape has no spawn/retry/resolve never derives a child-id set → never reads the queue.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedPlanDecisionAsync(runId, teamId);

        // A parked decision for some agent run exists in the team, but this run staged no children — so it must not appear.
        await SeedChildDecisionAsync(teamId, Guid.NewGuid(), "orphan", DecisionRiskLevels.Low, DecisionPolicies.SupervisorFirst);

        var context = await RehydrateAsync(runId, teamId);

        context.PendingChildDecisions.ShouldBeEmpty("a no-spawn run never reads the queue (byte-identical to pre-D4c-2)");
    }

    [Fact]
    public async Task The_drain_auto_answers_a_low_risk_child_decision_via_the_supervisor_author_path()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var childRunId = Guid.NewGuid();
        await SeedChildAgentRunAsync(runId, teamId, childRunId);
        await SeedSpawnDecisionAsync(runId, teamId, sequence: 1, SupervisorDecisionStatus.Succeeded, childRunId);
        var decisionId = await SeedChildDecisionAsync(teamId, childRunId, "pick a path", DecisionRiskLevels.Low, DecisionPolicies.SupervisorFirst);

        await RunDrainAsync(runId, teamId, new ScriptedArbiter(ArbiterVerdict.Answer(new[] { "a" }, null, "low-risk + recommended")));

        var row = await ReadLedgerAsync(decisionId, teamId);
        row.Status.ShouldBe(ToolCallLedgerStatus.Succeeded, "the real ledger CAS settled the child decision — the blocked agent unblocks");

        var answer = JsonSerializer.Deserialize<DecisionAnswer>(row.ResultJson!, Json)!;
        answer.AnsweredBy.ShouldBe(DecisionAnsweredByKinds.Supervisor, "the supervisor arbiter, not a human, recorded it");
        answer.SelectedOptions.ShouldBe(new[] { "a" });
        answer.Rationale.ShouldBe("low-risk + recommended", "the rationale is durable on the answer (AC3 — never silent)");
    }

    [Fact]
    public async Task The_real_arbiter_resolves_the_brain_model_and_auto_answers_a_low_risk_child_through_the_real_drain()
    {
        // The REAL LlmDecisionArbiter (resolve the brain-model row → frame the prompt → structured reply → project) wired
        // through the REAL rehydrate + drain + ledger — only the brain's STRUCTURED CLIENT is faked (a schema-valid
        // answer), so this is the always-green substrate twin of the live RealModel arbiter gate: it proves the
        // production arbiter path end-to-end (real pool resolution + real auto-answer floor + real ledger CAS) WITHOUT a
        // live key. Threads a real supervisorModelId via goalConfig — the trap the scripted tests sidestep with null.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var childRunId = Guid.NewGuid();
        await SeedChildAgentRunAsync(runId, teamId, childRunId);
        await SeedSpawnDecisionAsync(runId, teamId, sequence: 1, SupervisorDecisionStatus.Succeeded, childRunId);
        var decisionId = await SeedChildDecisionAsync(teamId, childRunId, "pick a path", DecisionRiskLevels.Low, DecisionPolicies.SupervisorFirst);

        var brainModelId = await SeedBrainModelAsync(teamId, FakeProvider, "test-model");

        using var scope = _fixture.BeginScope();
        var arbiter = new LlmDecisionArbiter(new LLMClientRegistry(new ILLMClient[] { new FakeStructuredArbiterClient(FakeProvider, "a") }), scope.Resolve<IModelPoolSelector>());
        var service = BuildService(scope, arbiter);

        var context = await service.RehydrateFromDecisionLogAsync(runId, teamId, NodeId, Goal, new SupervisorGoalConfig { SupervisorModelId = brainModelId }, CancellationToken.None);
        await service.ArbitratePendingChildDecisionsAsync(context, CancellationToken.None);

        var row = await ReadLedgerAsync(decisionId, teamId);
        row.Status.ShouldBe(ToolCallLedgerStatus.Succeeded, "the real arbiter resolved the seeded brain model and answered the low-risk child through the real ledger CAS");

        var answer = JsonSerializer.Deserialize<DecisionAnswer>(row.ResultJson!, Json)!;
        answer.AnsweredBy.ShouldBe(DecisionAnsweredByKinds.Supervisor, "the real supervisor arbiter recorded it, not a human");
        answer.SelectedOptions.ShouldBe(new[] { "a" });
    }

    [Fact]
    public async Task The_floor_overrides_a_wrong_arbiter_answer_on_a_high_risk_child()
    {
        // Defense-in-depth: even when the arbiter (wrongly) says answer, the answer path re-runs the fail-closed floor on
        // the stashed envelope. A high-risk decision is reserved for a human → the row stays parked, never auto-answered.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var childRunId = Guid.NewGuid();
        await SeedChildAgentRunAsync(runId, teamId, childRunId);
        await SeedSpawnDecisionAsync(runId, teamId, sequence: 1, SupervisorDecisionStatus.Succeeded, childRunId);
        var decisionId = await SeedChildDecisionAsync(teamId, childRunId, "drop prod table?", DecisionRiskLevels.High, DecisionPolicies.SupervisorFirst);

        await RunDrainAsync(runId, teamId, new ScriptedArbiter(ArbiterVerdict.Answer(new[] { "a" }, null, "the arbiter was wrong to answer this")));

        var row = await ReadLedgerAsync(decisionId, teamId);
        row.Status.ShouldBe(ToolCallLedgerStatus.AwaitingApproval, "a high-risk decision the floor reserves for a human is NEVER auto-answered, even on a wrong arbiter verdict");
        row.ResultJson.ShouldBeNull("nothing was recorded — it stays parked in the cross-grain queue for a human");
    }

    [Fact]
    public async Task Re_draining_an_already_answered_child_is_a_no_op()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var childRunId = Guid.NewGuid();
        await SeedChildAgentRunAsync(runId, teamId, childRunId);
        await SeedSpawnDecisionAsync(runId, teamId, sequence: 1, SupervisorDecisionStatus.Succeeded, childRunId);
        var decisionId = await SeedChildDecisionAsync(teamId, childRunId, "pick a path", DecisionRiskLevels.Low, DecisionPolicies.SupervisorFirst);

        await RunDrainAsync(runId, teamId, new ScriptedArbiter(ArbiterVerdict.Answer(new[] { "a" }, null, "first answer")));
        var afterFirst = (await ReadLedgerAsync(decisionId, teamId)).ResultJson;

        // A second drain (a re-walk after the answer): the decision is no longer AwaitingApproval, so the queue read
        // excludes it AND the answer CAS would no-op — either way no double-answer, and the first answer stands.
        await RunDrainAsync(runId, teamId, new ScriptedArbiter(ArbiterVerdict.Answer(new[] { "b" }, null, "second answer")));

        (await ReadLedgerAsync(decisionId, teamId)).ResultJson.ShouldBe(afterFirst, "the first answer is the durable one — a re-drain never overwrites a settled decision");
    }

    [Fact]
    public async Task The_frozen_in_flight_replay_bypasses_the_arbiter_drain()
    {
        // Replay-safety: the drain lives in ChooseDecisionAsync, which RunTurnAsync bypasses ENTIRELY when a crashed
        // decision is in-flight (it replays that frozen decision instead). So a child decision is NEVER auto-answered on
        // a frozen replay — the arbiter never acts off the live decide path, even with an arbiter that would answer.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var childRunId = Guid.NewGuid();
        await SeedChildAgentRunAsync(runId, teamId, childRunId);
        await SeedSpawnDecisionAsync(runId, teamId, sequence: 1, SupervisorDecisionStatus.Succeeded, childRunId);
        var decisionId = await SeedChildDecisionAsync(teamId, childRunId, "pick a path", DecisionRiskLevels.Low, DecisionPolicies.SupervisorFirst);

        // A crashed-mid-execution non-terminal decision → rehydrate surfaces it as InFlight → RunTurnAsync replays it frozen.
        await SeedInFlightPlanAsync(runId, teamId, sequence: 2);

        await RunTurnAsync(runId, teamId, new ScriptedArbiter(ArbiterVerdict.Answer(new[] { "a" }, null, "would answer if the drain were reached")));

        (await ReadLedgerAsync(decisionId, teamId)).Status.ShouldBe(ToolCallLedgerStatus.AwaitingApproval,
            "the frozen replay bypasses the drain — the child decision is never auto-answered off the replay path");
    }

    // ─── Drive the real service with only the arbiter brain faked ───────────────────

    private SupervisorTurnService BuildService(IComponentContext scope, IDecisionArbiter arbiter) => new(
        scope.Resolve<ISupervisorDecisionLog>(),
        scope.Resolve<ISupervisorDecider>(),
        scope.Resolve<ISupervisorActionExecutor>(),
        scope.Resolve<CodeSpaceDbContext>(),
        scope.Resolve<ISupervisorAcceptanceGrader>(),
        scope.Resolve<IDecisionQueueService>(),
        arbiter,
        scope.Resolve<IDecisionAnswerService>(),
        scope.Resolve<CodeSpace.Core.Services.Plans.IWorkPlanService>(),
        scope.Resolve<CodeSpace.Core.Services.Workflows.Lifecycle.IRunRecordLogger>(), scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(), scope.Resolve<ILogger<SupervisorTurnService>>());

    private async Task RunTurnAsync(Guid runId, Guid teamId, IDecisionArbiter arbiter)
    {
        using var scope = _fixture.BeginScope();
        await BuildService(scope, arbiter).RunTurnAsync(runId, teamId, NodeId, Goal, conversationId: null, goalConfig: null, CancellationToken.None);
    }

    private async Task<SupervisorTurnContext> RehydrateAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ISupervisorTurnService>().RehydrateFromDecisionLogAsync(runId, teamId, NodeId, Goal, goalConfig: null, CancellationToken.None);
    }

    private async Task RunDrainAsync(Guid runId, Guid teamId, IDecisionArbiter arbiter)
    {
        using var scope = _fixture.BeginScope();
        var service = BuildService(scope, arbiter);

        var context = await service.RehydrateFromDecisionLogAsync(runId, teamId, NodeId, Goal, goalConfig: null, CancellationToken.None);

        await service.ArbitratePendingChildDecisionsAsync(context, CancellationToken.None);
    }

    private sealed class ScriptedArbiter : IDecisionArbiter
    {
        private readonly ArbiterVerdict _verdict;

        public ScriptedArbiter(ArbiterVerdict verdict) => _verdict = verdict;

        public Task<ArbiterVerdict> DecideAsync(PendingDecision decision, Guid teamId, Guid? supervisorModelId, string goal, CancellationToken cancellationToken) =>
            Task.FromResult(_verdict);
    }

    private const string FakeProvider = "TestArbiter";

    /// <summary>A deterministic structured client standing in for the brain — returns a schema-valid arbiter ANSWER for the given option id, so the REAL arbiter's resolve→prompt→project→answer path runs end-to-end without a live key. Its provider matches the seeded brain credential so the arbiter's provider-routing selects it.</summary>
    private sealed class FakeStructuredArbiterClient : ILLMClient, IStructuredLLMClient
    {
        private readonly string _option;

        public FakeStructuredArbiterClient(string provider, string option) { Provider = provider; _option = option; }

        public string Provider { get; }

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LLMCompletion { Text = "", Model = request.Model });

        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new StructuredLLMCompletion { Json = JsonSerializer.SerializeToElement(new { kind = "answer", answer = new { selectedOptions = new[] { _option } }, rationale = "deterministic test answer" }), Model = request.Model });
    }

    // ─── Seeding ────────────────────────────────────────────────────────────────────

    private async Task<Guid> SeedSupervisorRunAsync(Guid teamId, Guid userId)
    {
        var workflowId = await CreateSupervisorWorkflowAsync(teamId, userId);
        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    private async Task<Guid> CreateSupervisorWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<MediatR.IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "sup-arbiter-" + Guid.NewGuid().ToString("N")[..6],
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

    private async Task SeedChildAgentRunAsync(Guid runId, Guid teamId, Guid agentRunId)
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
            IterationKey = $"{NodeId}#turn0#0",
            Harness = "codex-cli",
            Status = AgentRunStatus.Running,   // mid-run, blocked on its decision
            TaskJson = "{}",
            CreatedDate = now,
            CreatedBy = Guid.Empty,
            LastModifiedDate = now,
            LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedSpawnDecisionAsync(Guid runId, Guid teamId, long sequence, SupervisorDecisionStatus status, params Guid[] agentRunIds)
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
            PayloadJson = """{"subtaskIds":["s1"]}""",
            OutcomeJson = JsonSerializer.Serialize(new { agentRunIds, agentCount = agentRunIds.Length }, AgentJson.Options),
            FenceEpoch = 1,
            CreatedDate = now,
            CreatedBy = Guid.Empty,
            LastModifiedDate = now,
            LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedPlanDecisionAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            SupervisorRunId = runId,
            Sequence = 1,
            DecisionKind = SupervisorDecisionKinds.Plan,
            IdempotencyKey = $"plan-{Guid.NewGuid():N}",
            InputHash = "test",
            Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"subtasks":["a"]}""",
            OutcomeJson = """{"planned":["a"]}""",
            FenceEpoch = 1,
            CreatedDate = now,
            CreatedBy = Guid.Empty,
            LastModifiedDate = now,
            LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedInFlightPlanAsync(Guid runId, Guid teamId, long sequence)
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
            DecisionKind = SupervisorDecisionKinds.Plan,
            IdempotencyKey = $"plan-inflight-{Guid.NewGuid():N}",
            InputHash = "test",
            Status = SupervisorDecisionStatus.Running,   // non-terminal → rehydrate surfaces it as InFlight, replayed frozen
            PayloadJson = """{"subtasks":["a"]}""",
            OutcomeJson = null,
            FenceEpoch = 1,
            CreatedDate = now,
            CreatedBy = Guid.Empty,
            LastModifiedDate = now,
            LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedChildDecisionAsync(Guid teamId, Guid agentRunId, string question, string risk, string policy)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var ledgerId = Guid.NewGuid();

        var envelope = new DecisionRequest
        {
            Id = Guid.NewGuid(),
            RootTraceId = Guid.NewGuid(),
            AgentRunId = agentRunId,
            Scope = DecisionScopes.Agent,
            RequesterType = DecisionRequesterTypes.Agent,
            DecisionType = DecisionTypes.ChooseOne,
            Question = question,
            Options = new[] { new DecisionOption { Id = "a", Label = "A" }, new DecisionOption { Id = "b", Label = "B" } },
            RecommendedOption = "a",
            BlockingReason = "the agent is blocked",
            RiskLevel = risk,
            Policy = policy,
            TimeoutAt = DateTimeOffset.UtcNow.AddMinutes(10),
            DedupeKey = Guid.NewGuid().ToString("N"),
            ResumeBackend = DecisionResumeBackends.ToolLedger,
        };

        db.ToolCallLedger.Add(new ToolCallLedger
        {
            Id = ledgerId,
            TeamId = teamId,
            AgentRunId = agentRunId,
            ToolKind = DecisionToolKinds.DecisionRequest,
            IdempotencyKey = $"decision.request:{ledgerId:N}",
            InputHash = new string('0', 64),
            Status = ToolCallLedgerStatus.AwaitingApproval,
            DecisionEnvelopeJson = JsonSerializer.Serialize(envelope, Json),
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return ledgerId;
    }

    /// <summary>Seed a KEYED credentialed-model row for the supervisor brain (the real arbiter resolves it via the real <c>IModelPoolSelector</c>). The key is a dummy — the deterministic structured client never calls a real gateway. Returns the row id → the supervisor's <c>supervisorModelId</c>.</summary>
    private async Task<Guid> SeedBrainModelAsync(Guid teamId, string provider, string modelId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();

        var credId = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = credId, TeamId = teamId, Provider = provider, DisplayName = "arbiter brain cred",
            EncryptedApiKey = encryptor.Encrypt("dummy-key"), BaseUrl = null, Status = CredentialStatus.Active,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        var rowId = Guid.NewGuid();
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = rowId, ModelCredentialId = credId, ModelId = modelId, Source = ModelSource.Manual, Enabled = true });

        await db.SaveChangesAsync();
        return rowId;
    }

    private async Task<ToolCallLedger> ReadLedgerAsync(Guid ledgerId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ToolCallLedger.AsNoTracking()
            .SingleAsync(l => l.Id == ledgerId && l.TeamId == teamId);
    }
}
