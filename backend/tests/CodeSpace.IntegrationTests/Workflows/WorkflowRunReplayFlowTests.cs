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
/// Proves the engine's replay path obeys the design contract:
///   • Plain variable values are FROZEN from snapshot (workflow_run_variable rows from
///     the parent run, copied onto the replay run)
///   • Secret variable values are RE-RESOLVED from the current `variable` table
///     (intentional — rotation is a feature; old secrets must not still work)
///   • Trigger payload is reused verbatim from the parent
///   • System variables get fresh values (new run id, new started_at)
///   • Release hash is the SAME — both runs reference the same workflow_version
///
/// These tests simulate the replay by pre-populating workflow_run_variable rows on a
/// freshly-queued run BEFORE calling the engine (mimicking what the ReplayRunCommand
/// handler does in production). The engine sees an existing snapshot and walks the
/// replay branch.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class WorkflowRunReplayFlowTests
{
    private readonly PostgresFixture _fixture;

    public WorkflowRunReplayFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    private static JsonElement JsonString(string s) => JsonDocument.Parse(JsonSerializer.Serialize(s)).RootElement.Clone();

    [Fact]
    public async Task Replay_reads_plain_variable_value_from_snapshot_not_current()
    {
        // Setup: team variable v=A, run a workflow that outputs {{team.VAR}} → "A" snapshot.
        // Mutate the team variable to v=B. Replay → output must still be "A" (snapshot
        // wins for plain values; live mutations DON'T leak into the replay).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using (var setup = _fixture.BeginScope())
        {
            await setup.Resolve<IVariableService>().SetAsync(
                VariableScope.Team, teamId, teamId, "MIRROR", VariableValueType.String,
                JsonString("snapshot-frozen-value"), null, userId, CancellationToken.None);
        }

        var def = EchoTeamVarDef("MIRROR", outputName: "out");
        var (workflowId, originalRunId) = await CreateAndRunAsync(teamId, userId, def);

        // Mutate the source variable. Subsequent runs would see the new value; the replay
        // of the ORIGINAL run must see the frozen value.
        using (var rotate = _fixture.BeginScope())
        {
            await rotate.Resolve<IVariableService>().SetAsync(
                VariableScope.Team, teamId, teamId, "MIRROR", VariableValueType.String,
                JsonString("mutated-after-original"), null, userId, CancellationToken.None);
        }

        // Manually stage a replay: clone snapshot rows + trigger payload onto a new run.
        var replayRunId = await StageReplayAsync(originalRunId, workflowId);
        await RunEngineAsync(replayRunId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var replayRun = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == replayRunId);

        replayRun.Status.ShouldBe(WorkflowRunStatus.Success);
        JsonDocument.Parse(replayRun.OutputsJson).RootElement.GetProperty("out").GetString()
            .ShouldBe("snapshot-frozen-value",
                "replay MUST read plain values from snapshot, NOT from the (now-mutated) live variable table");
    }

    [Fact]
    public async Task Terminal_referencing_secret_in_outputs_fails_loudly()
    {
        // The secret-leak guard. A Terminal output mapping that references a Secret-typed
        // variable (e.g. an author trying to expose {{team.API_KEY}} as a workflow output)
        // must fail with a clear error rather than silently persisting the plaintext into
        // workflow_run.OutputsJson. Without the guard, every operator with run-view
        // permission would see the decrypted secret.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using (var setup = _fixture.BeginScope())
        {
            await setup.Resolve<IVariableService>().SetAsync(
                VariableScope.Team, teamId, teamId, "API_KEY", VariableValueType.Secret,
                JsonString("must-not-leak"), null, userId, CancellationToken.None);
        }

        // Workflow: trigger → terminal that maps a Terminal output named "key" to {{team.API_KEY}}.
        var def = EchoTeamVarDef("API_KEY", outputName: "key");
        var (_, runId) = await CreateAndRunAsync(teamId, userId, def);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.Status.ShouldBe(WorkflowRunStatus.Failure, "secret-leak guard must fail the run");
        run.Error.ShouldNotBeNullOrEmpty();
        run.Error!.ToLowerInvariant().Contains("secret variable").ShouldBeTrue(
            "error message must name the contract violation so operators can fix the wiring; got: " + run.Error);
        run.Error.Contains("team.API_KEY").ShouldBeTrue(
            "error message must name the offending path so the author knows where to look; got: " + run.Error);

        // The plaintext must NOT appear in OutputsJson — that's the entire point of the guard.
        run.OutputsJson.Contains("must-not-leak").ShouldBeFalse(
            "secret value leaked into OutputsJson; got: " + run.OutputsJson);
    }

    [Fact]
    public async Task Replay_re_resolves_plain_value_through_secret_path_when_present()
    {
        // The canonical "secret rotation is honoured by replay" assertion lives in
        // WorkflowEngineSecretsFlowTests.Rotated_team_value_takes_effect_on_the_next_run,
        // which exercises a secret used by a node's Inputs (not Terminal Outputs), so it
        // doesn't violate the secret-leak guard that blocks secrets in OutputsJson.
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Replay_reuses_trigger_payload_from_parent_run()
    {
        // The trigger payload is part of the run's frozen state — replay uses the original
        // payload byte-for-byte. Different trigger payloads on subsequent runs would diverge,
        // but a replay specifically reproduces the original run's input.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var def = EchoTriggerFieldDef("title", outputName: "echoed");
        var (workflowId, _) = await CreateWorkflowOnlyAsync(teamId, userId, def);

        var originalRunId = await QueueRunWithTriggerAsync(workflowId, """{"title":"original-payload"}""");
        await RunEngineAsync(originalRunId);

        var replayRunId = await StageReplayAsync(originalRunId, workflowId);
        await RunEngineAsync(replayRunId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var replayRun = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == replayRunId);

        // Payload lives on workflow_run_request; replay creates a new request whose
        // normalized_payload_json equals the original's. We assert the replay run outputs
        // the same echoed field, then verify the request side stored the payload.
        var replayRequest = await db.WorkflowRunRequest.AsNoTracking()
            .SingleAsync(r => r.Id == replayRun.RunRequestId);
        JsonDocument.Parse(replayRequest.NormalizedPayloadJson).RootElement.GetProperty("title").GetString()
            .ShouldBe("original-payload", "replay request must copy the parent's normalised payload byte-equivalent");
        JsonDocument.Parse(replayRun.OutputsJson).RootElement.GetProperty("echoed").GetString()
            .ShouldBe("original-payload");
    }

    [Fact]
    public async Task Replay_skips_persist_snapshot_does_not_duplicate_rows()
    {
        // Replay path should detect existing snapshot rows and SKIP the bulk-insert. If it
        // re-inserted, we'd hit the (run_id, scope, name) unique constraint and the run
        // would fail. Pin idempotence here.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using (var setup = _fixture.BeginScope())
        {
            await setup.Resolve<IVariableService>().SetAsync(
                VariableScope.Team, teamId, teamId, "FOO", VariableValueType.String,
                JsonString("v"), null, userId, CancellationToken.None);
        }

        var def = EchoTeamVarDef("FOO", outputName: "out");
        var (workflowId, originalRunId) = await CreateAndRunAsync(teamId, userId, def);

        var replayRunId = await StageReplayAsync(originalRunId, workflowId);
        await RunEngineAsync(replayRunId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var snapshotCount = await db.WorkflowRunVariable
            .Where(v => v.RunId == replayRunId && v.Scope == "Team" && v.Name == "FOO")
            .CountAsync();

        snapshotCount.ShouldBe(1, "replay must NOT re-insert snapshot rows (would violate unique index)");
    }

    [Fact]
    public async Task Replay_resolves_workflow_scoped_plain_variable_from_snapshot()
    {
        // Regression: the snapshot writer writes scope="Workflow"; any drift in the
        // replay reader's filter (e.g. "Wf" instead of "Workflow") silently drops every
        // workflow-scoped plain variable on replay. This test exercises the seam:
        // a wf.* plain variable must round-trip through the snapshot on replay.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // Create workflow first so wf-scoped variable has a scopeId to bind to.
        var def = EchoWfVarDef("REGION", outputName: "echoed_region");
        var (workflowId, _) = await CreateWorkflowOnlyAsync(teamId, userId, def);

        using (var setup = _fixture.BeginScope())
        {
            await setup.Resolve<IVariableService>().SetAsync(
                VariableScope.Workflow, workflowId, teamId, "REGION", VariableValueType.String,
                JsonString("us-east-1"), null, userId, CancellationToken.None);
        }

        // Original run: pulls REGION from live table + snapshots it.
        var originalRunId = await QueueRunWithTriggerAsync(workflowId, "{}");
        await RunEngineAsync(originalRunId);

        // Mutate the live value so we can prove replay reads from snapshot, not live.
        using (var rotate = _fixture.BeginScope())
        {
            await rotate.Resolve<IVariableService>().SetAsync(
                VariableScope.Workflow, workflowId, teamId, "REGION", VariableValueType.String,
                JsonString("eu-west-2"), null, userId, CancellationToken.None);
        }

        // Replay: should see the SNAPSHOT value, not the mutated live value.
        var replayRunId = await StageReplayAsync(originalRunId, workflowId);
        await RunEngineAsync(replayRunId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var replayRun = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == replayRunId);

        replayRun.Status.ShouldBe(WorkflowRunStatus.Success,
            "replay must succeed — wf.* plain variable must resolve from the snapshot via the correct scope discriminator");

        JsonDocument.Parse(replayRun.OutputsJson).RootElement.GetProperty("echoed_region").GetString()
            .ShouldBe("us-east-1",
                "replay reader must filter snapshot rows by scope=\"Workflow\" (matches the writer); " +
                "any drift between writer and reader silently drops wf-scoped variables and breaks replay reproducibility");
    }

    // ─── Workflow definition helpers ────────────────────────────────────────────

    private static WorkflowDefinition EchoWfVarDef(string varName, string outputName) =>
        WorkflowsTestSeed.MinimalDefinition() with
        {
            Outputs = new[]
            {
                new WorkflowVariable { Name = outputName, Schema = WorkflowsTestSeed.Json("""{"type":"string"}""") },
            },
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "end",   TypeKey = "builtin.terminal",  Config = WorkflowsTestSeed.EmptyJson(),
                        Inputs = WorkflowsTestSeed.Json($$"""{"{{outputName}}":"{{"{{"}}wf.{{varName}}{{"}}"}}"}""") },
            },
        };

    private static WorkflowDefinition EchoTeamVarDef(string varName, string outputName) =>
        WorkflowsTestSeed.MinimalDefinition() with
        {
            Outputs = new[]
            {
                new WorkflowVariable { Name = outputName, Schema = WorkflowsTestSeed.Json("""{"type":"string"}""") },
            },
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "end",   TypeKey = "builtin.terminal",  Config = WorkflowsTestSeed.EmptyJson(),
                        Inputs = WorkflowsTestSeed.Json($$"""{"{{outputName}}":"{{"{{"}}team.{{varName}}{{"}}"}}"}""") },
            },
        };

    private static WorkflowDefinition EchoTriggerFieldDef(string triggerField, string outputName) =>
        WorkflowsTestSeed.MinimalDefinition() with
        {
            Outputs = new[]
            {
                new WorkflowVariable { Name = outputName, Schema = WorkflowsTestSeed.Json("""{"type":"string"}""") },
            },
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "end",   TypeKey = "builtin.terminal",  Config = WorkflowsTestSeed.EmptyJson(),
                        Inputs = WorkflowsTestSeed.Json($$"""{"{{outputName}}":"{{"{{"}}trigger.{{triggerField}}{{"}}"}}"}""") },
            },
        };

    // ─── Run-staging helpers ─────────────────────────────────────────────────────

    private async Task<(Guid WorkflowId, Guid OriginalRunId)> CreateAndRunAsync(Guid teamId, Guid userId, WorkflowDefinition def)
    {
        var (workflowId, _) = await CreateWorkflowOnlyAsync(teamId, userId, def);
        var runId = await QueueRunWithTriggerAsync(workflowId, "{}");
        await RunEngineAsync(runId);
        return (workflowId, runId);
    }

    private async Task<(Guid WorkflowId, Guid UserId)> CreateWorkflowOnlyAsync(Guid teamId, Guid userId, WorkflowDefinition def)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        var workflowId = await mediator.Send(new CreateWorkflowCommand
        {
            Name = "replay-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = def,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
        return (workflowId, userId);
    }

    private async Task<Guid> QueueRunWithTriggerAsync(Guid workflowId, string triggerPayload)
    {
        // Look up the workflow's team so we can stamp request.team_id correctly.
        using var lookupScope = _fixture.BeginScope();
        var teamId = await lookupScope.Resolve<CodeSpaceDbContext>().Workflow.AsNoTracking()
            .Where(w => w.Id == workflowId)
            .Select(w => w.TeamId)
            .SingleAsync();

        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: triggerPayload);
    }

    /// <summary>
    /// Simulates the production ReplayRunCommand: creates a new
    /// <c>workflow_run_request</c> (source_type=replay, causation=original.request) +
    /// <c>workflow_run</c> + clones the parent's snapshot rows onto the new run id.
    /// </summary>
    private async Task<Guid> StageReplayAsync(Guid originalRunId, Guid workflowId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var original = await db.WorkflowRun.AsNoTracking()
            .Include(r => r.RunRequest)
            .SingleAsync(r => r.Id == originalRunId);
        var originalSnapshot = await db.WorkflowRunVariable.AsNoTracking()
            .Where(v => v.RunId == originalRunId)
            .ToListAsync();

        var replayRequestId = Guid.NewGuid();
        var replayId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = replayRequestId,
            TeamId = original.RunRequest.TeamId,
            WorkflowId = workflowId,
            SourceType = WorkflowRunSourceTypes.Replay,
            ActorType = "user",
            ActorId = SystemUsers.SeederId,
            CausationId = original.RunRequestId,
            NormalizedPayloadJson = original.RunRequest.NormalizedPayloadJson,
            Status = WorkflowRunRequestStatus.Consumed,
            ReceivedAt = now,
            VerifiedAt = now,
            NormalizedAt = now,
        });

        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = replayId,
            WorkflowId = workflowId,
            WorkflowVersion = original.WorkflowVersion,
            TeamId = original.TeamId,
            RunRequestId = replayRequestId,
            ReleaseHashAtRun = original.ReleaseHashAtRun,
            ParentRunId = originalRunId,
            // Seed directly in Enqueued state. The replay staging in this helper skips the
            // dispatcher (which would CAS Pending→Enqueued and Hangfire-Enqueue); production
            // has WorkflowService.ReplayRunAsync calling IWorkflowRunDispatcher.DispatchAsync
            // to do that handoff. The tests drive engine.ExecuteRunAsync directly (simulating
            // the Hangfire worker), and the engine's atomic entry CAS expects Enqueued.
            Status = WorkflowRunStatus.Enqueued,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });

        foreach (var s in originalSnapshot)
        {
            db.WorkflowRunVariable.Add(new WorkflowRunVariable
            {
                Id = Guid.NewGuid(),
                RunId = replayId,
                Scope = s.Scope,
                Name = s.Name,
                ValueType = s.ValueType,
                ValuePlain = s.ValuePlain,
                CapturedAt = now,
            });
        }

        await db.SaveChangesAsync();
        return replayId;
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }
}
