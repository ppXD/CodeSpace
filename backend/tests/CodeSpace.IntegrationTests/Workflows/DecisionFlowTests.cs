using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Dtos.Decisions;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Decision substrate D1 (high fidelity — REAL engine + REAL <see cref="IWorkflowResumeService"/> over real Postgres).
/// A <c>flow.decision</c> node parks the run on a durable, BOUNDED <c>Decision</c> wait carrying the typed
/// <c>DecisionRequest</c> envelope; an answer resolves it via the single-writer CAS and the run resumes FROM THE EXACT
/// node (ledger rehydration), surfacing the <c>DecisionAnswer</c> as outputs. Pins the hard guarantees: resolve-once
/// (AC2 — a second answer is an idempotent no-op), never-hang (the deadline applies the default), and the envelope is
/// durable + traceable (the row carries the question + rootTraceId).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class DecisionFlowTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _fixture;

    public DecisionFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task An_answer_resumes_the_run_from_the_exact_decision_point_and_surfaces_it()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, DecisionDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        Guid waitId;
        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Suspended);

            var wait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId);
            wait.WaitKind.ShouldBe(WorkflowWaitKinds.Decision);
            wait.WakeAt.ShouldNotBeNull("a decision is ALWAYS bounded — the deadline is stored as wake_at so it can never hang (AC4)");
            waitId = wait.Id;

            // The envelope is durable + traceable: the parked row carries the typed DecisionRequest.
            var req = JsonSerializer.Deserialize<DecisionRequest>(JsonDocument.Parse(wait.PayloadJson!).RootElement, Json)!;
            req.Question.ShouldBe("Which migration path?");
            req.Policy.ShouldBe(DecisionPolicies.HumanRequired);
            req.RootTraceId.ShouldBe(runId, "rootTraceId ties the decision to its root run for the one-line trace (AC5)");
            req.Status.ShouldBe(DecisionStatuses.Pending);
        }

        var answer = new DecisionAnswer { DecisionId = Guid.NewGuid(), AnsweredBy = DecisionAnsweredByKinds.Human, SelectedOptions = new[] { "b" }, FreeText = "B is safer", AnsweredByUserId = userId };
        (await ResumeAsync(runId, waitId, answer)).ShouldBeTrue();

        await RunEngineAsync(runId);

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

            var node = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "decide");
            node.Status.ShouldBe(NodeStatus.Success);
            var outputs = JsonDocument.Parse(node.OutputsJson).RootElement;
            outputs.GetProperty("selectedOption").GetString().ShouldBe("b", "the run resumed FROM the decision node with the human's answer");
            outputs.GetProperty("freeText").GetString().ShouldBe("B is safer");
            outputs.GetProperty("answeredBy").GetString().ShouldBe(DecisionAnsweredByKinds.Human);
            outputs.GetProperty("timedOut").GetBoolean().ShouldBeFalse();
        }
    }

    [Fact]
    public async Task A_second_answer_is_an_idempotent_no_op()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, DecisionDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(runId);

        var waitId = (await SingleWaitAsync(runId)).Id;
        var answer = new DecisionAnswer { DecisionId = Guid.NewGuid(), AnsweredBy = DecisionAnsweredByKinds.Human, SelectedOptions = new[] { "a" } };

        (await ResumeAsync(runId, waitId, answer)).ShouldBeTrue("the first answer resolves the wait");
        (await ResumeAsync(runId, waitId, answer with { SelectedOptions = new[] { "b" } })).ShouldBeFalse(
            "AC2: the decision is already resolved — a second answer (cross-pod dup click) is an idempotent no-op, never a double-resume");

        using var verify = _fixture.BeginScope();
        var wait = await verify.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().SingleAsync(w => w.Id == waitId);
        wait.Status.ShouldBe(WorkflowWaitStatuses.Resolved);
        JsonDocument.Parse(wait.PayloadJson!).RootElement.GetProperty("selectedOptions")[0].GetString()
            .ShouldBe("a", "the second answer never overwrote the first resolution");
    }

    [Fact]
    public async Task The_deadline_applies_the_default_and_resumes_with_timed_out()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, DecisionDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(runId);

        var waitId = (await SingleWaitAsync(runId)).Id;

        // Simulate the bounded-wait deadline firing (what the engine schedules at DeadlineAt) with the node's default.
        var timeout = new DecisionAnswer { DecisionId = Guid.NewGuid(), AnsweredBy = DecisionAnsweredByKinds.Timeout, SelectedOptions = new[] { "a" }, Rationale = "deadline", TimedOut = true };
        bool resumed;
        using (var scope = _fixture.BeginScope())
            resumed = await scope.Resolve<IWorkflowResumeService>().ResumeByDeadlineAsync(waitId, JsonSerializer.Serialize(timeout, Json), CancellationToken.None);
        resumed.ShouldBeTrue("the deadline fired on a still-pending decision → it applies the default and resumes");

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var node = await verify.Resolve<CodeSpaceDbContext>().WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "decide");
        var outputs = JsonDocument.Parse(node.OutputsJson).RootElement;
        outputs.GetProperty("timedOut").GetBoolean().ShouldBeTrue("a decision never hangs — the deadline applied the default (AC4)");
        outputs.GetProperty("selectedOption").GetString().ShouldBe("a");
        outputs.GetProperty("answeredBy").GetString().ShouldBe(DecisionAnsweredByKinds.Timeout);
    }

    [Fact]
    public async Task A_queue_answer_resumes_the_node_decision_from_the_exact_point()
    {
        // D3b: answering a NODE-grain (flow.decision) decision through the cross-grain queue's answer service resolves
        // the wait via the SAME ResumeWaitAsync the resume API uses → the run resumes from the exact node with the
        // chosen option; team-scoped + resolve-once.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, DecisionDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(runId);

        var waitId = (await SingleWaitAsync(runId)).Id;

        (await AnswerViaQueueAsync(waitId, new[] { "b" }, "via the queue", teamId, userId)).Outcome
            .ShouldBe(DecisionAnswerOutcome.Answered, "the queue answer resolves the node decision");

        await RunEngineAsync(runId);

        using (var verify = _fixture.BeginScope())
        {
            var node = await verify.Resolve<CodeSpaceDbContext>().WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "decide");
            var outputs = JsonDocument.Parse(node.OutputsJson).RootElement;
            outputs.GetProperty("selectedOption").GetString().ShouldBe("b", "the run resumed from the exact node with the queue answer");
            outputs.GetProperty("answeredBy").GetString().ShouldBe(DecisionAnsweredByKinds.Human);
        }

        (await AnswerViaQueueAsync(waitId, new[] { "a" }, null, teamId, userId)).Outcome
            .ShouldBe(DecisionAnswerOutcome.AlreadyResolved, "resolve-once: the decision is already resolved");

        var (otherTeam, otherUser) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        (await AnswerViaQueueAsync(waitId, new[] { "a" }, null, otherTeam, otherUser)).Outcome
            .ShouldBe(DecisionAnswerOutcome.NotFound, "a foreign team can't see or answer the decision");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<AnswerDecisionResult> AnswerViaQueueAsync(Guid decisionId, IReadOnlyList<string> selectedOptions, string? freeText, Guid teamId, Guid actorUserId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IDecisionAnswerService>().AnswerAsync(decisionId, selectedOptions, freeText, teamId, actorUserId, CancellationToken.None);
    }

    private async Task<WorkflowRunWait> SingleWaitAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId);
    }

    private async Task<bool> ResumeAsync(Guid runId, Guid waitId, DecisionAnswer answer)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowResumeService>().ResumeWaitAsync(runId, waitId, JsonSerializer.Serialize(answer, Json), CancellationToken.None);
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "decision-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = definition,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private static WorkflowDefinition DecisionDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "decide", TypeKey = "flow.decision",
                    Config = WorkflowsTestSeed.Json("""{ "question": "Which migration path?", "decisionType": "choose_one", "options": [ {"id":"a","label":"Path A"}, {"id":"b","label":"Path B"} ], "policy": "human_required", "timeoutSeconds": 600, "defaultAction": "a" }"""),
                    Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "decide" },
            new() { From = "decide", To = "end" },
        },
    };
}
