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
/// Engine v2 Phase 2 — node-level retry-on-failure, against real Postgres + the real engine. Uses
/// <see cref="FlakyTestNode"/> as a deterministic, controllable failure source. Pins the whole
/// contract:
///   • a transient failure recovers when retries remain (run succeeds, downstream runs);
///   • retries are bounded — exhausting them fails the run exactly like an un-retried failure;
///   • a node with NO policy runs exactly once (the non-breaking default);
///   • each retried attempt leaves a visible Warn `log` record on the run timeline;
///   • a suspend is never mistaken for a failure, so a retry policy doesn't retry a parked node.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class NodeRetryFlowTests
{
    private readonly PostgresFixture _fixture;

    public NodeRetryFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Retry_recovers_a_node_that_fails_then_succeeds()
    {
        var key = Guid.NewGuid().ToString("N");
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, FlakyDefinition(key, failTimes: 2, Retry(maxAttempts: 3)));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        FlakyTestNode.AttemptsFor(key).ShouldBe(3, "two failures + one success = three invocations of the same node instance");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Success, "the third attempt succeeded, so the run completes");
        (await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "flaky")).Status
            .ShouldBe(NodeStatus.Success, "the node's terminal status is its final (successful) attempt");
        (await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "end")).Status
            .ShouldBe(NodeStatus.Success, "downstream of a recovered node runs normally");

        var retryLogs = await RetryLogsAsync(db, runId);
        retryLogs.Count.ShouldBe(2, "two failed attempts each emit one 'retrying' log; the successful attempt emits none");
        retryLogs.ShouldAllBe(m => m.Contains("failed") && m.Contains("/3"));

        // The DURABLE structured retry history (the queryable replacement for the free-text Warn logs): one
        // attempt.failed row per retried attempt, in order, each carrying the 1-based attempt index + max + a
        // non-empty per-attempt error, and each chained to the node.started row via parent_record_id. The node.%
        // cell view is UNAFFECTED — the flaky node shows Success above — because attempt.failed sits OUTSIDE node.*.
        var attempts = await AttemptFailedRecordsAsync(db, runId);
        attempts.Count.ShouldBe(2, "two failed-but-retried attempts → two structured attempt.failed records");

        var nodeStartedId = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.NodeId == "flaky" && r.RecordType == WorkflowRunRecordTypes.NodeStarted)
            .Select(r => r.Id).SingleAsync();
        attempts.ShouldAllBe(a => a.ParentRecordId == nodeStartedId, "each attempt.failed chains to the node.started row so the run-detail tree nests it under the node");

        for (var i = 0; i < attempts.Count; i++)
        {
            var p = System.Text.Json.JsonDocument.Parse(attempts[i].PayloadJson).RootElement;
            p.GetProperty("attempt").GetInt32().ShouldBe(i + 1, "attempt indices are 1-based and in emission order");
            p.GetProperty("max_attempts").GetInt32().ShouldBe(3);
            p.GetProperty("error").GetString().ShouldNotBeNullOrEmpty("the per-attempt error is captured durably, not just logged");
        }
    }

    [Fact]
    public async Task Retry_exhausts_then_fails_the_run()
    {
        var key = Guid.NewGuid().ToString("N");
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, FlakyDefinition(key, failTimes: 99, Retry(maxAttempts: 3)));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        FlakyTestNode.AttemptsFor(key).ShouldBe(3, "the node is tried exactly maxAttempts times before giving up");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(WorkflowRunStatus.Failure, "exhausting retries fails the run like an un-retried failure");
        // The run carries the last attempt's failure message.
        run.Error.ShouldNotBeNull();
        run.Error!.ShouldContain("flaky");

        (await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "flaky")).Status
            .ShouldBe(NodeStatus.Failure);
        (await db.WorkflowRunNode.AsNoTracking().AnyAsync(n => n.RunId == runId && n.NodeId == "end"))
            .ShouldBeFalse("a failed node halts the run — downstream never runs");

        (await RetryLogsAsync(db, runId)).Count.ShouldBe(2, "attempts 1 and 2 log a retry; the final (3rd) attempt fails outright with no retry log");
        (await AttemptFailedRecordsAsync(db, runId)).Count.ShouldBe(2, "attempts 1 and 2 each emit a structured attempt.failed; the final (3rd) attempt is the terminal node.failed, not an attempt.failed");
    }

    [Fact]
    public async Task No_retry_policy_fails_after_a_single_attempt()
    {
        // The non-breaking default: a node with Retry == null behaves exactly as before — one
        // attempt, fail the run, no retry logs.
        var key = Guid.NewGuid().ToString("N");
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, FlakyDefinition(key, failTimes: 99, retry: null));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        FlakyTestNode.AttemptsFor(key).ShouldBe(1, "no policy ⇒ exactly one attempt");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Failure);
        (await RetryLogsAsync(db, runId)).Count.ShouldBe(0, "a single-attempt node emits no retry logs");
    }

    [Fact]
    public async Task Retry_policy_never_retries_a_suspend()
    {
        // A retry policy on a node that SUSPENDS (flow.sleep) must not treat the suspend as a
        // failure: the run parks (Suspended), it does not loop/retry into Failure.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, SleepWithRetryDefinition(seconds: 60, Retry(maxAttempts: 3)));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Suspended, "a suspend short-circuits the retry loop — it's not a failure");
        (await db.WorkflowRunWait.AsNoTracking().AnyAsync(w => w.RunId == runId && w.NodeId == "sleep"))
            .ShouldBeTrue("the sleep parked normally with a wait row");
        (await RetryLogsAsync(db, runId)).Count.ShouldBe(0, "no retry happened — nothing failed");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>The structured per-attempt records for the flaky node, oldest-first — payload + the parent chain.</summary>
    private static async Task<List<(string PayloadJson, Guid? ParentRecordId)>> AttemptFailedRecordsAsync(CodeSpaceDbContext db, Guid runId)
    {
        var rows = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.NodeId == "flaky" && r.RecordType == WorkflowRunRecordTypes.AttemptFailed)
            .OrderBy(r => r.Sequence)
            .Select(r => new { r.PayloadJson, r.ParentRecordId })
            .ToListAsync();

        return rows.Select(r => (r.PayloadJson, r.ParentRecordId)).ToList();
    }

    private static async Task<List<string>> RetryLogsAsync(CodeSpaceDbContext db, Guid runId)
    {
        var records = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.NodeId == "flaky" && r.RecordType == WorkflowRunRecordTypes.Log)
            .OrderBy(r => r.Sequence)
            .Select(r => r.PayloadJson)
            .ToListAsync();

        return records
            .Select(p => System.Text.Json.JsonDocument.Parse(p).RootElement.GetProperty("message").GetString() ?? "")
            .ToList();
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        return await mediator.Send(new CreateWorkflowCommand
        {
            Name = "retry-" + Guid.NewGuid().ToString("N")[..6],
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

    private static RetryPolicy Retry(int maxAttempts) => new() { MaxAttempts = maxAttempts, BackoffSeconds = 0 };

    private static WorkflowDefinition FlakyDefinition(string key, int failTimes, RetryPolicy? retry) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "flaky", TypeKey = FlakyTestNode.Key,
                    Config = WorkflowsTestSeed.Json($$"""{"key":"{{key}}","failTimes":{{failTimes}}}"""),
                    Inputs = WorkflowsTestSeed.EmptyJson(),
                    Retry = retry },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "flaky" },
            new() { From = "flaky", To = "end" },
        },
    };

    private static WorkflowDefinition SleepWithRetryDefinition(int seconds, RetryPolicy retry) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "sleep", TypeKey = "flow.sleep",
                    Config = WorkflowsTestSeed.Json($$"""{"seconds":{{seconds}}}"""),
                    Inputs = WorkflowsTestSeed.EmptyJson(),
                    Retry = retry },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "sleep" },
            new() { From = "sleep", To = "end" },
        },
    };
}
