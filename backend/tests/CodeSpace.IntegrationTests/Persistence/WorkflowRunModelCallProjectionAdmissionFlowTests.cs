using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

/// <summary>
/// Real-Postgres admission pins for projecting the immutable interaction tape into the Workflow Run model-call
/// plane. The projection remains shadow-only; these tests prove the schema can identify exact source facts,
/// reject cross-scope or ambiguous admission, and version late evidence without treating BIGSERIAL as a commit cursor.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class WorkflowRunModelCallProjectionAdmissionFlowTests
{
    private const string RecordSource = "workflow-run-record/v1";
    private readonly PostgresFixture _fixture;

    public WorkflowRunModelCallProjectionAdmissionFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Exact_started_and_completed_records_admit_one_idempotent_call_attempt()
    {
        var world = await SeedRunAsync();
        var correlationId = Guid.NewGuid();
        var started = Record(world.RunId, WorkflowRunRecordTypes.InteractionStarted, correlationId);
        var terminal = Record(world.RunId, WorkflowRunRecordTypes.InteractionCompleted, correlationId, at: started.OccurredAt.AddSeconds(2));

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunRecord.AddRange(started, terminal);
            await db.SaveChangesAsync();
            var call = Call(world, correlationId);
            db.WorkflowRunModelCall.Add(call);
            db.WorkflowRunModelCallAttempt.Add(Attempt(world, call.Id, started.Id, terminal.Id));
            await db.SaveChangesAsync();
        }

        using (var duplicate = _fixture.BeginScope())
        {
            var db = duplicate.Resolve<CodeSpaceDbContext>();
            var duplicateCall = Call(world, correlationId);
            db.WorkflowRunModelCall.Add(duplicateCall);
            await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        }

        using var verify = _fixture.BeginScope();
        var read = verify.Resolve<CodeSpaceDbContext>();
        (await read.WorkflowRunModelCall.AsNoTracking().SingleAsync(c => c.SourceCorrelationId == correlationId)).SourceKind.ShouldBe(RecordSource);
        var attempt = await read.WorkflowRunModelCallAttempt.AsNoTracking().SingleAsync(a => a.SourceTerminalRecordId == terminal.Id);
        attempt.SourceStartedRecordId.ShouldBe(started.Id);
        attempt.SourceEvidenceRevision.ShouldBe(1);

        using var mutation = _fixture.BeginScope();
        var mutationDb = mutation.Resolve<CodeSpaceDbContext>();
        var projected = await mutationDb.WorkflowRunModelCall.SingleAsync(c => c.Id == attempt.ModelCallId);
        projected.SourceCorrelationId = Guid.NewGuid();
        await mutationDb.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>("source identity is immutable after idempotent admission");
    }

    [Fact]
    public async Task Failed_is_a_valid_terminal_but_non_terminal_or_mismatched_correlation_fails_closed()
    {
        var world = await SeedRunAsync();
        var correlationId = Guid.NewGuid();
        var started = Record(world.RunId, WorkflowRunRecordTypes.InteractionStarted, correlationId);
        var failed = Record(world.RunId, WorkflowRunRecordTypes.InteractionFailed, correlationId);
        var deltaCorrelationId = Guid.NewGuid();
        var deltaStarted = Record(world.RunId, WorkflowRunRecordTypes.InteractionStarted, deltaCorrelationId);
        var delta = Record(world.RunId, WorkflowRunRecordTypes.InteractionDelta, deltaCorrelationId);
        var mismatchedCorrelationId = Guid.NewGuid();
        var mismatchedStarted = Record(world.RunId, WorkflowRunRecordTypes.InteractionStarted, mismatchedCorrelationId);
        var wrong = Record(world.RunId, WorkflowRunRecordTypes.InteractionCompleted, Guid.NewGuid());

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunRecord.AddRange(started, failed, deltaStarted, delta, mismatchedStarted, wrong);
            await db.SaveChangesAsync();

            var accepted = Call(world, correlationId);
            db.WorkflowRunModelCall.Add(accepted);
            db.WorkflowRunModelCallAttempt.Add(Attempt(world, accepted.Id, started.Id, failed.Id, status: "Failed"));
            await db.SaveChangesAsync();
        }

        await AssertAttemptRejectedAsync(world, deltaCorrelationId, deltaStarted.Id, delta.Id, "interaction.delta is progressive evidence, never a terminal");
        await AssertAttemptRejectedAsync(world, mismatchedCorrelationId, mismatchedStarted.Id, wrong.Id, "a terminal from another correlation must not be paired by proximity or sequence");
    }

    [Fact]
    public async Task Foreign_run_and_foreign_team_sources_are_rejected_at_the_database_boundary()
    {
        var owner = await SeedRunAsync();
        var foreign = await SeedRunAsync();
        var correlationId = Guid.NewGuid();
        var foreignTerminal = Record(foreign.RunId, WorkflowRunRecordTypes.InteractionCompleted, correlationId);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunRecord.Add(foreignTerminal);
            await db.SaveChangesAsync();

            var call = Call(owner, correlationId);
            db.WorkflowRunModelCall.Add(call);
            db.WorkflowRunModelCallAttempt.Add(Attempt(owner, call.Id, startedRecordId: null, foreignTerminal.Id));
            await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        }

        using var wrongOwner = _fixture.BeginScope();
        var wrongDb = wrongOwner.Resolve<CodeSpaceDbContext>();
        wrongDb.WorkflowRunModelCall.Add(Call(owner with { TeamId = foreign.TeamId }, Guid.NewGuid()));
        await wrongDb.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Late_started_evidence_advances_exactly_one_revision_and_cannot_be_removed_or_replaced()
    {
        var world = await SeedRunAsync();
        var correlationId = Guid.NewGuid();
        var terminal = Record(world.RunId, WorkflowRunRecordTypes.InteractionCompleted, correlationId);
        Guid attemptId;

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunRecord.Add(terminal);
            await db.SaveChangesAsync();
            var call = Call(world, correlationId);
            var attempt = Attempt(world, call.Id, startedRecordId: null, terminal.Id);
            attemptId = attempt.Id;
            db.WorkflowRunModelCall.Add(call);
            db.WorkflowRunModelCallAttempt.Add(attempt);
            await db.SaveChangesAsync();
        }

        var lateStarted = Record(world.RunId, WorkflowRunRecordTypes.InteractionStarted, correlationId, at: terminal.OccurredAt.AddSeconds(5));
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunRecord.Add(lateStarted);
            await db.SaveChangesAsync();
            var attempt = await db.WorkflowRunModelCallAttempt.SingleAsync(a => a.Id == attemptId);
            attempt.SourceStartedRecordId = lateStarted.Id;
            attempt.SourceEvidenceRevision = 2;
            attempt.CaptureCompleteness = WorkflowRunCaptureCompleteness.Partial;
            await db.SaveChangesAsync();
        }

        using (var stale = _fixture.BeginScope())
        {
            var db = stale.Resolve<CodeSpaceDbContext>();
            var attempt = await db.WorkflowRunModelCallAttempt.SingleAsync(a => a.Id == attemptId);
            attempt.SourceStartedRecordId = null;
            attempt.SourceEvidenceRevision = 3;
            await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        }

        var replacement = Record(world.RunId, WorkflowRunRecordTypes.InteractionStarted, correlationId);
        using (var replace = _fixture.BeginScope())
        {
            var db = replace.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunRecord.Add(replacement);
            await db.SaveChangesAsync();
            var attempt = await db.WorkflowRunModelCallAttempt.SingleAsync(a => a.Id == attemptId);
            attempt.SourceStartedRecordId = replacement.Id;
            attempt.SourceEvidenceRevision = 3;
            await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        }
    }

    [Fact]
    public async Task Source_terminal_is_single_admission_even_when_two_workers_choose_different_attempt_ids()
    {
        var world = await SeedRunAsync();
        var correlationId = Guid.NewGuid();
        var terminal = Record(world.RunId, WorkflowRunRecordTypes.InteractionFailed, correlationId);

        using (var seed = _fixture.BeginScope())
        {
            var db = seed.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunRecord.Add(terminal);
            var call = Call(world, correlationId);
            db.WorkflowRunModelCall.Add(call);
            await db.SaveChangesAsync();
        }

        var callId = await ReadCallIdAsync(correlationId);
        using (var first = _fixture.BeginScope())
        {
            var db = first.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunModelCallAttempt.Add(Attempt(world, callId, startedRecordId: null, terminal.Id, status: "Failed"));
            await db.SaveChangesAsync();
        }

        using var replay = _fixture.BeginScope();
        var replayDb = replay.Resolve<CodeSpaceDbContext>();
        var second = Attempt(world, callId, startedRecordId: null, terminal.Id, status: "Failed");
        second.AttemptOrdinal = 2;
        replayDb.WorkflowRunModelCallAttempt.Add(second);
        await replayDb.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Concurrent_late_evidence_writers_compare_the_same_revision_and_only_one_commits()
    {
        var world = await SeedRunAsync();
        var correlationId = Guid.NewGuid();
        var terminal = Record(world.RunId, WorkflowRunRecordTypes.InteractionCompleted, correlationId);
        var started = Record(world.RunId, WorkflowRunRecordTypes.InteractionStarted, correlationId);
        Guid attemptId;

        using (var seed = _fixture.BeginScope())
        {
            var db = seed.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunRecord.AddRange(terminal, started);
            await db.SaveChangesAsync();
            var call = Call(world, correlationId);
            var attempt = Attempt(world, call.Id, startedRecordId: null, terminal.Id);
            attemptId = attempt.Id;
            db.WorkflowRunModelCall.Add(call);
            db.WorkflowRunModelCallAttempt.Add(attempt);
            await db.SaveChangesAsync();
        }

        using var firstScope = _fixture.BeginScope();
        using var secondScope = _fixture.BeginScope();
        var firstDb = firstScope.Resolve<CodeSpaceDbContext>();
        var secondDb = secondScope.Resolve<CodeSpaceDbContext>();
        var first = await firstDb.WorkflowRunModelCallAttempt.SingleAsync(a => a.Id == attemptId);
        var second = await secondDb.WorkflowRunModelCallAttempt.SingleAsync(a => a.Id == attemptId);

        first.SourceStartedRecordId = started.Id;
        first.SourceEvidenceRevision = 2;
        second.SourceStartedRecordId = started.Id;
        second.SourceEvidenceRevision = 2;

        await firstDb.SaveChangesAsync();
        await secondDb.SaveChangesAsync().ShouldThrowAsync<DbUpdateConcurrencyException>();
    }

    private async Task AssertAttemptRejectedAsync(RunWorld world, Guid correlationId, Guid? startedRecordId, Guid terminalRecordId, string because)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var call = Call(world, correlationId);
        db.WorkflowRunModelCall.Add(call);
        db.WorkflowRunModelCallAttempt.Add(Attempt(world, call.Id, startedRecordId, terminalRecordId));
        await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>(because);
    }

    private async Task<Guid> ReadCallIdAsync(Guid correlationId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunModelCall.AsNoTracking()
            .Where(c => c.SourceCorrelationId == correlationId)
            .Select(c => c.Id)
            .SingleAsync();
    }

    private async Task<RunWorld> SeedRunAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        Guid workflowId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            workflowId = await scope.Resolve<MediatR.IMediator>().Send(new CreateWorkflowCommand
            {
                Name = "model-call-projection-" + Guid.NewGuid().ToString("N")[..8],
                Definition = WorkflowsTestSeed.MinimalDefinition(),
                Activations = new List<WorkflowActivationInput>(),
                Enabled = true,
            });
        }

        return new RunWorld(await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId), teamId);
    }

    private static WorkflowRunRecord Record(Guid runId, string type, Guid correlationId, DateTimeOffset? at = null) => new()
    {
        Id = Guid.NewGuid(), RunId = runId, RecordType = type, NodeId = "sup", IterationKey = "sup#turn1",
        CorrelationId = correlationId, OccurredAt = at ?? DateTimeOffset.UtcNow, PayloadJson = "{}",
    };

    private static WorkflowRunModelCall Call(RunWorld world, Guid correlationId) => new()
    {
        Id = Guid.NewGuid(), TeamId = world.TeamId, WorkflowRunId = world.RunId, NodeId = "sup", IterationKey = "sup#turn1",
        CallOrdinal = 1, Purpose = "unknown/v1", CaptureSource = RecordSource,
        CaptureCompleteness = WorkflowRunCaptureCompleteness.Partial, SchemaVersion = WorkflowRunDataContract.CurrentVersion,
        SourceKind = RecordSource, SourceCorrelationId = correlationId,
    };

    private static WorkflowRunModelCallAttempt Attempt(RunWorld world, Guid callId, Guid? startedRecordId, Guid terminalRecordId, string status = "Succeeded") => new()
    {
        Id = Guid.NewGuid(), TeamId = world.TeamId, WorkflowRunId = world.RunId, ModelCallId = callId, AttemptOrdinal = 1,
        Status = status, CaptureSource = RecordSource, CaptureCompleteness = WorkflowRunCaptureCompleteness.Partial,
        StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow, SchemaVersion = WorkflowRunDataContract.CurrentVersion,
        SourceStartedRecordId = startedRecordId, SourceTerminalRecordId = terminalRecordId, SourceEvidenceRevision = 1,
    };

    private sealed record RunWorld(Guid RunId, Guid TeamId);
}
