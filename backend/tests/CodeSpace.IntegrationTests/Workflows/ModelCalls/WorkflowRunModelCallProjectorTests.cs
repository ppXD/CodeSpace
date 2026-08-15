using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.ModelCalls;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.ModelCalls;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class WorkflowRunModelCallProjectorTests
{
    private readonly PostgresFixture _fixture;

    public WorkflowRunModelCallProjectorTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Exact_tape_pair_projects_once_with_typed_usage_and_artifact_identity()
    {
        var world = await SeedRunAsync();
        var correlationId = Guid.NewGuid();
        Guid artifactId;
        using (var artifactScope = _fixture.BeginScope())
            artifactId = await artifactScope.Resolve<IArtifactStore>().PutAsync(world.TeamId, "captured response"u8.ToArray(), "text/plain", CancellationToken.None);
        var started = Record(world.RunId, WorkflowRunRecordTypes.InteractionStarted, correlationId, """
            {"kind":"supervisor.decision","provider":"custom","model":"reasoner-v1","prompt":{"system":"sys","user":"usr"}}
            """);
        var terminal = Record(world.RunId, WorkflowRunRecordTypes.InteractionCompleted, correlationId, $$$"""
            {"kind":"supervisor.decision","provider":"custom","model":"reasoner-v1","usage":{"inputTokens":50001,"outputTokens":1234,"finishReason":"stop"},"output":{"$artifact_id":"{{{artifactId:D}}}","size_bytes":200000,"content_type":"text/plain"}}
            """, started.OccurredAt.AddSeconds(3));
        await AddRecordsAsync(started, terminal);

        var first = await SweepAsync(50);
        var second = await SweepAsync(50);

        first.ShouldBe(new WorkflowRunModelCallProjectionResult(1, 0));
        second.ShouldBe(new WorkflowRunModelCallProjectionResult(0, 0));
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var call = await db.WorkflowRunModelCall.AsNoTracking().SingleAsync(value => value.SourceCorrelationId == correlationId);
        call.WorkflowRunId.ShouldBe(world.RunId);
        call.TeamId.ShouldBe(world.TeamId);
        call.SourceKind.ShouldBe(WorkflowRunModelCallProjector.SourceKind);
        call.Purpose.ShouldBe("supervisor.decision/v1");
        call.RequestedProvider.ShouldBe("custom");
        call.RequestedModel.ShouldBe("reasoner-v1");
        call.CaptureCompleteness.ShouldBe(WorkflowRunCaptureCompleteness.Partial);
        var attempt = await db.WorkflowRunModelCallAttempt.AsNoTracking().SingleAsync(value => value.ModelCallId == call.Id);
        attempt.SourceStartedRecordId.ShouldBe(started.Id);
        attempt.SourceTerminalRecordId.ShouldBe(terminal.Id);
        attempt.SourceEvidenceRevision.ShouldBe(1);
        attempt.Status.ShouldBe("Succeeded");
        attempt.InputTokens.ShouldBe(50001);
        attempt.OutputTokens.ShouldBe(1234);
        attempt.FinishReason.ShouldBe("stop");
        attempt.ResponseArtifactId.ShouldBe(artifactId);
        attempt.TransportKind.ShouldBeNull("the legacy tape cannot attest a physical HTTP/CLI transport");
    }

    [Fact]
    public async Task Terminal_without_start_is_partial_then_late_start_attaches_exactly_once()
    {
        var world = await SeedRunAsync();
        var correlationId = Guid.NewGuid();
        var terminal = Record(world.RunId, WorkflowRunRecordTypes.InteractionFailed, correlationId,
            """{"kind":"planner.plan","provider":"custom","error":"gateway down","category":"Transport","failureKind":"provider"}""");
        await AddRecordsAsync(terminal);

        (await SweepAsync(50)).ShouldBe(new WorkflowRunModelCallProjectionResult(1, 0));
        var started = Record(world.RunId, WorkflowRunRecordTypes.InteractionStarted, correlationId,
            """{"kind":"planner.plan","provider":"custom","model":"planner-v2","prompt":{"user":"late"}}""", terminal.OccurredAt.AddSeconds(5));
        await AddRecordsAsync(started);

        (await SweepAsync(50)).ShouldBe(new WorkflowRunModelCallProjectionResult(0, 1));
        (await SweepAsync(50)).ShouldBe(new WorkflowRunModelCallProjectionResult(0, 0));

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var call = await db.WorkflowRunModelCall.AsNoTracking().SingleAsync(value => value.SourceCorrelationId == correlationId);
        call.RequestedModel.ShouldBe("planner-v2");
        var attempt = await db.WorkflowRunModelCallAttempt.AsNoTracking().SingleAsync(value => value.ModelCallId == call.Id);
        attempt.SourceStartedRecordId.ShouldBe(started.Id);
        attempt.SourceEvidenceRevision.ShouldBe(2);
        attempt.Status.ShouldBe("Failed");
        attempt.ErrorCode.ShouldBe("Transport");
        attempt.CompletedAt.HasValue.ShouldBeTrue();
        attempt.StartedAt.ShouldBe(attempt.CompletedAt.Value, "a late observation is clamped to the persisted terminal timestamp and cannot produce an impossible completed-before-started interval");
    }

    [Fact]
    public async Task Malformed_payload_is_visible_as_corrupt_and_never_blocks_a_later_candidate()
    {
        var world = await SeedRunAsync();
        var brokenCorrelation = Guid.NewGuid();
        var validCorrelation = Guid.NewGuid();
        await AddRecordsAsync(
            Record(world.RunId, WorkflowRunRecordTypes.InteractionCompleted, brokenCorrelation, "[]"),
            Record(world.RunId, WorkflowRunRecordTypes.InteractionCompleted, validCorrelation, """{"kind":"grader.oracle","provider":"test","model":"judge"}"""));

        (await SweepAsync(50)).TerminalAttemptsProjected.ShouldBe(2);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var broken = await db.WorkflowRunModelCall.AsNoTracking().SingleAsync(value => value.SourceCorrelationId == brokenCorrelation);
        var valid = await db.WorkflowRunModelCall.AsNoTracking().SingleAsync(value => value.SourceCorrelationId == validCorrelation);
        broken.CaptureCompleteness.ShouldBe(WorkflowRunCaptureCompleteness.Corrupt);
        valid.CaptureCompleteness.ShouldBe(WorkflowRunCaptureCompleteness.Partial);
    }

    [Fact]
    public async Task Malformed_typed_evidence_is_corrupt_and_missing_values_remain_nullable()
    {
        var world = await SeedRunAsync();
        var correlationId = Guid.NewGuid();
        await AddRecordsAsync(Record(world.RunId, WorkflowRunRecordTypes.InteractionCompleted, correlationId,
            """{"kind":42,"provider":{"unexpected":true},"model":"observed-model","usage":{"inputTokens":-1,"outputTokens":"2"},"output":{"$artifact_id":"not-a-guid"}}"""));

        (await SweepAsync(50)).TerminalAttemptsProjected.ShouldBe(1);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var call = await db.WorkflowRunModelCall.AsNoTracking().SingleAsync(value => value.WorkflowRunId == world.RunId && value.SourceCorrelationId == correlationId);
        var attempt = await db.WorkflowRunModelCallAttempt.AsNoTracking().SingleAsync(value => value.ModelCallId == call.Id);
        call.Purpose.ShouldBe("unknown/v1");
        call.CaptureCompleteness.ShouldBe(WorkflowRunCaptureCompleteness.Corrupt);
        attempt.EffectiveProvider.ShouldBeNull();
        attempt.EffectiveModel.ShouldBe("observed-model");
        attempt.InputTokens.ShouldBeNull();
        attempt.OutputTokens.ShouldBeNull();
        attempt.ResponseArtifactId.ShouldBeNull();
        attempt.CaptureCompleteness.ShouldBe(WorkflowRunCaptureCompleteness.Corrupt);
    }

    [Fact]
    public async Task One_tape_correlation_projects_one_physical_observation_and_never_invents_a_retry()
    {
        var world = await SeedRunAsync();
        var correlationId = Guid.NewGuid();
        var completed = Record(world.RunId, WorkflowRunRecordTypes.InteractionCompleted, correlationId,
            """{"kind":"single-observation","provider":"test","model":"m"}""");
        var duplicate = Record(world.RunId, WorkflowRunRecordTypes.InteractionFailed, correlationId,
            """{"kind":"single-observation","provider":"test","failureKind":"provider"}""", completed.OccurredAt.AddSeconds(1));
        await AddRecordsAsync(completed, duplicate);

        (await SweepAsync(50)).ShouldBe(new WorkflowRunModelCallProjectionResult(1, 0));
        await AddRecordsAsync(Record(world.RunId, WorkflowRunRecordTypes.InteractionFailed, correlationId,
            """{"kind":"single-observation","provider":"test","failureKind":"provider"}""", duplicate.OccurredAt.AddSeconds(1)));

        (await SweepAsync(50)).ShouldBe(new WorkflowRunModelCallProjectionResult(0, 0));
        (await SweepAsync(50)).ShouldBe(new WorkflowRunModelCallProjectionResult(0, 0));

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var call = await db.WorkflowRunModelCall.AsNoTracking().SingleAsync(value => value.WorkflowRunId == world.RunId && value.SourceCorrelationId == correlationId);
        var attempt = await db.WorkflowRunModelCallAttempt.AsNoTracking().SingleAsync(value => value.ModelCallId == call.Id);
        attempt.AttemptOrdinal.ShouldBe(1);
        attempt.SourceTerminalRecordId.ShouldBe(completed.Id);
        attempt.Status.ShouldBe("Succeeded");
    }

    [Fact]
    public async Task Artifact_reference_is_admitted_only_when_existing_in_the_exact_team()
    {
        var owner = await SeedRunAsync();
        var foreign = await SeedRunAsync();
        Guid foreignArtifactId;
        using (var scope = _fixture.BeginScope())
            foreignArtifactId = await scope.Resolve<IArtifactStore>().PutAsync(foreign.TeamId, "foreign"u8.ToArray(), "text/plain", CancellationToken.None);
        var missingArtifactId = Guid.NewGuid();
        var missingCorrelation = Guid.NewGuid();
        var foreignCorrelation = Guid.NewGuid();
        await AddRecordsAsync(
            Record(owner.RunId, WorkflowRunRecordTypes.InteractionCompleted, missingCorrelation,
                $$$"""{"kind":"missing-artifact","provider":"test","model":"m","output":{"$artifact_id":"{{{missingArtifactId:D}}}"}}"""),
            Record(owner.RunId, WorkflowRunRecordTypes.InteractionCompleted, foreignCorrelation,
                $$$"""{"kind":"foreign-artifact","provider":"test","model":"m","output":{"$artifact_id":"{{{foreignArtifactId:D}}}"}}"""));

        (await SweepAsync(50)).TerminalAttemptsProjected.ShouldBe(2);

        using var readScope = _fixture.BeginScope();
        var db = readScope.Resolve<CodeSpaceDbContext>();
        var callIds = await db.WorkflowRunModelCall.AsNoTracking().Where(value => value.WorkflowRunId == owner.RunId
                && (value.SourceCorrelationId == missingCorrelation || value.SourceCorrelationId == foreignCorrelation))
            .Select(value => value.Id).ToArrayAsync();
        var attempts = await db.WorkflowRunModelCallAttempt.AsNoTracking().Where(value => callIds.Contains(value.ModelCallId)).ToListAsync();
        attempts.Count.ShouldBe(2);
        attempts.ShouldAllBe(value => value.ResponseArtifactId == null);
        attempts.ShouldAllBe(value => value.CaptureCompleteness == WorkflowRunCaptureCompleteness.Partial);
    }

    [Fact]
    public async Task Shared_correlation_across_runs_keeps_exact_tenant_run_and_source_rows()
    {
        var first = await SeedRunAsync();
        var second = await SeedRunAsync();
        var correlationId = Guid.NewGuid();
        var firstTerminal = Record(first.RunId, WorkflowRunRecordTypes.InteractionCompleted, correlationId,
            """{"kind":"cross-run","provider":"first","model":"m1"}""");
        var secondTerminal = Record(second.RunId, WorkflowRunRecordTypes.InteractionFailed, correlationId,
            """{"kind":"cross-run","provider":"second","failureKind":"provider"}""");
        await AddRecordsAsync(firstTerminal, secondTerminal);

        (await SweepAsync(50)).TerminalAttemptsProjected.ShouldBe(2);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var projected = await (from call in db.WorkflowRunModelCall.AsNoTracking()
                               join attempt in db.WorkflowRunModelCallAttempt.AsNoTracking() on call.Id equals attempt.ModelCallId
                               where call.SourceCorrelationId == correlationId
                               select new { call.TeamId, call.WorkflowRunId, attempt.SourceTerminalRecordId }).ToListAsync();
        projected.Count.ShouldBe(2);
        projected.ShouldContain(value => value.TeamId == first.TeamId && value.WorkflowRunId == first.RunId && value.SourceTerminalRecordId == firstTerminal.Id);
        projected.ShouldContain(value => value.TeamId == second.TeamId && value.WorkflowRunId == second.RunId && value.SourceTerminalRecordId == secondTerminal.Id);
    }

    [Fact]
    public async Task Batch_is_bounded_and_source_antijoin_drains_without_a_sequence_watermark()
    {
        var world = await SeedRunAsync();
        var terminals = Enumerable.Range(0, 5).Select(index => Record(world.RunId, WorkflowRunRecordTypes.InteractionCompleted,
            Guid.NewGuid(), $$$"""{"kind":"batch.{{{index}}}","provider":"test","model":"m"}""")).ToArray();
        await AddRecordsAsync(terminals);

        (await SweepAsync(2)).TerminalAttemptsProjected.ShouldBe(2);
        (await SweepAsync(2)).TerminalAttemptsProjected.ShouldBe(2);
        (await SweepAsync(2)).TerminalAttemptsProjected.ShouldBe(1);
        (await SweepAsync(2)).TerminalAttemptsProjected.ShouldBe(0);

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<CodeSpaceDbContext>().WorkflowRunModelCall.CountAsync(value => value.WorkflowRunId == world.RunId)).ShouldBe(5);
    }

    [Fact]
    public async Task Overlapping_sweeps_serialize_per_source_and_admit_one_attempt()
    {
        var world = await SeedRunAsync();
        var correlationId = Guid.NewGuid();
        await AddRecordsAsync(Record(world.RunId, WorkflowRunRecordTypes.InteractionCompleted, correlationId,
            """{"kind":"concurrent","provider":"test","model":"m"}"""));

        var first = Task.Run(() => SweepAsync(50));
        var second = Task.Run(() => SweepAsync(50));
        var results = await Task.WhenAll(first, second);

        results.Sum(value => value.TerminalAttemptsProjected).ShouldBe(1);
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRunModelCall.CountAsync(value => value.SourceCorrelationId == correlationId)).ShouldBe(1);
        (await db.WorkflowRunModelCallAttempt.CountAsync(value => value.WorkflowRunId == world.RunId)).ShouldBe(1);
    }

    [Fact]
    public async Task Invalid_batch_size_fails_before_querying()
    {
        using var scope = _fixture.BeginScope();
        var projector = scope.Resolve<IWorkflowRunModelCallProjector>();
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => projector.SweepAsync(0, CancellationToken.None));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => projector.SweepAsync(1001, CancellationToken.None));
    }

    private async Task<WorkflowRunModelCallProjectionResult> SweepAsync(int batchSize)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowRunModelCallProjector>().SweepAsync(batchSize, CancellationToken.None);
    }

    private async Task AddRecordsAsync(params WorkflowRunRecord[] records)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.WorkflowRunRecord.AddRange(records);
        await db.SaveChangesAsync();
    }

    private async Task<RunWorld> SeedRunAsync()
    {
        await DrainExistingBacklogAsync();
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        Guid workflowId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            workflowId = await scope.Resolve<MediatR.IMediator>().Send(new CreateWorkflowCommand
            {
                Name = "model-call-projector-" + Guid.NewGuid().ToString("N")[..8],
                Definition = WorkflowsTestSeed.MinimalDefinition(),
                Activations = new List<WorkflowActivationInput>(),
                Enabled = true,
            });
        }

        return new RunWorld(await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId), teamId);
    }

    private async Task DrainExistingBacklogAsync()
    {
        // The real projector is intentionally global. Other integration classes exercise the explicit legacy-fallback
        // reader by leaving valid terminal records unprojected, so this class must establish its own empty-backlog
        // precondition before asserting exact global sweep counts. Never delete append-only facts or depend on test order.
        for (var iteration = 0; iteration < 100; iteration++)
        {
            if ((await SweepAsync(1000)).TotalChanges == 0) return;
        }

        throw new InvalidOperationException("The model-call projection backlog did not drain within 100 bounded sweeps.");
    }

    private static WorkflowRunRecord Record(Guid runId, string recordType, Guid correlationId, string payloadJson, DateTimeOffset? occurredAt = null) => new()
    {
        Id = Guid.NewGuid(), RunId = runId, RecordType = recordType, NodeId = "sup", IterationKey = "sup#turn1",
        CorrelationId = correlationId, OccurredAt = occurredAt ?? DateTimeOffset.UtcNow, PayloadJson = payloadJson,
    };

    private sealed record RunWorld(Guid RunId, Guid TeamId);
}
