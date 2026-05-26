using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Assert the <c>workflow_run_node</c> SQL VIEW projects the ledger into the row shape the
/// run-detail UI / WorkflowService.GetRunAsync consumer expects. The view hides the
/// ledger's complexity faithfully so the rest of the app doesn't need to know it exists.
///
/// Coverage:
///   - Status transitions Running → Success / Failure / Skipped
///   - Inputs come from the FIRST node.started (multiple starts = retry; first wins for inputs)
///   - Outputs come from the LATEST terminal record
///   - StartedAt is the first node.started's occurred_at
///   - CompletedAt is the latest terminal record's occurred_at; NULL for Running rows
///   - The DTO round-trip (GetWorkflowRunQuery → WorkflowRunDetail.Nodes) matches what the
///     SPA expects from the view
///   - Retry semantics: multiple node.started records for the same cell only project to ONE row
/// </summary>
[Collection(PostgresCollection.Name)]
public class RunRecordViewProjectionFlowTests
{
    private readonly PostgresFixture _fixture;

    public RunRecordViewProjectionFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Successful_node_projects_status_Success_and_outputs_populated()
    {
        var runId = await SeedAndRunMinimalAsync();

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var nodes = await db.WorkflowRunNode.AsNoTracking().Where(n => n.RunId == runId).ToListAsync();

        nodes.Count.ShouldBe(2);
        nodes.ShouldAllBe(n => n.Status == NodeStatus.Success);
        nodes.ShouldAllBe(n => n.StartedAt.HasValue);
        nodes.ShouldAllBe(n => n.CompletedAt.HasValue);
        nodes.ShouldAllBe(n => n.CompletedAt.Value >= n.StartedAt.Value);

        // Outputs JSON is populated to an object (may be empty for the terminal with no inputs).
        nodes.ShouldAllBe(n => JsonDocument.Parse(n.OutputsJson).RootElement.ValueKind == JsonValueKind.Object);
    }

