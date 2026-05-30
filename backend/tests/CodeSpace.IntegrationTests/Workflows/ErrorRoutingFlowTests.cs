using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Engine v2 Phase 2 — node-level error routing (graph try/catch), against real Postgres + the
/// real engine. A node failure normally fails the run; with an outgoing <c>error</c>-handle edge
/// the engine routes the run down the handler branch instead, exposing the failure as the node's
/// <c>error</c> output. Uses <see cref="FlakyTestNode"/> as a deterministic failure source. Pins:
///   • a failure with an error edge routes to the handler (run succeeds, normal branch skipped,
///     handler reads {{nodes.&lt;id&gt;.outputs.error.message}});
///   • WITHOUT an error edge a failure still fails the run (error routing is strictly opt-in);
///   • error routing composes with retry — retries exhaust first, THEN the error branch fires;
///   • a re-dispatched run with a handled failure rehydrates without re-failing or re-running it.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ErrorRoutingFlowTests
{
    private readonly PostgresFixture _fixture;

    public ErrorRoutingFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Error_edge_routes_a_failure_to_the_handler_branch()
    {
        var key = Guid.NewGuid().ToString("N");
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, ErrorRoutedDefinition(key, failTimes: 99, retry: null));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success, "the failure is handled by the error branch, so the run succeeds");

        (await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "flaky")).Status
            .ShouldBe(NodeStatus.Failure, "the node honestly records that it failed — the RUN succeeds because the failure was handled");
        (await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "ok")).Status
            .ShouldBe(NodeStatus.Skipped, "the normal (success) branch is dead when the node fails");
        (await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "caught")).Status
            .ShouldBe(NodeStatus.Success, "the error branch runs");

        // The error output carries BOTH the failure message AND the failing node's id, so a shared
        // handler can tell what failed and where.
        run.OutputsJson.ShouldNotBeNull();
        var outputs = System.Text.Json.JsonDocument.Parse(run.OutputsJson!).RootElement;
        outputs.GetProperty("message").GetString().ShouldContain("flaky failure");
        outputs.GetProperty("node").GetString().ShouldBe("flaky");
    }

    [Fact]
    public async Task Without_an_error_edge_a_failure_still_fails_the_run()
    {
        // Error routing is strictly opt-in: a node with no `error` edge fails the run exactly as
        // before this feature (the non-breaking baseline).
        var key = Guid.NewGuid().ToString("N");
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, NoErrorEdgeDefinition(key));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Failure);
        (await db.WorkflowRunNode.AsNoTracking().AnyAsync(n => n.RunId == runId && n.NodeId == "end"))
            .ShouldBeFalse("with no error edge the failure halts the run — the terminal never runs");
    }

    [Fact]
    public async Task Retry_exhausts_before_the_error_branch_fires()
    {
        // PR1 retry + PR2 error routing compose: the node is retried to exhaustion FIRST, then the
        // (still-failing) result routes down the error branch.
        var key = Guid.NewGuid().ToString("N");
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, ErrorRoutedDefinition(key, failTimes: 99, retry: new RetryPolicy { MaxAttempts = 2, BackoffSeconds = 0 }));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        FlakyTestNode.AttemptsFor(key).ShouldBe(2, "the node is retried to exhaustion before the failure is handled");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);
        (await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "caught")).Status
            .ShouldBe(NodeStatus.Success, "after retries exhaust, the error branch runs");
    }

    [Fact]
    public async Task Re_dispatching_a_handled_failure_does_not_re_fail_or_re_run()
    {
        // The durable walker must rehydrate a persisted handled-failure as a settled (failed)
        // source — NOT re-throw it as a run failure, and NOT re-invoke the node.
        var key = Guid.NewGuid().ToString("N");
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, ErrorRoutedDefinition(key, failTimes: 99, retry: null));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);
        FlakyTestNode.AttemptsFor(key).ShouldBe(1, "first pass invokes the node once");

        // Simulate a re-dispatch (reconciler / Hangfire retry / replica race) — flip the completed
        // run back to Enqueued so the engine's entry CAS claims it again and rehydrates.
        await ReEnqueueAsync(runId);
        await RunEngineAsync(runId);

        FlakyTestNode.AttemptsFor(key).ShouldBe(1, "rehydrate settles the handled failure from the ledger — it does NOT re-invoke the node");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Success, "re-entry of a handled-failure run stays Success — the persisted node.failed is NOT re-thrown");
    }

    [Fact]
    public async Task Success_does_not_fire_the_error_branch()
    {
        // The regression the user hit: a node that SUCCEEDS must take its NORMAL edge, never the
        // error edge. (failTimes:0 ⇒ flaky succeeds on the first attempt.)
        var key = Guid.NewGuid().ToString("N");
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, ErrorRoutedDefinition(key, failTimes: 0, retry: null));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);
        (await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "flaky")).Status
            .ShouldBe(NodeStatus.Success);
        (await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "ok")).Status
            .ShouldBe(NodeStatus.Success, "the normal (success) branch runs");
        (await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "caught")).Status
            .ShouldBe(NodeStatus.Skipped, "the error branch must NOT fire when the node succeeds");
    }

    [Fact]
    public async Task Success_after_a_retry_does_not_fire_the_error_branch()
    {
        // Compose retry + error routing on the SUCCESS path: the node fails once, succeeds on the
        // retry, and the error branch stays skipped (only a genuine final failure routes to error).
        var key = Guid.NewGuid().ToString("N");
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, ErrorRoutedDefinition(key, failTimes: 1, retry: new RetryPolicy { MaxAttempts = 2, BackoffSeconds = 0 }));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        FlakyTestNode.AttemptsFor(key).ShouldBe(2, "fails once, then succeeds on the retry");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);
        (await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "ok")).Status
            .ShouldBe(NodeStatus.Success, "the normal branch runs once the retry succeeds");
        (await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "caught")).Status
            .ShouldBe(NodeStatus.Skipped, "a node that ultimately succeeds never routes to its error branch");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        return await mediator.Send(new CreateWorkflowCommand
        {
            Name = "errroute-" + Guid.NewGuid().ToString("N")[..6],
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

    private async Task ReEnqueueAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE workflow_run SET status = 'Enqueued' WHERE id = {runId}");
    }

    // start → flaky → ok (normal terminal); flaky =(error)=> caught (terminal capturing the error).
    // failTimes drives the source's outcome: 99 ⇒ always fails (error branch); 0 ⇒ succeeds (normal branch).
    private static WorkflowDefinition ErrorRoutedDefinition(string key, int failTimes, RetryPolicy? retry) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "flaky", TypeKey = FlakyTestNode.Key,
                    Config = WorkflowsTestSeed.Json($$"""{"key":"{{key}}","failTimes":{{failTimes}}}"""),
                    Inputs = WorkflowsTestSeed.EmptyJson(),
                    Retry = retry },
            new() { Id = "ok", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "caught", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{"message":"{{nodes.flaky.outputs.error.message}}","node":"{{nodes.flaky.outputs.error.node}}"}""") },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "flaky" },
            new() { From = "flaky", To = "ok" },
            new() { From = "flaky", To = "caught", SourceHandle = WorkflowHandles.Error },
        },
    };

    // start → flaky → end (terminal). No error edge — a failure fails the run.
    private static WorkflowDefinition NoErrorEdgeDefinition(string key) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "flaky", TypeKey = FlakyTestNode.Key,
                    Config = WorkflowsTestSeed.Json($$"""{"key":"{{key}}","failTimes":99}"""),
                    Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "flaky" },
            new() { From = "flaky", To = "end" },
        },
    };
}
