using System.Text.Json;
using Autofac;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (the REAL <see cref="Core.Services.Supervisor.Executors.RealSupervisorActionExecutor"/> resolved
/// from DI): P0-2 action schema validation — a retry naming no <c>subtaskId</c>, or an ask_human naming no
/// <c>question</c>, is REJECTED with a specific, actionable reason rather than silently no-opped. Both rejections
/// fire BEFORE any DB write (no subtask lookup, no card post, no wait staged), so a bare in-memory
/// <see cref="SupervisorTurnContext"/> is enough — no team/repo/git seeding, mirroring the direct <c>ExecuteAsync</c>
/// seam <c>SupervisorPlanFoldFlowTests</c> already established for the plan verb's own rejection.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorActionRejectionFlowTests
{
    private readonly PostgresFixture _fixture;

    public SupervisorActionRejectionFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_retry_naming_no_subtask_id_is_rejected_with_a_specific_reason(string subtaskId)
    {
        using var scope = _fixture.BeginScope();

        var execution = await scope.Resolve<ISupervisorActionExecutor>()
            .ExecuteAsync(RetryDecision(subtaskId), Context(), CancellationToken.None);

        execution.OutcomeJson.ShouldBe(RejectedRetryJson);
        execution.ParkedAgentWaitCount.ShouldBe(0, "the rejection is synchronous — no agent wait is staged");
    }

    [Fact]
    public async Task A_retry_decision_with_no_retry_sub_object_is_rejected_the_same_way()
    {
        // The model chose kind="retry" but omitted the whole "retry" sub-object — schema-legal (only "kind" is
        // root-required). Run the REAL SupervisorDecisionProjector (not a hand-typed approximation) so the exact
        // production fallback shape (SupervisorRetryPayload { SubtaskId = "" }) is what reaches the executor.
        using var scope = _fixture.BeginScope();

        var projected = SupervisorDecisionProjector.Project(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Retry });
        var execution = await scope.Resolve<ISupervisorActionExecutor>().ExecuteAsync(projected, Context(), CancellationToken.None);

        execution.OutcomeJson.ShouldBe(RejectedRetryJson);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_ask_human_naming_no_question_is_rejected_with_a_specific_reason_and_posts_no_card(string question)
    {
        using var scope = _fixture.BeginScope();

        var execution = await scope.Resolve<ISupervisorActionExecutor>()
            .ExecuteAsync(AskHumanDecision(question), Context(), CancellationToken.None);

        execution.OutcomeJson.ShouldBe(RejectedAskHumanJson);
        execution.HumanWaitToken.ShouldBeNull("no card was posted — no human interaction is spent on a blank question");
    }

    [Fact]
    public async Task An_ask_human_decision_with_no_askHuman_sub_object_is_rejected_the_same_way()
    {
        // The model chose kind="askHuman" but omitted the whole "askHuman" sub-object — schema-legal (only "kind" is
        // root-required). Run the REAL SupervisorDecisionProjector so the exact production fallback shape
        // (SupervisorAskHumanPayload { Question = "" }) is what reaches the executor.
        using var scope = _fixture.BeginScope();

        var projected = SupervisorDecisionProjector.Project(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.AskHuman });
        var execution = await scope.Resolve<ISupervisorActionExecutor>().ExecuteAsync(projected, Context(), CancellationToken.None);

        execution.OutcomeJson.ShouldBe(RejectedAskHumanJson);
    }

    // ── H2 (strict action identity): ids the current plan never declared are rejected, never ghost-run ──

    [Fact]
    public async Task A_retry_naming_an_id_the_plan_never_declared_is_rejected_naming_the_declared_universe()
    {
        // Pre-H2 this fell through BuildAgentTask's instruction chain to the WHOLE GOAL — a ghost agent re-running
        // the entire task under a typo'd or stale-plan id.
        using var scope = _fixture.BeginScope();

        var execution = await scope.Resolve<ISupervisorActionExecutor>()
            .ExecuteAsync(RetryDecision("authh"), ContextWithPlan("auth", "ui"), CancellationToken.None);

        execution.OutcomeJson.ShouldBe(JsonSerializer.Serialize(RealSupervisorActionExecutor.BuildUnknownSubtaskRetryOutcome("authh", new[] { "auth", "ui" }), AgentJson.Options));
        execution.ParkedAgentWaitCount.ShouldBe(0, "the rejection is synchronous — no ghost agent is ever staged");
    }

    [Fact]
    public async Task A_spawn_naming_an_undeclared_id_rejects_the_WHOLE_spawn_never_a_partial_fan_out()
    {
        using var scope = _fixture.BeginScope();

        var spawn = new SupervisorDecision
        {
            Kind = SupervisorDecisionKinds.Spawn,
            PayloadJson = JsonSerializer.Serialize(new SupervisorSpawnPayload { SubtaskIds = new[] { "auth", "ghost" } }, AgentJson.Options),
        };

        var execution = await scope.Resolve<ISupervisorActionExecutor>()
            .ExecuteAsync(spawn, ContextWithPlan("auth", "ui"), CancellationToken.None);

        execution.OutcomeJson.ShouldBe(JsonSerializer.Serialize(RealSupervisorActionExecutor.BuildUnknownSubtaskSpawnOutcome(new[] { "ghost" }, new[] { "auth", "ui" }), AgentJson.Options),
            "a partial filter would desync the positional subtaskIds[i] ↔ agentResults[i] join — the whole decision is rejected with the reason");
        execution.ParkedAgentWaitCount.ShouldBe(0, "zero agents staged — including for the ids that WERE valid");
    }

    private static SupervisorTurnContext ContextWithPlan(params string[] subtaskIds) => new()
    {
        Goal = "ship the feature", NodeId = "sup", TurnNumber = 2,
        PriorDecisions = new[]
        {
            new SupervisorPriorDecision
            {
                Id = Guid.NewGuid(), Sequence = 1, DecisionKind = SupervisorDecisionKinds.Plan, Status = SupervisorDecisionStatus.Succeeded,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    goal = "g",
                    subtasks = subtaskIds.Select(id => new { id, title = id, instruction = $"do {id}" }),
                }, AgentJson.Options),
                OutcomeJson = "{}",
            },
        },
    };

    private static readonly string RejectedRetryJson = JsonSerializer.Serialize(RealSupervisorActionExecutor.BuildRejectedRetryOutcome(), AgentJson.Options);
    private static readonly string RejectedAskHumanJson = JsonSerializer.Serialize(RealSupervisorActionExecutor.RejectedAskHumanOutcome, AgentJson.Options);

    private static SupervisorTurnContext Context() => new() { Goal = "ship the feature", NodeId = "sup", TurnNumber = 1 };

    private static SupervisorDecision RetryDecision(string subtaskId) => new()
    {
        Kind = SupervisorDecisionKinds.Retry,
        PayloadJson = JsonSerializer.Serialize(new SupervisorRetryPayload { SubtaskId = subtaskId }, AgentJson.Options),
    };

    private static SupervisorDecision AskHumanDecision(string question) => new()
    {
        Kind = SupervisorDecisionKinds.AskHuman,
        PayloadJson = JsonSerializer.Serialize(new SupervisorAskHumanPayload { Question = question }, AgentJson.Options),
    };
}