    [Fact]
    public async Task Skipped_node_projects_status_Skipped_with_chronological_started_at()
    {
        // The view's started_at is COALESCE(first_started_at, first_occurred_at) so cells
        // that emit only node.skipped / node.failed (never node.started) still get a non-
        // null timestamp for chronological ordering in the UI. A skipped node's started_at
        // and completed_at both come from the same node.skipped record (the only record
        // emitted for the cell), so they should be equal.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var def = BranchedDefinition(condition: "true");
        var workflowId = await CreateWorkflowAsync(teamId, userId, def);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var skipped = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "false_end");
        skipped.Status.ShouldBe(NodeStatus.Skipped);
        skipped.StartedAt.ShouldNotBeNull(
            "skipped nodes expose started_at via the first_occurrence fallback so the UI can order them chronologically");
        skipped.CompletedAt.ShouldNotBeNull("the node.skipped record's occurred_at maps to completed_at");
        skipped.StartedAt.ShouldBe(skipped.CompletedAt,
            "skipped cells have exactly one ledger record (node.skipped) — both started_at and completed_at derive from it");
    }

    [Fact]
    public async Task Failed_node_projects_status_Failure_with_error_populated()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // logic.if with empty condition fails inside RunAsync — the engine catches the
        // returned Failure and emits node.failed.
        var def = new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start",  TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "branch", TypeKey = "logic.if",
                        Config = WorkflowsTestSeed.Json("""{"condition":""}"""),
                        Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start",  To = "branch" },
                new() { From = "branch", To = "end", SourceHandle = "true" },
            },
        };

        var workflowId = await CreateWorkflowAsync(teamId, userId, def);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var failed = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "branch");

        failed.Status.ShouldBe(NodeStatus.Failure);
        failed.Error.ShouldNotBeNullOrEmpty("error column projects from node.failed payload's 'error' field");
        failed.StartedAt.ShouldNotBeNull();
        failed.CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Retry_via_multiple_started_records_projects_to_one_row_with_first_inputs()
    {
        // Simulate a retry: emit two node.started + one node.completed for the same cell.
        // The view's latest-record-wins rule means status comes from .completed, but inputs
        // come from the FIRST .started.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        using (var scope = _fixture.BeginScope())
        {
            var logger = scope.Resolve<IRunRecordLogger>();
            var firstInputs = new Dictionary<string, JsonElement>
            {
                ["attempt"] = JsonSerializer.SerializeToElement("first"),
            };
            var secondInputs = new Dictionary<string, JsonElement>
            {
                ["attempt"] = JsonSerializer.SerializeToElement("second"),
            };
            var emptyConfig = (IReadOnlyDictionary<string, JsonElement>)new Dictionary<string, JsonElement>();
            await logger.NodeStartedAsync(runId, "retried", iterationKey: "", firstInputs, emptyConfig, CancellationToken.None);
            await logger.NodeStartedAsync(runId, "retried", iterationKey: "", secondInputs, emptyConfig, CancellationToken.None);
            await logger.NodeCompletedAsync(runId, "retried", iterationKey: "",
                outputs: new Dictionary<string, JsonElement> { ["result"] = JsonSerializer.SerializeToElement("ok") },
                duration: TimeSpan.FromMilliseconds(5),
                cancellationToken: CancellationToken.None);
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var retried = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "retried");

        retried.Status.ShouldBe(NodeStatus.Success, "latest record (.completed) wins for status");

        var inputs = JsonDocument.Parse(retried.InputsJson).RootElement;
        inputs.GetProperty("attempt").GetString().ShouldBe("first",
            "view projects inputs from the FIRST node.started so the original attempt's resolved inputs are visible — retry attempts are surfaced in the ledger, not in the projection");

        var outputs = JsonDocument.Parse(retried.OutputsJson).RootElement;
        outputs.GetProperty("result").GetString().ShouldBe("ok");
    }

    [Fact]
    public async Task Running_node_with_only_started_record_projects_status_Running_no_completed_at()
    {
        var runId = await SeedAndRunMinimalAsync();

        // Add a third synthetic node that only has a started record (no completion).
        using (var scope = _fixture.BeginScope())
        {
            var logger = scope.Resolve<IRunRecordLogger>();
            await logger.NodeStartedAsync(runId, "still_running", iterationKey: "",
                resolvedInputs: new Dictionary<string, JsonElement>(),
                resolvedConfig: new Dictionary<string, JsonElement>(),
                cancellationToken: CancellationToken.None);
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var stillRunning = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "still_running");

        stillRunning.Status.ShouldBe(NodeStatus.Running,
            "a cell whose latest record is node.started must project as Running");
        stillRunning.StartedAt.ShouldNotBeNull();
        stillRunning.CompletedAt.ShouldBeNull("completed_at only populates for terminal record types");
    }

    [Fact]
    public async Task GetWorkflowRun_DTO_round_trip_matches_view_shape()
    {
        // The end-to-end consumer contract: a UI call hits GetWorkflowRunQuery → handler →
        // WorkflowService.GetRunAsync → reads via _db.WorkflowRunNode (the view) → projects
        // into WorkflowRunDetail.Nodes. The underlying storage is the ledger; the DTO must
        // look like what the SPA expects.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(runId);

        WorkflowRunDetail? detail;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            detail = await mediator.Send(new GetWorkflowRunQuery { RunId = runId });
        }

        detail.ShouldNotBeNull();
        detail!.Nodes.Count.ShouldBe(2);
        detail.Nodes.Select(n => n.NodeId).ShouldBe(new[] { "start", "end" });
        detail.Nodes.ShouldAllBe(n => n.Status == NodeStatus.Success);
        detail.Nodes.ShouldAllBe(n => n.StartedAt.HasValue && n.CompletedAt.HasValue);
        detail.Nodes.ShouldAllBe(n => n.Inputs.ValueKind == JsonValueKind.Object);
        detail.Nodes.ShouldAllBe(n => n.Outputs.ValueKind == JsonValueKind.Object);
    }

    [Fact]
    public async Task Node_ordering_in_view_matches_started_at_for_run_detail_chronology()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        // The WorkflowService.GetRunAsync sorts by StartedAt. The view must surface
        // started_at correctly so that sort is deterministic.
        var ordered = await db.WorkflowRunNode.AsNoTracking()
            .Where(n => n.RunId == runId)
            .OrderBy(n => n.StartedAt)
            .ToListAsync();

        ordered.Count.ShouldBe(2);
        ordered[0].NodeId.ShouldBe("start", "trigger node fires first");
        ordered[1].NodeId.ShouldBe("end", "terminal fires after the trigger");
        ordered[0].StartedAt!.Value.ShouldBeLessThanOrEqualTo(ordered[1].StartedAt!.Value);
    }

    [Fact]
    public async Task Two_runs_view_query_isolates_per_run()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());

        var runIdA = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        var runIdB = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runIdA);
        await RunEngineAsync(runIdB);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var nodesA = await db.WorkflowRunNode.AsNoTracking().Where(n => n.RunId == runIdA).ToListAsync();
        var nodesB = await db.WorkflowRunNode.AsNoTracking().Where(n => n.RunId == runIdB).ToListAsync();

        nodesA.Count.ShouldBe(2);
        nodesB.Count.ShouldBe(2);
        nodesA.ShouldAllBe(n => n.RunId == runIdA);
        nodesB.ShouldAllBe(n => n.RunId == runIdB);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        return await mediator.Send(new CreateWorkflowCommand
        {
            Name = "record-view-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = definition,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task<Guid> SeedAndRunMinimalAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(runId);
        return runId;
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private static WorkflowDefinition BranchedDefinition(string condition) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start",     TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "branch",    TypeKey = "logic.if",
                    Config = WorkflowsTestSeed.Json($$"""{"condition":"{{condition}}"}"""),
                    Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "true_end",  TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "false_end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start",  To = "branch" },
            new() { From = "branch", To = "true_end",  SourceHandle = "true" },
            new() { From = "branch", To = "false_end", SourceHandle = "false" },
        },
    };
}
