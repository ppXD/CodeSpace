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
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows.ModelCalls;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Pins the Workflow Run-owned, metadata-first model-call read path over real Postgres and artifact storage. A large
/// prompt/result is read one bounded page at a time; an unavailable prompt can never prevent an independent result,
/// usage, or trace part from loading.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class WorkflowRunModelCallReadFlowTests
{
    private readonly PostgresFixture _fixture;

    public WorkflowRunModelCallReadFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Metadata_does_not_read_blob_bytes_and_one_missing_part_does_not_poison_the_others()
    {
        var seeded = await SeedCallAsync(includeMissingSystemPrompt: true, result: "RESULT-OK");

        using var scope = _fixture.BeginScope();
        var reader = scope.Resolve<IWorkflowRunModelCallReader>();

        var metadata = await reader.ReadMetadataAsync(seeded.RunId, seeded.Sequence, seeded.TeamId, CancellationToken.None);

        metadata.ShouldNotBeNull("metadata is projected from the append-only ledger and artifact references, without opening blob bytes");
        metadata!.RunId.ShouldBe(seeded.RunId);
        metadata.Sequence.ShouldBe(seeded.Sequence);
        metadata.Status.ShouldBe(WorkflowRunModelCallStatus.Completed);
        metadata.WorkflowRunModelCallId.ShouldBeNull();
        metadata.ProjectionState.ShouldBe(WorkflowRunModelCallProjectionState.LegacyFallback);
        metadata.CaptureCompleteness.ShouldBe(WorkflowRunCaptureCompleteness.LegacyUnknown);
        metadata.Parts.Single(p => p.Part == WorkflowRunModelCallPart.SystemPrompt).Source.ShouldBe(WorkflowRunModelCallPartSource.Artifact);

        var result = await reader.ReadPartAsync(new WorkflowRunModelCallPartReadRequest(seeded.RunId, seeded.Sequence, seeded.TeamId, WorkflowRunModelCallPart.Result), CancellationToken.None);
        result.ShouldNotBeNull();
        result!.Availability.ShouldBe(WorkflowRunModelCallPartAvailability.Available);
        result.Text.ShouldBe("RESULT-OK", "reading Result never dereferences the missing SystemPrompt artifact");

        var system = await reader.ReadPartAsync(new WorkflowRunModelCallPartReadRequest(seeded.RunId, seeded.Sequence, seeded.TeamId, WorkflowRunModelCallPart.SystemPrompt), CancellationToken.None);
        system.ShouldNotBeNull();
        system!.Availability.ShouldBe(WorkflowRunModelCallPartAvailability.PhysicalObjectMissing);
        system.Text.ShouldBeNull();

        var usage = await reader.ReadPartAsync(new WorkflowRunModelCallPartReadRequest(seeded.RunId, seeded.Sequence, seeded.TeamId, WorkflowRunModelCallPart.Usage), CancellationToken.None);
        usage!.Availability.ShouldBe(WorkflowRunModelCallPartAvailability.Available);
        usage.Text.ShouldContain("inputTokens");
    }

    [Fact]
    public async Task A_large_CJK_result_round_trips_exactly_through_bounded_UTF8_pages()
    {
        var expected = string.Concat(Enumerable.Repeat("部署分析🙂\n", 18_000));
        var seeded = await SeedCallAsync(includeMissingSystemPrompt: false, result: expected);

        using var scope = _fixture.BeginScope();
        var reader = scope.Resolve<IWorkflowRunModelCallReader>();
        var text = new StringBuilder();
        long offset = 0;
        var pageCount = 0;

        while (true)
        {
            var page = await reader.ReadPartAsync(new WorkflowRunModelCallPartReadRequest(seeded.RunId, seeded.Sequence, seeded.TeamId, WorkflowRunModelCallPart.Result)
            {
                OffsetBytes = offset,
                LimitBytes = 4096,
            }, CancellationToken.None);

            page.ShouldNotBeNull();
            page!.Availability.ShouldBe(WorkflowRunModelCallPartAvailability.Available);
            page.ReturnedBytes.ShouldBeLessThanOrEqualTo(4096);
            text.Append(page.Text);
            pageCount++;

            if (page.NextOffsetBytes is not { } next) break;
            next.ShouldBeGreaterThan(offset);
            offset = next;
        }

        pageCount.ShouldBeGreaterThan(20, "the endpoint pages rather than constructing one unbounded response");
        text.ToString().ShouldBe(expected, "page boundaries never split or replace a UTF-8 rune");
    }

    [Fact]
    public async Task Foreign_team_and_non_model_call_sequences_are_indistinguishable_not_found()
    {
        var seeded = await SeedCallAsync(includeMissingSystemPrompt: false, result: "ok");
        var (foreignTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScope();
        var reader = scope.Resolve<IWorkflowRunModelCallReader>();

        (await reader.ReadMetadataAsync(seeded.RunId, seeded.Sequence, foreignTeamId, CancellationToken.None)).ShouldBeNull();
        (await reader.ReadPartAsync(new WorkflowRunModelCallPartReadRequest(seeded.RunId, seeded.Sequence + 1, seeded.TeamId, WorkflowRunModelCallPart.Result), CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Failed_interaction_exposes_its_error_as_an_independent_bounded_part()
    {
        var seeded = await SeedFailedCallAsync("provider exploded inline");
        using var scope = _fixture.BeginScope();
        var reader = scope.Resolve<IWorkflowRunModelCallReader>();

        var error = await reader.ReadPartAsync(new WorkflowRunModelCallPartReadRequest(seeded.RunId, seeded.Sequence, seeded.TeamId, WorkflowRunModelCallPart.Error)
        {
            LimitBytes = 4096,
        }, CancellationToken.None);

        error.ShouldNotBeNull();
        error!.Availability.ShouldBe(WorkflowRunModelCallPartAvailability.Available);
        error.Text.ShouldBe("provider exploded inline");
        error.ReturnedBytes.ShouldBeLessThanOrEqualTo(4096);
        error.NextOffsetBytes.ShouldBeNull();
    }

    [Fact]
    public async Task Projected_part_read_never_substitutes_a_different_started_record_with_the_same_correlation()
    {
        var seeded = await SeedCallAsync(includeMissingSystemPrompt: false, result: "RESULT", earlierForeignPrompt: "WRONG SOURCE");
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowRunModelCallProjector>().SweepAsync(50, CancellationToken.None);

        var part = await scope.Resolve<IWorkflowRunModelCallReader>().ReadPartAsync(
            new WorkflowRunModelCallPartReadRequest(seeded.RunId, seeded.Sequence, seeded.TeamId, WorkflowRunModelCallPart.UserPrompt), CancellationToken.None);

        part.ShouldNotBeNull();
        part!.Availability.ShouldBe(WorkflowRunModelCallPartAvailability.Available);
        part.Text.ShouldBe("USER", "the projected attempt's exact started row wins over an earlier row that reused the correlation in another source scope");
    }

    [Fact]
    public async Task Run_level_page_indexes_projected_calls_from_the_stable_cross_producer_plane()
    {
        var seeded = await SeedCallAsync(includeMissingSystemPrompt: false, result: "RESULT");
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowRunModelCallProjector>().SweepAsync(50, CancellationToken.None);

        var page = await scope.Resolve<IWorkflowRunModelCallReader>().ReadPageAsync(seeded.RunId, seeded.TeamId, cursor: null, limit: 20, CancellationToken.None);

        page.ShouldNotBeNull();
        page!.Items.Count.ShouldBe(1);
        page.Items[0].Purpose.ShouldBe("supervisor.decision/v1");
        page.Items[0].CaptureSource.ShouldBe("workflow-run-record/v1");
        page.Items[0].WorkflowRunModelCallId.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Run_level_page_uses_a_created_time_and_id_keyset_without_duplicates_across_equal_timestamps()
    {
        var seeded = await SeedCallAsync(includeMissingSystemPrompt: false, result: "RESULT");
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowRunModelCallProjector>().SweepAsync(50, CancellationToken.None);
        var db = scope.Resolve<CodeSpaceDbContext>();
        var tiedAt = DateTimeOffset.UtcNow.AddDays(1);
        var tiedIds = new[]
        {
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            Guid.Parse("10000000-0000-0000-0000-000000000002"),
            Guid.Parse("10000000-0000-0000-0000-000000000003"),
        };
        db.WorkflowRunModelCall.AddRange(tiedIds.Select((id, index) => new WorkflowRunModelCall
        {
            Id = id, TeamId = seeded.TeamId, WorkflowRunId = seeded.RunId, CallOrdinal = index + 10,
            Purpose = "keyset-test/v1", CaptureSource = "test/v1", CaptureCompleteness = WorkflowRunCaptureCompleteness.Partial,
            CreatedDate = tiedAt, LastModifiedDate = tiedAt,
        }));
        await db.SaveChangesAsync();

        var reader = scope.Resolve<IWorkflowRunModelCallReader>();
        var first = (await reader.ReadPageAsync(seeded.RunId, seeded.TeamId, cursor: null, limit: 2, CancellationToken.None))!;
        first.Items.Count.ShouldBe(2);
        first.NextCursor.ShouldNotBeNull();
        var second = (await reader.ReadPageAsync(seeded.RunId, seeded.TeamId, first.NextCursor, limit: 2, CancellationToken.None))!;

        var ids = first.Items.Concat(second.Items).Select(value => value.WorkflowRunModelCallId).ToList();
        ids.Count.ShouldBe(4);
        ids.Distinct().Count().ShouldBe(4, "the composite cursor neither repeats nor skips rows sharing one timestamptz");
        ids.ShouldContain(tiedIds[0]);
        ids.ShouldContain(tiedIds[1]);
        ids.ShouldContain(tiedIds[2]);
        second.NextCursor.ShouldBeNull();
    }

    private async Task<(Guid RunId, Guid TeamId, long Sequence)> SeedCallAsync(bool includeMissingSystemPrompt, string result, string? earlierForeignPrompt = null)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        var correlationId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var artifacts = scope.Resolve<IArtifactStore>();

        object system = "SYSTEM";
        if (includeMissingSystemPrompt)
        {
            var missingArtifactId = await SeedMissingArtifactAsync(db, teamId);
            system = ArtifactRef(missingArtifactId, 12_000, "text/plain");
        }

        object output = result;
        if (Encoding.UTF8.GetByteCount(result) > ArtifactStoreConfig.InlineThresholdBytes)
        {
            var artifactId = await artifacts.PutAsync(teamId, Encoding.UTF8.GetBytes(result), "text/plain", CancellationToken.None);
            output = ArtifactRef(artifactId, Encoding.UTF8.GetByteCount(result), "text/plain");
        }

        var started = new WorkflowRunRecord
        {
            Id = Guid.NewGuid(), RunId = runId, RecordType = WorkflowRunRecordTypes.InteractionStarted,
            NodeId = "sup", IterationKey = "sup#turn1", CorrelationId = correlationId,
            PayloadJson = JsonSerializer.Serialize(new { kind = "supervisor.decision", provider = "test", model = "test-model", prompt = new { system, user = "USER" } }),
        };
        var completed = new WorkflowRunRecord
        {
            Id = Guid.NewGuid(), RunId = runId, RecordType = WorkflowRunRecordTypes.InteractionCompleted,
            NodeId = "sup", IterationKey = "sup#turn1", CorrelationId = correlationId,
            PayloadJson = JsonSerializer.Serialize(new { kind = "supervisor.decision", provider = "test", model = "test-model", usage = new { inputTokens = 12, outputTokens = 8, finishReason = "stop" }, output }),
        };

        if (earlierForeignPrompt is not null)
            db.WorkflowRunRecord.Add(new WorkflowRunRecord
            {
                Id = Guid.NewGuid(), RunId = runId, RecordType = WorkflowRunRecordTypes.InteractionStarted,
                NodeId = "foreign-node", IterationKey = "foreign#turn1", CorrelationId = correlationId,
                PayloadJson = JsonSerializer.Serialize(new { kind = "supervisor.decision", provider = "test", model = "test-model", prompt = new { user = earlierForeignPrompt } }),
            });
        db.WorkflowRunRecord.AddRange(started, completed);
        await db.SaveChangesAsync();
        return (runId, teamId, completed.Sequence);
    }

    private async Task<(Guid RunId, Guid TeamId, long Sequence)> SeedFailedCallAsync(string error)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        var correlationId = Guid.NewGuid();
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.WorkflowRunRecord.AddRange(
            new WorkflowRunRecord
            {
                Id = Guid.NewGuid(), RunId = runId, RecordType = WorkflowRunRecordTypes.InteractionStarted,
                NodeId = "sup", IterationKey = "sup#turn1", CorrelationId = correlationId,
                PayloadJson = JsonSerializer.Serialize(new { kind = "supervisor.decision", provider = "test", model = "test-model", prompt = "USER" }),
            },
            new WorkflowRunRecord
            {
                Id = Guid.NewGuid(), RunId = runId, RecordType = WorkflowRunRecordTypes.InteractionFailed,
                NodeId = "sup", IterationKey = "sup#turn1", CorrelationId = correlationId,
                PayloadJson = JsonSerializer.Serialize(new { kind = "supervisor.decision", provider = "test", error, category = "Transport", failureKind = "provider" }),
            });
        await db.SaveChangesAsync();
        var terminal = await db.WorkflowRunRecord.AsNoTracking().SingleAsync(value => value.RunId == runId && value.RecordType == WorkflowRunRecordTypes.InteractionFailed);
        return (runId, teamId, terminal.Sequence);
    }

    private static async Task<Guid> SeedMissingArtifactAsync(CodeSpaceDbContext db, Guid teamId)
    {
        var sha = new string('a', 63) + Random.Shared.Next(10).ToString();
        var id = Guid.NewGuid();
        db.WorkflowArtifact.Add(new WorkflowArtifact
        {
            Id = id,
            TeamId = teamId,
            Sha256 = sha,
            ContentType = "text/plain",
            SizeBytes = 12_000,
            StorageUrl = new Uri(Path.Combine(CodeSpace.Core.Settings.DurableRoots.ArtifactStore(CodeSpace.Core.Settings.RuntimeSettings.Current.ArtifactStoreDirectory), sha[..2], sha.Substring(2, 2), sha)).AbsoluteUri,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static Dictionary<string, object> ArtifactRef(Guid artifactId, int sizeBytes, string contentType) => new()
    {
        ["$artifact_id"] = artifactId.ToString(),
        ["size_bytes"] = sizeBytes,
        ["content_type"] = contentType,
    };

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<MediatR.IMediator>();
        return await mediator.Send(new CreateWorkflowCommand
        {
            Name = "model-call-read-" + Guid.NewGuid().ToString("N")[..8],
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }
}
