using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Learning;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres + the REAL <see cref="SupervisorTurnService"/> over the real decision ledger and
/// the REAL <see cref="LlmSupervisorDecider"/>; only the gateway is scripted, at the one honest
/// <see cref="IStructuredLLMClient"/> seam): a brain whose reply the run cannot execute gets ONE bounded re-ask,
/// and the recovered decision reaches the durable row with the re-ask on it.
///
/// <para>Two live shapes. A bare <c>{"kind":"plan"}</c> (real-model run 33943475246) — schema-valid, binds cleanly,
/// and projects to an empty plan the executor then rejects, spending the turn. And a <c>retry</c> aimed at a unit
/// that already succeeded while another sat failed (golden <c>five-subtask-middle-failed</c>, main runs 33945398336
/// and 33946934743) — whole payload, plan-declared id, so the run re-runs finished work. The unit tier pins the
/// decider's recovery; THIS tier pins the half the decider cannot see — that the marker survives projection,
/// execution and the terminal CAS, so the ledger says the decision cost a second round-trip instead of reading like
/// a clean one.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorPayloadReaskFlowTests
{
    private const string NodeId = "sup";
    private const string ScriptedProvider = "scripted";

    private const string PayloadLessPlan = """{"kind":"plan","rationale":{"why":"decompose the goal"}}""";
    private const string WholePlan = """{"kind":"plan","plan":{"goal":"ship the feature","subtasks":[{"id":"s1","title":"Audit","instruction":"audit it"}]}}""";

    private const string RetryFinishedUnit = """{"kind":"retry","retry":{"subtaskId":"s1"}}""";

    private readonly PostgresFixture _fixture;

    public SupervisorPayloadReaskFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_payload_less_plan_is_re_asked_and_the_recovered_plan_lands_on_the_ledger_row()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = Guid.NewGuid();

        var client = await RunTurnAsync(runId, teamId, PayloadLessPlan, WholePlan);

        client.Requests.Count.ShouldBe(2, "the payload-less first reply buys exactly ONE bounded re-ask");
        client.Requests[1].UserPrompt.ShouldContain("Decompose into 'subtasks'", customMessage: "the re-ask quotes the 'plan' object's own schema fragment — the shape the first reply proved the model could not recall");

        var row = await OnlyDecisionAsync(runId, teamId);

        row.DecisionKind.ShouldBe(SupervisorDecisionKinds.Plan);
        row.Status.ShouldBe(SupervisorDecisionStatus.Succeeded);
        row.PayloadJson.ShouldContain("audit it", customMessage: "the RE-ASKED plan is what got frozen — not the empty payload the projector substitutes for a missing sub-object");
        // Read the field rather than substring the text: the column is jsonb, so Postgres re-renders the bytes the
        // fold wrote (key order and whitespace both change) and a literal probe would pin the driver, not the fact.
        OutcomeFlag(row, "payloadReasked").ShouldBe(true, "the row says the decision cost a second round-trip");
        SupervisorOutcome.ReadPayloadReaskedFromKind(row.OutcomeJson).ShouldBe(SupervisorDecisionKinds.Plan, "…and which kind the payload-less reply had named");

        (await CurrentWorkPlanItemsAsync(runId, teamId)).ShouldContain("audit it", customMessage: "the plan is durably recorded — the executor REJECTS a subtask-less plan, so a lost re-ask would leave no work plan at all");
    }

    [Fact]
    public async Task A_whole_first_reply_leaves_the_row_byte_identical_to_before_the_re_ask_existed()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = Guid.NewGuid();

        var client = await RunTurnAsync(runId, teamId, WholePlan);

        client.Requests.Count.ShouldBe(1, "a whole reply must never buy a round-trip");

        var row = await OnlyDecisionAsync(runId, teamId);

        OutcomeFlag(row, "payloadReasked").ShouldBeNull("the common path stays untouched — a re-ask marker on a decision that never needed one is a false signal in every read of the tape");
        SupervisorOutcome.ReadPayloadReaskedFromKind(row.OutcomeJson).ShouldBeNull();
        SupervisorOutcome.ReadRetryTargetReasked(row.OutcomeJson).ShouldBeFalse("neither correction fired — both markers stay off the row");
    }

    [Fact]
    public async Task A_retry_aimed_at_finished_work_is_re_asked_and_the_re_authored_decision_lands_on_the_ledger_row()
    {
        // LIVE shape (golden 'five-subtask-middle-failed', main runs 33945398336 + 33946934743): the brain retried a
        // unit that had already succeeded and been accepted, while another sat FAILED. Schema-valid, whole payload,
        // plan-declared id — nothing downstream can see it, so the turn re-runs finished work.
        //
        // The re-ask reply here re-PLANS: the correction invites a different action, and the server never re-aims the
        // retry itself. The decider tier pins the re-aim-to-the-failed-unit case; THIS tier pins the half the decider
        // cannot see — that the marker survives projection, execution and the terminal CAS onto the durable row.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = Guid.NewGuid();

        await SeedMixedFanOutAsync(runId, teamId);

        var client = await RunTurnAsync(runId, teamId, RetryFinishedUnit, WholePlan);

        client.Requests.Count.ShouldBe(2, "the misaimed first reply buys exactly ONE bounded re-ask");
        client.Requests[1].UserPrompt.ShouldContain("s2", customMessage: "the correction quotes the unit ids a retry is actually owed");
        client.Requests[1].UserPrompt.ShouldContain("build failed: missing symbol", customMessage: "…on top of the run context it decides from, so a real id is authorable");

        var row = await LatestDecisionAsync(runId, teamId);

        row.DecisionKind.ShouldBe(SupervisorDecisionKinds.Plan, "the re-ask reply DECIDES — it may abandon the verb it first named");
        OutcomeFlag(row, "retryTargetReasked").ShouldBe(true, "the row says the decision cost a second round-trip");
        SupervisorOutcome.ReadRetryTargetReasked(row.OutcomeJson).ShouldBeTrue();
    }

    // ─── Plumbing ────────────────────────────────────────────────────────────────

    private async Task<SequencedStructuredClient> RunTurnAsync(Guid runId, Guid teamId, params string[] scriptedReplies)
    {
        using var scope = _fixture.BeginScope();

        var client = new SequencedStructuredClient(scriptedReplies);
        var goalConfig = new SupervisorGoalConfig { Goal = "ship the feature", DisplayTitle = "ship the feature", SupervisorModelId = Guid.NewGuid() };

        await NewTurnService(scope, client).RunTurnAsync(runId, teamId, NodeId, goalConfig.Goal!, conversationId: null, goalConfig, CancellationToken.None);

        return client;
    }

    /// <summary>One boolean field of the persisted outcome, or null when absent — read through the parser because the jsonb column re-renders whatever bytes the fold wrote.</summary>
    private static bool? OutcomeFlag(SupervisorDecisionRecord row, string field) =>
        JsonDocument.Parse(row.OutcomeJson ?? "{}").RootElement.TryGetProperty(field, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private async Task<SupervisorDecisionRecord> OnlyDecisionAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking()
            .SingleAsync(d => d.SupervisorRunId == runId && d.TeamId == teamId);
    }

    private async Task<SupervisorDecisionRecord> LatestDecisionAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId).OrderByDescending(d => d.Sequence).FirstAsync();
    }

    /// <summary>The live fan-out on the tape before the turn under test: s1 succeeded and is accepted, s2 FAILED. A retry is owed to s2 and to nothing else.</summary>
    private async Task SeedMixedFanOutAsync(Guid runId, Guid teamId)
    {
        var plan = new SupervisorPlanPayload
        {
            Goal = "ship the feature",
            Subtasks = new[]
            {
                new SupervisorPlannedSubtask { Id = "s1", Title = "Audit", Instruction = "audit it" },
                new SupervisorPlannedSubtask { Id = "s2", Title = "Implement", Instruction = "implement it" },
            },
        };

        var results = new[]
        {
            new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = "Succeeded", Summary = "audited", AcceptancePassed = true, AcceptanceDetail = "tests-passed" },
            new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = "Failed", Error = "build failed: missing symbol" },
        };

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;

        db.SupervisorDecisionRecord.AddRange(
            Seeded(runId, teamId, 1, SupervisorDecisionKinds.Plan, JsonSerializer.Serialize(plan, AgentJson.Options), "{}", now),
            Seeded(runId, teamId, 2, SupervisorDecisionKinds.Spawn, """{"subtaskIds":["s1","s2"]}""",
                JsonSerializer.Serialize(new { agentRunIds = results.Select(r => r.AgentRunId), agentCount = results.Length, agentResults = results }, AgentJson.Options), now));

        await db.SaveChangesAsync();
    }

    private static SupervisorDecisionRecord Seeded(Guid runId, Guid teamId, long sequence, string kind, string payloadJson, string outcomeJson, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId, Sequence = sequence,
        DecisionKind = kind, IdempotencyKey = $"{kind}-{Guid.NewGuid():N}", InputHash = "test",
        Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payloadJson, OutcomeJson = outcomeJson,
        FenceEpoch = 1, CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
    };

    private async Task<string> CurrentWorkPlanItemsAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var plan = await scope.Resolve<Core.Services.Plans.IWorkPlanService>().GetCurrentAsync(runId, teamId, CancellationToken.None, CodeSpace.Messages.Plans.WorkPlanOrigins.Supervisor);

        return plan?.ItemsJson ?? "";
    }

    private static SupervisorTurnService NewTurnService(ILifetimeScope scope, IStructuredLLMClient client) => new(
        scope.Resolve<ISupervisorDecisionLog>(),
        NewDecider(scope, client),
        scope.Resolve<ISupervisorActionExecutor>(),
        scope.Resolve<CodeSpaceDbContext>(),
        scope.Resolve<ISupervisorAcceptanceGrader>(),
        scope.Resolve<Core.Services.Decisions.IDecisionQueueService>(),
        scope.Resolve<Core.Services.Supervisor.Arbiter.IDecisionArbiter>(),
        scope.Resolve<Core.Services.Decisions.IDecisionAnswerService>(),
        scope.Resolve<Core.Services.Plans.IWorkPlanService>(),
        scope.Resolve<Core.Services.Workflows.Lifecycle.IRunRecordLogger>(),
        scope.Resolve<Core.Services.Workflows.Artifacts.IArtifactOffloader>(),
        scope.Resolve<IPublishManifestStore>(),
        scope.Resolve<ISupervisorPublishedBranchResolver>(),
        scope.Resolve<Core.Services.Completion.ICompletionAssessmentComposer>(),
        scope.Resolve<Core.Services.Workflows.Budget.IBudgetLedger>(),
        scope.Resolve<ILessonReader>(),
        scope.Resolve<ILogger<SupervisorTurnService>>());

    private static LlmSupervisorDecider NewDecider(ILifetimeScope scope, IStructuredLLMClient client) => new(
        new LLMClientRegistry(new ILLMClient[] { (ILLMClient)client }),
        new StubPoolSelector(),
        new AgentHarnessRegistry(Array.Empty<IAgentHarness>()),
        RealModelLiveWire.Personas(),
        new InMemoryTapeSummaryStore(),
        new NullRepoGrounding(),
        NullLogger<LlmSupervisorDecider>.Instance);

    /// <summary>Walks a fixed sequence of replies (the last one repeats) and records every request — the one honest seam a bounded re-ask is visible at.</summary>
    private sealed class SequencedStructuredClient : ILLMClient, IStructuredLLMClient
    {
        private readonly Queue<JsonElement> _replies;
        public readonly List<StructuredLLMCompletionRequest> Requests = new();

        public SequencedStructuredClient(IEnumerable<string> modelJson) => _replies = new Queue<JsonElement>(modelJson.Select(json => JsonDocument.Parse(json).RootElement.Clone()));

        public string Provider => ScriptedProvider;

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException("the supervisor decides on the structured path only");

        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            return Task.FromResult(new StructuredLLMCompletion { Json = _replies.Count > 1 ? _replies.Dequeue() : _replies.Peek(), Model = request.Model });
        }
    }

    /// <summary>Resolves the operator's pinned brain row to the scripted provider so the decider routes at the scripted client.</summary>
    private sealed class StubPoolSelector : IModelPoolSelector
    {
        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken) =>
            Task.FromResult<ModelPoolPick?>(new ModelPoolPick { ModelId = "scripted-model", Credential = new ResolvedModelCredential { Provider = ScriptedProvider, ApiKey = "x" } });

        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<PoolModelInfo>> ListPoolAsync(Guid teamId, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PoolModelInfo>>(Array.Empty<PoolModelInfo>());
        public Task<Guid?> SelectBrainRowIdAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<Guid?> ResolvePinnedBrainRowIdAsync(Guid teamId, Guid modelCredentialModelId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<string?> ResolveTeamDefaultProviderAsync(Guid teamId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
