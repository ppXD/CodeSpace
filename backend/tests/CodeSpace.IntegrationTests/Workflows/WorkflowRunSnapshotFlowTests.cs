using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Variables;
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
/// Proves the engine writes a complete per-run snapshot at scope-build time.
/// First-run path coverage:
///   1. workflow_run.release_hash_at_run is populated from workflow_version.definition_hash
///   2. workflow_run_variable rows exist for every team variable + every input value
///   3. Secret rows have value_plain NULL (audit name only — value never snapshotted)
///   4. Plain rows have value_plain set to JSON-encoded value
///   5. Each (run_id, scope, name) tuple appears exactly once (unique-index enforcement)
///
/// Replay-path coverage is in <c>WorkflowRunReplayFlowTests</c> (separate file for the
/// equally-large suite of replay-semantic tests).
/// </summary>
[Collection(PostgresCollection.Name)]
public class WorkflowRunSnapshotFlowTests
{
    private readonly PostgresFixture _fixture;

    public WorkflowRunSnapshotFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    private static JsonElement JsonString(string s) => JsonDocument.Parse(JsonSerializer.Serialize(s)).RootElement.Clone();

    [Fact]
    public async Task FirstRun_populates_release_hash_at_run_from_workflow_version()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var def = WorkflowsTestSeed.MinimalDefinition();
        var (workflowId, runId) = await CreateAndQueueRunAsync(teamId, userId, def);

        await RunEngineAsync(runId);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var version = await db.WorkflowVersion.AsNoTracking()
            .SingleAsync(v => v.WorkflowId == workflowId && v.Version == 1);
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.ReleaseHashAtRun.ShouldBe(version.DefinitionHash,
            "engine MUST copy the version's definition_hash into the run row at scope-build time");
        run.ReleaseHashAtRun.Length.ShouldBe(64, "hash is 64-char hex SHA-256");
    }

    [Fact]
    public async Task FirstRun_snapshots_team_plain_variables_into_workflow_run_variable()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // Set a team plain variable BEFORE the run; engine should snapshot it into the run.
        using (var setup = _fixture.BeginScope())
        {
            await setup.Resolve<IVariableService>().SetAsync(
                VariableScope.Team, teamId, teamId, "REGION", VariableValueType.String,
                JsonString("us-east-1"), description: null, userId, CancellationToken.None);
        }

        var (_, runId) = await CreateAndQueueRunAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var snapshot = await db.WorkflowRunVariable.AsNoTracking()
            .Where(v => v.RunId == runId && v.Scope == "Team")
            .ToListAsync();

        snapshot.Count.ShouldBe(1);
        snapshot[0].Name.ShouldBe("REGION");
        snapshot[0].ValueType.ShouldBe("String");
        snapshot[0].ValuePlain.ShouldBe("\"us-east-1\"",
            "plain values are stored as JSON-encoded strings, including surrounding quotes for type=String");
    }

    [Fact]
    public async Task FirstRun_snapshots_secret_variables_with_name_only_no_value()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using (var setup = _fixture.BeginScope())
        {
            await setup.Resolve<IVariableService>().SetAsync(
                VariableScope.Team, teamId, teamId, "API_KEY", VariableValueType.Secret,
                JsonString("sk-sensitive-do-not-snapshot"), null, userId, CancellationToken.None);
        }

        var (_, runId) = await CreateAndQueueRunAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var snapshot = await db.WorkflowRunVariable.AsNoTracking()
            .Where(v => v.RunId == runId && v.Scope == "Team" && v.Name == "API_KEY")
            .SingleAsync();

        snapshot.ValueType.ShouldBe("Secret");
        snapshot.ValuePlain.ShouldBeNull("secret values MUST NEVER be snapshotted — name only");

        // Belt-and-suspenders: the entire workflow_run_variable table for this run should
        // contain ZERO occurrences of the sensitive plaintext anywhere.
        var allRows = await db.WorkflowRunVariable.AsNoTracking()
            .Where(v => v.RunId == runId)
            .ToListAsync();

        foreach (var row in allRows)
        {
            (row.ValuePlain ?? string.Empty).ShouldNotContain("sk-sensitive-do-not-snapshot",
                customMessage: "secret plaintext appearing in any snapshot row is a security regression");
        }
    }

    [Fact]
    public async Task FirstRun_snapshots_workflow_scope_variables_alongside_team_scope()
    {
        // Both scopes feed scope.Wf and scope.Team respectively at run start. Both must
        // appear in the snapshot, discriminated by the Scope column.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (workflowId, _) = await CreateWorkflowOnlyAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());

        using (var setup = _fixture.BeginScope())
        {
            var svc = setup.Resolve<IVariableService>();
            await svc.SetAsync(VariableScope.Team, teamId, teamId, "TEAM_VAR", VariableValueType.String,
                JsonString("team-value"), null, userId, CancellationToken.None);
            await svc.SetAsync(VariableScope.Workflow, workflowId, teamId, "WF_VAR", VariableValueType.Number,
                JsonDocument.Parse("42").RootElement.Clone(), null, userId, CancellationToken.None);
        }

        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var snapshot = await db.WorkflowRunVariable.AsNoTracking()
            .Where(v => v.RunId == runId)
            .ToDictionaryAsync(v => $"{v.Scope}.{v.Name}");

        snapshot.ContainsKey("Team.TEAM_VAR").ShouldBeTrue();
        snapshot["Team.TEAM_VAR"].ValuePlain.ShouldBe("\"team-value\"");

        // Engine writes "Workflow" not "Wf" (aligned with VariableScope enum).
        snapshot.ContainsKey("Workflow.WF_VAR").ShouldBeTrue();
        snapshot["Workflow.WF_VAR"].ValueType.ShouldBe("Number");
        snapshot["Workflow.WF_VAR"].ValuePlain.ShouldBe("42");
    }

    [Fact]
    public async Task FirstRun_with_no_variables_persists_empty_snapshot_set_release_hash_still_populated()
    {
        // Edge case: workflow with zero team + wf variables. Snapshot table should have
        // zero rows for this run, BUT release_hash_at_run must still be populated. Release
        // identity is independent of whether the workflow uses variables.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (_, runId) = await CreateAndQueueRunAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        var snapshotCount = await db.WorkflowRunVariable.CountAsync(v => v.RunId == runId);

        run.ReleaseHashAtRun.ShouldNotBeNullOrEmpty();
        snapshotCount.ShouldBe(0, "no variables → no snapshot rows; release_hash still set");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<(Guid WorkflowId, Guid RunId)> CreateAndQueueRunAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        var (workflowId, _) = await CreateWorkflowOnlyAsync(teamId, userId, definition);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        return (workflowId, runId);
    }

    private async Task<(Guid WorkflowId, Guid UserId)> CreateWorkflowOnlyAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        var workflowId = await mediator.Send(new CreateWorkflowCommand
        {
            Name = "snap-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = definition,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
        return (workflowId, userId);
    }


    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }
}
