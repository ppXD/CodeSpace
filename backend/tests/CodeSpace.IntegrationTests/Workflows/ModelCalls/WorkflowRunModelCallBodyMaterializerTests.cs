using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.ModelCalls;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows.ModelCalls;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.ModelCalls;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class WorkflowRunModelCallBodyMaterializerTests
{
    private readonly PostgresFixture _fixture;

    public WorkflowRunModelCallBodyMaterializerTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Large_utf8_prompt_and_response_materialize_without_truncation_and_settle_refs_atomically()
    {
        var world = await SeedRunAsync();
        var correlationId = Guid.NewGuid();
        var prompt = new string('界', 60000);
        var response = new string('答', 60000);
        var started = Record(world.RunId, WorkflowRunRecordTypes.InteractionStarted, correlationId,
            JsonSerializer.Serialize(new { kind = "large-body", prompt }));
        var terminal = Record(world.RunId, WorkflowRunRecordTypes.InteractionCompleted, correlationId,
            JsonSerializer.Serialize(new { kind = "large-body", output = response }), started.OccurredAt.AddSeconds(1));
        await AddRecordsAsync(started, terminal);
        await ProjectAsync();

        var result = await MaterializeAsync(world.RunId);

        result.Claimed.ShouldBe(2);
        result.Available.ShouldBe(2);
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var call = await db.WorkflowRunModelCall.AsNoTracking().SingleAsync(value => value.SourceCorrelationId == correlationId);
        var attempt = await db.WorkflowRunModelCallAttempt.AsNoTracking().SingleAsync(value => value.ModelCallId == call.Id);
        call.RequestArtifactId.ShouldNotBeNull();
        attempt.ResponseArtifactId.ShouldNotBeNull();
        attempt.SourceEvidenceRevision.ShouldBe(1, "body settlement is not late source admission and cannot forge a late-start revision");
        var artifacts = scope.Resolve<IArtifactStore>();
        (await artifacts.GetBytesAsync(world.TeamId, call.RequestArtifactId.Value, CancellationToken.None))!.Bytes.AsSpan(0, 8)
            .SequenceEqual("CSMCB1S\n"u8).ShouldBeTrue();
        (await ReadBodyAsync(scope.Resolve<IWorkflowRunModelCallReader>(), world, call.Id, null,
            WorkflowRunModelCallBody.LogicalRequest)).ShouldBe((prompt, "text/plain; charset=utf-8"));
        (await ReadBodyAsync(scope.Resolve<IWorkflowRunModelCallReader>(), world, call.Id, attempt.Id,
            WorkflowRunModelCallBody.AttemptResponse)).ShouldBe((response, "text/plain; charset=utf-8"));
        foreach (var invalidOffset in new[] { long.MaxValue - 7, long.MaxValue })
        {
            var invalid = await scope.Resolve<IWorkflowRunModelCallReader>().ReadBodyAsync(
                new WorkflowRunModelCallBodyReadRequest(world.RunId, call.Id, world.TeamId, WorkflowRunModelCallBody.AttemptResponse)
                {
                    AttemptId = attempt.Id,
                    OffsetBytes = invalidOffset,
                }, CancellationToken.None);
            invalid.ShouldNotBeNull();
            invalid!.Availability.ShouldBe(WorkflowRunModelCallPartAvailability.InvalidOffset,
                "typed-envelope offset translation must reject instead of wrapping long.MaxValue into a provider read");
        }
        var captures = await db.WorkflowRunModelCallBodyCapture.AsNoTracking().Where(value => value.ModelCallAttemptId == attempt.Id).ToListAsync();
        captures.ShouldAllBe(value => value.State == WorkflowRunModelCallBodyCaptureState.Available && value.LeaseOwnerId == null
            && value.TerminalAt != null && value.MaterializationFormat == WorkflowRunModelCallBodyMaterializationFormats.Utf8StringEnvelope);
        var metadata = await scope.Resolve<IWorkflowRunModelCallReader>().ReadByIdAsync(world.RunId, call.Id, world.TeamId, CancellationToken.None);
        metadata!.Bodies.Single().CaptureHealth.ShouldBe(WorkflowRunModelCallBodyCaptureHealth.Available);
        metadata.Bodies.Single().MaterializationFormat.ShouldBe(WorkflowRunModelCallBodyMaterializationFormats.Utf8StringEnvelope);
        var responseDescriptor = metadata.Attempts.Single().Bodies.Single(value => value.Body == WorkflowRunModelCallBody.AttemptResponse);
        responseDescriptor.CaptureHealth.ShouldBe(WorkflowRunModelCallBodyCaptureHealth.Available);
        responseDescriptor.MaterializationFormat.ShouldBe(WorkflowRunModelCallBodyMaterializationFormats.Utf8StringEnvelope);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Json_string_and_number_with_identical_logical_bytes_never_share_CAS_identity_or_media_type(bool reversePreseed)
    {
        var world = await SeedRunAsync();
        var stringCorrelation = Guid.NewGuid();
        var numberCorrelation = Guid.NewGuid();
        await AddRecordsAsync(
            Record(world.RunId, WorkflowRunRecordTypes.InteractionCompleted, stringCorrelation, """{"kind":"string-body","output":"123"}"""),
            Record(world.RunId, WorkflowRunRecordTypes.InteractionCompleted, numberCorrelation, """{"kind":"number-body","output":123}"""));
        await ProjectAsync();

        Guid stringArtifactId;
        Guid numberArtifactId;
        using (var scope = _fixture.BeginScope())
        {
            var writer = scope.Resolve<IWorkflowRunModelCallBodyArtifactWriter>();
            var stringBytes = "CSMCB1S\n123"u8.ToArray();
            var numberBytes = "CSMCB1J\n123"u8.ToArray();
            var first = reversePreseed ? numberBytes : stringBytes;
            var second = reversePreseed ? stringBytes : numberBytes;
            var firstMetadata = await writer.PutAsync(world.TeamId, first,
                WorkflowRunModelCallBodyMaterializationFormats.EnvelopeContentType, CancellationToken.None);
            var secondMetadata = await writer.PutAsync(world.TeamId, second,
                WorkflowRunModelCallBodyMaterializationFormats.EnvelopeContentType, CancellationToken.None);
            stringArtifactId = reversePreseed ? secondMetadata.Id : firstMetadata.Id;
            numberArtifactId = reversePreseed ? firstMetadata.Id : secondMetadata.Id;
        }

        (await MaterializeAsync(world.RunId)).Available.ShouldBe(2);
        using var readScope = _fixture.BeginScope();
        var db = readScope.Resolve<CodeSpaceDbContext>();
        var stringCall = await db.WorkflowRunModelCall.AsNoTracking().SingleAsync(value => value.SourceCorrelationId == stringCorrelation);
        var numberCall = await db.WorkflowRunModelCall.AsNoTracking().SingleAsync(value => value.SourceCorrelationId == numberCorrelation);
        var stringAttempt = await db.WorkflowRunModelCallAttempt.AsNoTracking().SingleAsync(value => value.ModelCallId == stringCall.Id);
        var numberAttempt = await db.WorkflowRunModelCallAttempt.AsNoTracking().SingleAsync(value => value.ModelCallId == numberCall.Id);
        stringAttempt.ResponseArtifactId.ShouldBe(stringArtifactId);
        numberAttempt.ResponseArtifactId.ShouldBe(numberArtifactId);
        stringArtifactId.ShouldNotBe(numberArtifactId, "typed envelope bytes domain-separate JSON strings from JSON scalars");
        var reader = readScope.Resolve<IWorkflowRunModelCallReader>();
        (await ReadBodyAsync(reader, world, stringCall.Id, stringAttempt.Id, WorkflowRunModelCallBody.AttemptResponse))
            .ShouldBe(("123", "text/plain; charset=utf-8"));
        (await ReadBodyAsync(reader, world, numberCall.Id, numberAttempt.Id, WorkflowRunModelCallBody.AttemptResponse))
            .ShouldBe(("123", "application/json"));
    }

    [Fact]
    public async Task Structured_null_missing_malformed_and_malformed_reference_sources_settle_honestly()
    {
        var world = await SeedRunAsync();
        var structured = Guid.NewGuid();
        var nullValue = Guid.NewGuid();
        var missing = Guid.NewGuid();
        var nonObject = Guid.NewGuid();
        var malformedReference = Guid.NewGuid();
        var failed = Guid.NewGuid();
        await AddRecordsAsync(
            Record(world.RunId, WorkflowRunRecordTypes.InteractionCompleted, structured,
                """{"kind":"structured","output":{"answer":42,"parts":[true,"界"]}}"""),
            Record(world.RunId, WorkflowRunRecordTypes.InteractionCompleted, nullValue, """{"kind":"null","output":null}"""),
            Record(world.RunId, WorkflowRunRecordTypes.InteractionCompleted, missing, """{"kind":"missing"}"""),
            Record(world.RunId, WorkflowRunRecordTypes.InteractionCompleted, nonObject, "[]"),
            Record(world.RunId, WorkflowRunRecordTypes.InteractionCompleted, malformedReference,
                """{"kind":"bad-ref","output":{"$artifact_id":"not-a-guid"}}"""),
            Record(world.RunId, WorkflowRunRecordTypes.InteractionFailed, failed,
                """{"kind":"failed","error":{"code":"provider-timeout","retryable":true}}"""));
        await ProjectAsync();

        var result = await MaterializeAsync(world.RunId);

        result.Claimed.ShouldBe(6);
        result.Available.ShouldBe(2);
        result.NotRecorded.ShouldBe(2);
        result.Corrupt.ShouldBe(2);
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var calls = await db.WorkflowRunModelCall.AsNoTracking().Where(value => value.WorkflowRunId == world.RunId)
            .ToDictionaryAsync(value => value.SourceCorrelationId!.Value);
        var structuredAttempt = await db.WorkflowRunModelCallAttempt.AsNoTracking().SingleAsync(value => value.ModelCallId == calls[structured].Id);
        var structuredBody = await ReadBodyAsync(scope.Resolve<IWorkflowRunModelCallReader>(), world, calls[structured].Id,
            structuredAttempt.Id, WorkflowRunModelCallBody.AttemptResponse);
        structuredBody.ContentType.ShouldBe("application/json");
        JsonElement.DeepEquals(JsonDocument.Parse(structuredBody.Text).RootElement,
            JsonDocument.Parse("""{"answer":42,"parts":[true,"界"]}""").RootElement).ShouldBeTrue();
        var failedAttempt = await db.WorkflowRunModelCallAttempt.AsNoTracking().SingleAsync(value => value.ModelCallId == calls[failed].Id);
        var failedBody = await ReadBodyAsync(scope.Resolve<IWorkflowRunModelCallReader>(), world, calls[failed].Id,
            failedAttempt.Id, WorkflowRunModelCallBody.AttemptError);
        failedBody.ContentType.ShouldBe("application/json");
        JsonElement.DeepEquals(JsonDocument.Parse(failedBody.Text).RootElement,
            JsonDocument.Parse("""{"code":"provider-timeout","retryable":true}""").RootElement).ShouldBeTrue();
        var states = await (from capture in db.WorkflowRunModelCallBodyCapture.AsNoTracking()
                            join call in db.WorkflowRunModelCall.AsNoTracking() on capture.ModelCallId equals call.Id
                            where capture.WorkflowRunId == world.RunId
                            select new { Correlation = call.SourceCorrelationId!.Value, capture.State }).ToDictionaryAsync(value => value.Correlation, value => value.State);
        states[nullValue].ShouldBe(WorkflowRunModelCallBodyCaptureState.NotRecorded);
        states[missing].ShouldBe(WorkflowRunModelCallBodyCaptureState.NotRecorded);
        states[nonObject].ShouldBe(WorkflowRunModelCallBodyCaptureState.Corrupt);
        states[malformedReference].ShouldBe(WorkflowRunModelCallBodyCaptureState.Corrupt);
        var reader = scope.Resolve<IWorkflowRunModelCallReader>();
        var nullMetadata = await reader.ReadByIdAsync(world.RunId, calls[nullValue].Id, world.TeamId, CancellationToken.None);
        var nullDescriptor = nullMetadata!.Attempts.Single().Bodies.Single(value => value.Body == WorkflowRunModelCallBody.AttemptResponse);
        nullDescriptor.ReferenceState.ShouldBe(WorkflowRunModelCallBodyReferenceState.NotRecorded,
            "a terminal exact-source NotRecorded outcome must override the older call-wide Partial estimate");
        nullDescriptor.CaptureHealth.ShouldBe(WorkflowRunModelCallBodyCaptureHealth.Abandoned);
        nullDescriptor.MaterializationFormat.ShouldBeNull();
        var corruptMetadata = await reader.ReadByIdAsync(world.RunId, calls[malformedReference].Id, world.TeamId, CancellationToken.None);
        var corruptDescriptor = corruptMetadata!.Attempts.Single().Bodies.Single(value => value.Body == WorkflowRunModelCallBody.AttemptResponse);
        corruptDescriptor.ReferenceState.ShouldBe(WorkflowRunModelCallBodyReferenceState.Corrupt,
            "a terminal exact-source Corrupt outcome must override the older call-wide Corrupt/Partial estimate per body");
        corruptDescriptor.CaptureHealth.ShouldBe(WorkflowRunModelCallBodyCaptureHealth.Abandoned);
        corruptDescriptor.MaterializationFormat.ShouldBeNull();
    }

    [Fact]
    public async Task Store_failure_releases_the_lease_but_keeps_the_exact_source_pending_for_retry()
    {
        var world = await SeedRunAsync();
        var correlationId = Guid.NewGuid();
        var terminal = Record(world.RunId, WorkflowRunRecordTypes.InteractionCompleted, correlationId,
            """{"kind":"retryable-body","output":"source survives"}""");
        await AddRecordsAsync(terminal);
        await ProjectAsync();
        var writer = new ThrowingWriter();

        using (var pendingScope = _fixture.BeginScope())
        {
            var pendingDb = pendingScope.Resolve<CodeSpaceDbContext>();
            var pendingCall = await pendingDb.WorkflowRunModelCall.AsNoTracking().SingleAsync(value => value.SourceCorrelationId == correlationId);
            var pendingMetadata = await pendingScope.Resolve<IWorkflowRunModelCallReader>()
                .ReadByIdAsync(world.RunId, pendingCall.Id, world.TeamId, CancellationToken.None);
            pendingMetadata!.Attempts.Single().Bodies.Single(value => value.Body == WorkflowRunModelCallBody.AttemptResponse)
                .CaptureHealth.ShouldBe(WorkflowRunModelCallBodyCaptureHealth.Pending);
        }

        var result = await MaterializeAsync(world.RunId, writer);

        result.Claimed.ShouldBe(1);
        result.RetryScheduled.ShouldBe(1);
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var capture = await db.WorkflowRunModelCallBodyCapture.AsNoTracking().SingleAsync(value => value.WorkflowRunId == world.RunId);
        capture.State.ShouldBe(WorkflowRunModelCallBodyCaptureState.Pending);
        capture.MaterializationAttemptCount.ShouldBe(1);
        capture.LeaseOwnerId.ShouldBeNull();
        capture.LastErrorCode.ShouldBe("artifact-store-failed");
        capture.NextMaterializationAt.ShouldBeGreaterThan(capture.LastModifiedAt);
        (await db.WorkflowRunRecord.AsNoTracking().AnyAsync(value => value.Id == terminal.Id)).ShouldBeTrue("retry never consumes the immutable source");
        var call = await db.WorkflowRunModelCall.AsNoTracking().SingleAsync(value => value.SourceCorrelationId == correlationId);
        var metadata = await scope.Resolve<IWorkflowRunModelCallReader>().ReadByIdAsync(world.RunId, call.Id, world.TeamId, CancellationToken.None);
        var descriptor = metadata!.Attempts.Single().Bodies.Single(value => value.Body == WorkflowRunModelCallBody.AttemptResponse);
        descriptor.CaptureHealth.ShouldBe(WorkflowRunModelCallBodyCaptureHealth.Retry);
        descriptor.MaterializationFormat.ShouldBeNull();
    }

    [Fact]
    public async Task Bounded_store_failure_settles_capture_failed_without_inventing_a_body_reference()
    {
        var world = await SeedRunAsync();
        var correlationId = Guid.NewGuid();
        var terminal = Record(world.RunId, WorkflowRunRecordTypes.InteractionCompleted, correlationId,
            """{"kind":"exhausted-body","output":"still in the source"}""");
        await AddRecordsAsync(terminal);
        await ProjectAsync();
        using var scope = _fixture.BeginScope();
        var service = new WorkflowRunModelCallBodyMaterializer(scope.Resolve<DbContextOptions<CodeSpaceDbContext>>(), new ThrowingWriter(),
            NullLogger<WorkflowRunModelCallBodyMaterializer>.Instance, WorkflowRunModelCallBodyMaterializerOptions.Default with
            {
                RunFilter = world.RunId,
                MaxAttempts = 1,
            });

        var result = await service.SweepAsync(10, CancellationToken.None);

        result.CaptureFailed.ShouldBe(1);
        var db = scope.Resolve<CodeSpaceDbContext>();
        var capture = await db.WorkflowRunModelCallBodyCapture.AsNoTracking().SingleAsync(value => value.WorkflowRunId == world.RunId);
        capture.State.ShouldBe(WorkflowRunModelCallBodyCaptureState.CaptureFailed);
        capture.ArtifactId.ShouldBeNull();
        capture.TerminalAt.ShouldNotBeNull();
        capture.LastErrorCode.ShouldBe("materialization-exhausted");
        var call = await db.WorkflowRunModelCall.AsNoTracking().SingleAsync(value => value.SourceCorrelationId == correlationId);
        var attempt = await db.WorkflowRunModelCallAttempt.AsNoTracking().SingleAsync(value => value.ModelCallId == call.Id);
        attempt.ResponseArtifactId.ShouldBeNull();
        var page = await scope.Resolve<IWorkflowRunModelCallReader>().ReadBodyAsync(
            new WorkflowRunModelCallBodyReadRequest(world.RunId, call.Id, world.TeamId, WorkflowRunModelCallBody.AttemptResponse)
            {
                AttemptId = attempt.Id,
            }, CancellationToken.None);
        page.ShouldNotBeNull();
        page!.Availability.ShouldBe(WorkflowRunModelCallPartAvailability.CaptureUnavailable);
        var metadata = await scope.Resolve<IWorkflowRunModelCallReader>().ReadByIdAsync(world.RunId, call.Id, world.TeamId, CancellationToken.None);
        var descriptor = metadata!.Attempts.Single().Bodies.Single(value => value.Body == WorkflowRunModelCallBody.AttemptResponse);
        descriptor.ReferenceState.ShouldBe(WorkflowRunModelCallBodyReferenceState.Unavailable);
        descriptor.CaptureHealth.ShouldBe(WorkflowRunModelCallBodyCaptureHealth.Failed);
        descriptor.MaterializationFormat.ShouldBeNull();
    }

    [Fact]
    public async Task Caller_abort_leaves_a_fenced_lease_and_another_worker_reclaims_the_exact_source_after_expiry()
    {
        var world = await SeedRunAsync();
        var correlationId = Guid.NewGuid();
        var terminal = Record(world.RunId, WorkflowRunRecordTypes.InteractionCompleted, correlationId,
            """{"kind":"aborted-body","output":"recover me"}""");
        await AddRecordsAsync(terminal);
        await ProjectAsync();
        var writer = new BlockingWriter();
        using var scope = _fixture.BeginScope();
        var interrupted = new WorkflowRunModelCallBodyMaterializer(scope.Resolve<DbContextOptions<CodeSpaceDbContext>>(), writer,
            NullLogger<WorkflowRunModelCallBodyMaterializer>.Instance, WorkflowRunModelCallBodyMaterializerOptions.Default with
            {
                RunFilter = world.RunId,
                OperationTimeout = TimeSpan.FromMilliseconds(100),
                LeaseDuration = TimeSpan.FromMilliseconds(1200),
            });
        using var cancellation = new CancellationTokenSource();
        var sweep = interrupted.SweepAsync(10, cancellation.Token);
        await writer.Started;
        cancellation.Cancel();
        await sweep.ShouldThrowAsync<OperationCanceledException>();

        var db = scope.Resolve<CodeSpaceDbContext>();
        var leased = await db.WorkflowRunModelCallBodyCapture.AsNoTracking().SingleAsync(value => value.WorkflowRunId == world.RunId);
        leased.State.ShouldBe(WorkflowRunModelCallBodyCaptureState.Pending);
        leased.LeaseOwnerId.ShouldNotBeNull();
        leased.MaterializationAttemptCount.ShouldBe(1);
        (await db.WorkflowRunRecord.AsNoTracking().AnyAsync(value => value.Id == terminal.Id)).ShouldBeTrue();
        var call = await db.WorkflowRunModelCall.AsNoTracking().SingleAsync(value => value.SourceCorrelationId == correlationId);
        var materializing = await scope.Resolve<IWorkflowRunModelCallReader>()
            .ReadByIdAsync(world.RunId, call.Id, world.TeamId, CancellationToken.None);
        var materializingDescriptor = materializing!.Attempts.Single().Bodies.Single(value => value.Body == WorkflowRunModelCallBody.AttemptResponse);
        materializingDescriptor.CaptureHealth.ShouldBe(WorkflowRunModelCallBodyCaptureHealth.Materializing);
        materializingDescriptor.MaterializationFormat.ShouldBeNull();

        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE workflow_run_model_call_body_capture SET lease_expires_at = clock_timestamp() - interval '1 millisecond',
                lease_owner_id = {{leased.LeaseOwnerId}}, lease_fence = {{leased.LeaseFence + 1}},
                materialization_attempt_count = {{leased.MaterializationAttemptCount + 1}}, revision = {{leased.Revision + 1}},
                last_modified_at = clock_timestamp() WHERE id = {{leased.Id}}
            """).ShouldThrowAsync<Exception>("a live lease cannot be shortened or forged, even by a test/admin raw write");
        await Task.Delay(TimeSpan.FromMilliseconds(1300));

        var recovered = await MaterializeAsync(world.RunId);
        recovered.Available.ShouldBe(1);
        var settled = await db.WorkflowRunModelCallBodyCapture.AsNoTracking().SingleAsync(value => value.Id == leased.Id);
        settled.State.ShouldBe(WorkflowRunModelCallBodyCaptureState.Available);
        settled.MaterializationAttemptCount.ShouldBe(2);
        var available = await scope.Resolve<IWorkflowRunModelCallReader>()
            .ReadByIdAsync(world.RunId, call.Id, world.TeamId, CancellationToken.None);
        available!.Attempts.Single().Bodies.Single(value => value.Body == WorkflowRunModelCallBody.AttemptResponse)
            .CaptureHealth.ShouldBe(WorkflowRunModelCallBodyCaptureHealth.Available);
    }

    [Fact]
    public async Task Database_refuses_live_claim_theft_and_cross_team_available_settlement()
    {
        var world = await SeedRunAsync();
        var foreign = await SeedRunAsync();
        var correlationId = Guid.NewGuid();
        await AddRecordsAsync(Record(world.RunId, WorkflowRunRecordTypes.InteractionCompleted, correlationId,
            """{"kind":"guarded-body","output":"body"}"""));
        await ProjectAsync();
        Guid foreignArtifactId;
        using (var foreignScope = _fixture.BeginScope())
            foreignArtifactId = await foreignScope.Resolve<IArtifactStore>().PutAsync(foreign.TeamId, "foreign"u8.ToArray(), "text/plain", CancellationToken.None);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var capture = await db.WorkflowRunModelCallBodyCapture.SingleAsync(value => value.WorkflowRunId == world.RunId);
        var attemptId = capture.ModelCallAttemptId;
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE workflow_run_model_call_attempt SET status = 'Failed', last_modified_date = clock_timestamp()
             WHERE id = {{attemptId}}
            """).ShouldThrowAsync<Exception>("opening the body-ref seam cannot make unrelated projected attempt facts raw-SQL mutable");
        var firstOwner = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE workflow_run_model_call_body_capture SET lease_owner_id = {{firstOwner}}, lease_fence = 1,
                lease_expires_at = clock_timestamp() + interval '1 second', materialization_attempt_count = 1,
                revision = 2, last_modified_at = clock_timestamp() WHERE id = {{capture.Id}}
            """);
        var thief = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE workflow_run_model_call_body_capture SET lease_owner_id = {{thief}}, lease_fence = 2,
                lease_expires_at = clock_timestamp() + interval '5 minutes', materialization_attempt_count = 2,
                revision = 3, last_modified_at = clock_timestamp() WHERE id = {{capture.Id}}
            """).ShouldThrowAsync<Exception>("a live exact owner/fence cannot be replaced");
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE workflow_run_model_call_body_capture SET state = 'NotRecorded', terminal_at = clock_timestamp(),
                lease_owner_id = NULL, lease_expires_at = NULL, revision = 3, last_modified_at = clock_timestamp()
             WHERE id = {{capture.Id}}
            """).ShouldThrowAsync<Exception>("raw SQL cannot settle without presenting the exact lease capability");

        await Task.Delay(1100);
        var secondOwner = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE workflow_run_model_call_body_capture SET lease_owner_id = {{secondOwner}}, lease_fence = 2,
                lease_expires_at = clock_timestamp() + interval '5 minutes', materialization_attempt_count = 2,
                revision = 3, last_modified_at = clock_timestamp() WHERE id = {{capture.Id}}
            """);

        await using (var staleTransaction = await db.Database.BeginTransactionAsync())
        {
            await AuthorizeSettlementAsync(db, firstOwner, 1);
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                UPDATE workflow_run_model_call_body_capture SET state = 'NotRecorded', terminal_at = clock_timestamp(),
                    lease_owner_id = NULL, lease_expires_at = NULL, revision = 4, last_modified_at = clock_timestamp()
                 WHERE id = {{capture.Id}}
                """).ShouldThrowAsync<Exception>("an expired worker cannot settle after the row has been reclaimed at a newer fence");
        }

        var foreignMetadata = await scope.Resolve<IArtifactStore>().GetMetadataAsync(foreign.TeamId, foreignArtifactId, CancellationToken.None);
        await using (var crossTeamTransaction = await db.Database.BeginTransactionAsync())
        {
            await AuthorizeSettlementAsync(db, secondOwner, 2);
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                UPDATE workflow_run_model_call_attempt SET response_artifact_id = {{foreignArtifactId}}, last_modified_date = clock_timestamp()
                 WHERE id = {{attemptId}}
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                UPDATE workflow_run_model_call_body_capture SET state = 'Available', artifact_id = {{foreignArtifactId}},
                    source_sha256 = {{foreignMetadata!.Sha256}}, size_bytes = {{foreignMetadata.SizeBytes}}, content_type = {{foreignMetadata.ContentType}},
                    materialization_format = 'external-artifact/v1', terminal_at = clock_timestamp(),
                    lease_owner_id = NULL, lease_expires_at = NULL, revision = 4, last_modified_at = clock_timestamp()
                 WHERE id = {{capture.Id}}
                """).ShouldThrowAsync<Exception>("Available settlement must prove exact same-team artifact metadata even with an exact target ref");
        }

        var malformedArtifactId = await scope.Resolve<IArtifactStore>().PutAsync(world.TeamId, "BADHDR!!payload"u8.ToArray(),
            WorkflowRunModelCallBodyMaterializationFormats.EnvelopeContentType, CancellationToken.None);
        var malformedMetadata = await scope.Resolve<IArtifactStore>().GetMetadataAsync(world.TeamId, malformedArtifactId, CancellationToken.None);
        var malformedSettledAt = DateTimeOffset.UtcNow;
        await using (var validSettlement = await db.Database.BeginTransactionAsync())
        {
            await AuthorizeSettlementAsync(db, secondOwner, 2);
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                UPDATE workflow_run_model_call_attempt SET response_artifact_id = {{malformedArtifactId}}, last_modified_date = clock_timestamp()
                 WHERE id = {{attemptId}}
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                UPDATE workflow_run_model_call_body_capture SET state = 'Available', artifact_id = {{malformedArtifactId}},
                    source_sha256 = {{malformedMetadata!.Sha256}}, size_bytes = {{malformedMetadata.SizeBytes}}, content_type = {{malformedMetadata.ContentType}},
                    materialization_format = 'utf8-string-envelope/v1', terminal_at = {{malformedSettledAt}},
                    lease_owner_id = NULL, lease_expires_at = NULL, revision = 4, last_modified_at = {{malformedSettledAt}}
                 WHERE id = {{capture.Id}}
                """);
            await validSettlement.CommitAsync();
        }
        var malformedRead = await scope.Resolve<IWorkflowRunModelCallReader>().ReadBodyAsync(
            new WorkflowRunModelCallBodyReadRequest(world.RunId, capture.ModelCallId, world.TeamId, WorkflowRunModelCallBody.AttemptResponse)
            {
                AttemptId = attemptId,
            }, CancellationToken.None);
        malformedRead.ShouldNotBeNull();
        malformedRead!.Availability.ShouldBe(WorkflowRunModelCallPartAvailability.IntegrityFailure,
            "a legal metadata transition cannot make bytes with the wrong typed-envelope header readable");
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE workflow_run_model_call_body_capture SET materialization_format = 'json-envelope/v1',
                revision = 5, last_modified_at = clock_timestamp() WHERE id = {{capture.Id}}
            """).ShouldThrowAsync<Exception>("Available format is part of the immutable terminal decoding identity");
    }

    private async Task<WorkflowRunModelCallBodyMaterializationSummary> MaterializeAsync(Guid runId, IWorkflowRunModelCallBodyArtifactWriter? writer = null)
    {
        using var scope = _fixture.BeginScope();
        var service = new WorkflowRunModelCallBodyMaterializer(scope.Resolve<DbContextOptions<CodeSpaceDbContext>>(),
            writer ?? scope.Resolve<IWorkflowRunModelCallBodyArtifactWriter>(), NullLogger<WorkflowRunModelCallBodyMaterializer>.Instance,
            WorkflowRunModelCallBodyMaterializerOptions.Default with { RunFilter = runId });
        return await service.SweepAsync(50, CancellationToken.None);
    }

    private async Task ProjectAsync()
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowRunModelCallProjector>().SweepAsync(50, CancellationToken.None);
    }

    private static async Task<(string Text, string ContentType)> ReadBodyAsync(IWorkflowRunModelCallReader reader, RunWorld world,
        Guid callId, Guid? attemptId, WorkflowRunModelCallBody body)
    {
        var text = new StringBuilder();
        long offset = 0;
        string? contentType = null;
        while (true)
        {
            var page = await reader.ReadBodyAsync(new WorkflowRunModelCallBodyReadRequest(world.RunId, callId, world.TeamId, body)
            {
                AttemptId = attemptId,
                OffsetBytes = offset,
                LimitBytes = 4096,
            }, CancellationToken.None);
            page.ShouldNotBeNull();
            page!.Availability.ShouldBe(WorkflowRunModelCallPartAvailability.Available);
            page.ReturnedBytes.ShouldBeLessThanOrEqualTo(4096);
            contentType ??= page.ContentType;
            page.ContentType.ShouldBe(contentType);
            text.Append(page.Text);
            if (page.NextOffsetBytes is not { } next) break;
            next.ShouldBeGreaterThan(offset);
            offset = next;
        }
        return (text.ToString(), contentType!);
    }

    private async Task AddRecordsAsync(params WorkflowRunRecord[] records)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.WorkflowRunRecord.AddRange(records);
        await db.SaveChangesAsync();
    }

    private static async Task AuthorizeSettlementAsync(CodeSpaceDbContext db, Guid ownerId, long fence) =>
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            SELECT set_config('codespace.workflow_run_model_call_body_lease_owner', {{ownerId.ToString()}}, true),
                   set_config('codespace.workflow_run_model_call_body_lease_fence', {{fence.ToString()}}, true)
            """);

    private async Task<RunWorld> SeedRunAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        Guid workflowId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            workflowId = await scope.Resolve<MediatR.IMediator>().Send(new CreateWorkflowCommand
            {
                Name = "body-materializer-" + Guid.NewGuid().ToString("N")[..8], Definition = WorkflowsTestSeed.MinimalDefinition(),
                Activations = new List<WorkflowActivationInput>(), Enabled = true,
            });
        return new RunWorld(await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId), teamId);
    }

    private static WorkflowRunRecord Record(Guid runId, string recordType, Guid correlationId, string payloadJson, DateTimeOffset? occurredAt = null) => new()
    {
        Id = Guid.NewGuid(), RunId = runId, RecordType = recordType, NodeId = "sup", IterationKey = "sup#turn1",
        CorrelationId = correlationId, OccurredAt = occurredAt ?? DateTimeOffset.UtcNow, PayloadJson = payloadJson,
    };

    private sealed record RunWorld(Guid RunId, Guid TeamId);

    private sealed class ThrowingWriter : IWorkflowRunModelCallBodyArtifactWriter
    {
        public Task<ArtifactMetadata?> ReadMetadataAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken) => Task.FromResult<ArtifactMetadata?>(null);
        public Task<ArtifactMetadata> PutAsync(Guid teamId, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("synthetic object-store outage");
    }

    private sealed class BlockingWriter : IWorkflowRunModelCallBodyArtifactWriter
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;
        public Task<ArtifactMetadata?> ReadMetadataAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken) => Task.FromResult<ArtifactMetadata?>(null);

        public async Task<ArtifactMetadata> PutAsync(Guid teamId, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable blocking writer continuation");
        }
    }
}
