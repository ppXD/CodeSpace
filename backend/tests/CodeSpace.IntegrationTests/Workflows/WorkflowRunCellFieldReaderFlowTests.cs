using System.Data.Common;
using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Display;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class WorkflowRunCellFieldReaderFlowTests
{
    private const string Bomb = "cell-field-body-must-not-cross-descriptor-seam";
    private readonly PostgresFixture _fixture;

    public WorkflowRunCellFieldReaderFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Exact_selected_cell_descriptors_preserve_inline_and_artifact_facts_without_bytes_ids_or_n_plus_one()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (foreignTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var artifact = await PutArtifactAsync(teamId, JsonSerializer.SerializeToUtf8Bytes(new { value = "stored" }));
        var wrongTypeArtifact = await PutArtifactAsync(teamId, JsonSerializer.SerializeToUtf8Bytes(new { value = "typed-wrong" }), "text/plain");
        var foreignArtifact = await PutArtifactAsync(foreignTeamId, JsonSerializer.SerializeToUtf8Bytes(new { value = "foreign" }));
        var missingId = Guid.NewGuid();
        var original = await SeedRunAsync(teamId, null, null, DateTimeOffset.UtcNow.AddMinutes(-2));
        var latest = await SeedRunAsync(teamId, original, original, DateTimeOffset.UtcNow);
        await SeedCellAsync(original, Inputs(artifact.Id), Outputs(artifact, wrongTypeArtifact, foreignArtifact, missingId), "old-error");
        var records = await SeedCellAsync(latest, Inputs(artifact.Id), Outputs(artifact, wrongTypeArtifact, foreignArtifact, missingId), "latest-error");

        var commands = new ReadCommandRecorder();
        using var scope = ReadScope(commands);
        var page = await scope.Resolve<IWorkflowRunCellFieldReader>().ReadAsync(Request(teamId, latest, latest, limit: 100), CancellationToken.None);

        page.ShouldNotBeNull();
        page!.SourceRunId.ShouldBe(latest);
        page.StateRecordId.ShouldBe(records.StateId);
        page.StateRecordSequence.ShouldBe(records.StateSequence);
        page.FirstStartedRecordId.ShouldBe(records.StartedId);
        page.FirstStartedRecordSequence.ShouldBe(records.StartedSequence);
        page.Status.ShouldBe(NodeStatus.Success);
        page.FieldsAvailability.ShouldBe(WorkflowRunCellFieldAvailability.Available);
        page.InputsAvailability.ShouldBe(WorkflowRunCellFieldAvailability.Available);
        page.OutputsAvailability.ShouldBe(WorkflowRunCellFieldAvailability.Available);
        page.ErrorAvailability.ShouldBe(WorkflowRunCellFieldAvailability.Available);
        page.RequestCursor.ShouldBeNull();
        page.NextCursor.ShouldBeNull();

        var pointerInput = page.Fields.Single(value => value.Section == WorkflowRunCellFieldSection.Input && value.Name == "pointerLike");
        pointerInput.Materialization.ShouldBe(WorkflowRunCellFieldMaterialization.Inline,
            "only the output producer owns $artifact_ref semantics");
        pointerInput.TotalBytes.ShouldBeNull("inline size is deferred rather than stringifying every value in metadata");

        var stored = page.Fields.Single(value => value.Section == WorkflowRunCellFieldSection.Output && value.Name == "stored");
        stored.Materialization.ShouldBe(WorkflowRunCellFieldMaterialization.Artifact);
        stored.Availability.ShouldBe(WorkflowRunCellFieldAvailability.Available);
        stored.TotalBytes.ShouldBe(artifact.Size);
        stored.Sha256.ShouldBe(artifact.Sha256);

        page.Fields.Single(value => value.Name == "missing").Availability.ShouldBe(WorkflowRunCellFieldAvailability.Unavailable);
        page.Fields.Single(value => value.Name == "missing").ProblemCode.ShouldBe(WorkflowRunCellFieldProblemCode.ArtifactMetadataMissing);
        page.Fields.Single(value => value.Name == "foreign").ProblemCode.ShouldBe(WorkflowRunCellFieldProblemCode.ArtifactMetadataMissing,
            "an artifact owned by another team is indistinguishable from missing metadata");
        page.Fields.Single(value => value.Name == "malformed").Availability.ShouldBe(WorkflowRunCellFieldAvailability.CorruptReference);
        page.Fields.Single(value => value.Name == "mismatch").ProblemCode.ShouldBe(WorkflowRunCellFieldProblemCode.DeclaredSizeMismatch);
        page.Fields.Single(value => value.Name == "wrongDeclaredType").ProblemCode.ShouldBe(WorkflowRunCellFieldProblemCode.DeclaredContentTypeMismatch);
        page.Fields.Single(value => value.Name == "wrongStoredType").ProblemCode.ShouldBe(WorkflowRunCellFieldProblemCode.StoredContentTypeMismatch);
        page.Fields.Single(value => value.Section == WorkflowRunCellFieldSection.Error).Name.ShouldBeNull();
        page.Fields.ShouldNotContain(value => value.Name == "configSecret");

        var wire = JsonSerializer.Serialize(page, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        wire.ShouldNotContain(Bomb, Case.Sensitive);
        wire.ShouldNotContain(artifact.Id.ToString(), Case.Insensitive);
        commands.Commands.Count(value => value.Contains("workflow_artifact", StringComparison.OrdinalIgnoreCase)).ShouldBe(1,
            "all referenced artifact metadata is admitted by one exact-team batch query");
    }

    [Fact]
    public async Task Field_pages_are_C_collated_keysets_bounded_to_one_hundred_and_keep_empty_names()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, null, null, DateTimeOffset.UtcNow);
        var outputs = Enumerable.Range(0, 104).ToDictionary(index => $"f{index:000}", index => (object?)index, StringComparer.Ordinal);
        outputs[string.Empty] = "empty-name-is-valid";
        var maximumUtf8Name = string.Concat(Enumerable.Repeat("💾", 256));
        outputs[maximumUtf8Name] = "exactly-1024-utf8-bytes-is-valid";
        await SeedCellAsync(runId, new Dictionary<string, object?>(), outputs, error: null);

        using var scope = _fixture.BeginScope();
        var reader = scope.Resolve<IWorkflowRunCellFieldReader>();
        var first = await reader.ReadAsync(Request(teamId, runId, runId, limit: 100), CancellationToken.None);
        var second = await reader.ReadAsync(Request(teamId, runId, runId, limit: 100, cursor: first!.NextCursor), CancellationToken.None);

        first!.FieldsAvailability.ShouldBe(WorkflowRunCellFieldAvailability.Truncated);
        first.Fields.Count.ShouldBe(100);
        first.Fields[0].Name.ShouldBe(string.Empty);
        first.NextCursor.ShouldNotBeNull();
        second!.RequestCursor.ShouldBe(first.NextCursor);
        second.FieldsAvailability.ShouldBe(WorkflowRunCellFieldAvailability.Available);
        second.Fields.Count.ShouldBe(6);
        second.NextCursor.ShouldBeNull();
        first.Fields.Concat(second.Fields).Select(value => value.Name).ShouldBe(
            new[] { string.Empty }.Concat(Enumerable.Range(0, 104).Select(index => $"f{index:000}")).Append(maximumUtf8Name));
    }

    [Fact]
    public async Task Oversize_names_and_missing_sections_are_typed_and_never_masquerade_as_empty_success()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, null, null, DateTimeOffset.UtcNow);
        await SeedTerminalOnlyCellAsync(runId, new Dictionary<string, object?> { [new string('n', 257)] = 1 });

        using var scope = _fixture.BeginScope();
        var page = await scope.Resolve<IWorkflowRunCellFieldReader>().ReadAsync(Request(teamId, runId, runId, limit: 50), CancellationToken.None);

        page.ShouldNotBeNull();
        page!.FieldsAvailability.ShouldBe(WorkflowRunCellFieldAvailability.NameTooLarge);
        page.Fields.ShouldBeEmpty("the oversize identity itself never crosses the bounded wire");
        page.NextCursor.ShouldBeNull();
        page.InputsAvailability.ShouldBe(WorkflowRunCellFieldAvailability.NotRecorded,
            "rerun-seeded terminal-only cells have no node.started; this is not an observed empty input bag");
        page.OutputsAvailability.ShouldBe(WorkflowRunCellFieldAvailability.Available);
        page.ErrorAvailability.ShouldBe(WorkflowRunCellFieldAvailability.NotRecorded);
    }

    [Fact]
    public async Task Foreign_wrong_source_and_cursor_after_a_new_state_are_all_rejected_as_absent_coordinates()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var original = await SeedRunAsync(teamId, null, null, DateTimeOffset.UtcNow.AddMinutes(-1));
        var latest = await SeedRunAsync(teamId, original, original, DateTimeOffset.UtcNow);
        await SeedCellAsync(original, new Dictionary<string, object?> { ["old"] = 1 }, new Dictionary<string, object?> { ["old"] = 1 }, null);
        await SeedCellAsync(latest, new Dictionary<string, object?> { ["new"] = 1 },
            Enumerable.Range(0, 2).ToDictionary(value => $"out{value}", value => (object?)value), null);

        using var scope = _fixture.BeginScope();
        var reader = scope.Resolve<IWorkflowRunCellFieldReader>();
        (await reader.ReadAsync(Request(Guid.NewGuid(), latest, latest, limit: 1), CancellationToken.None)).ShouldBeNull();
        (await reader.ReadAsync(Request(teamId, latest, original, limit: 1), CancellationToken.None)).ShouldBeNull(
            "the source must be the latest lineage attempt selected by #1611, not merely a team-owned attempt");

        var first = await reader.ReadAsync(Request(teamId, latest, latest, limit: 1), CancellationToken.None);
        first!.NextCursor.ShouldNotBeNull();
        await AppendFailedStateAsync(latest);

        (await reader.ReadAsync(Request(teamId, latest, latest, limit: 1, cursor: first.NextCursor), CancellationToken.None)).ShouldBeNull(
            "a cursor bound to an older immutable state record cannot mix with fields admitted after a new state");
    }

    [Fact]
    public async Task Exact_cell_and_field_queries_use_run_and_record_indexes_with_a_large_inline_sibling()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, null, null, DateTimeOffset.UtcNow);
        var outputs = Enumerable.Range(0, 105).ToDictionary(index => $"field{index:000}", index => (object?)index, StringComparer.Ordinal);
        outputs["zzBomb"] = Bomb + new string('b', 4 * 1024 * 1024);
        var records = await SeedCellAsync(runId, new Dictionary<string, object?>(), outputs, null);
        await BulkSeedForeignCellsAsync(runId, 10_050);

        using var scope = _fixture.BeginScope();
        var admitted = await scope.Resolve<IWorkflowRunViewAdmission>().AdmitAsync(runId, teamId, WorkflowRunViewScope.AttemptOnly, CancellationToken.None);
        var cellPlan = await ExplainSelectedCellAsync(scope.Resolve<CodeSpaceDbContext>(), admitted!);
        var fieldPlan = await ExplainFieldsAsync(scope.Resolve<CodeSpaceDbContext>(), runId, records);

        cellPlan.ShouldNotContain("Seq Scan on workflow_run_record", Case.Sensitive);
        cellPlan.ShouldContain("idx_wrr_run_node", Case.Sensitive);
        fieldPlan.ShouldNotContain("Seq Scan on workflow_run_record", Case.Sensitive);
        fieldPlan.ShouldContain("Index Scan", Case.Sensitive, "the immutable first/latest record identities must stay index-shaped");
        fieldPlan.ShouldNotContain("Sort Method: external", Case.Sensitive);
    }

    private ILifetimeScope ReadScope(DbCommandInterceptor interceptor) => _fixture.BeginScope(builder =>
    {
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention().AddInterceptors(interceptor).Options;
        builder.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance();
    });

    private static WorkflowRunCellFieldReadRequest Request(Guid teamId, Guid requestedRunId, Guid sourceRunId, int limit, string? cursor = null) => new()
    {
        TeamId = teamId, RequestedRunId = requestedRunId, Scope = WorkflowRunViewScope.LineageMerged,
        SourceRunId = sourceRunId, NodeId = "work", IterationKey = string.Empty, Cursor = cursor, Limit = limit,
    };

    private async Task<ArtifactFact> PutArtifactAsync(Guid teamId, byte[] bytes, string contentType = "application/json")
    {
        using var scope = _fixture.BeginScope();
        var id = await scope.Resolve<IArtifactStore>().PutAsync(teamId, bytes, contentType, CancellationToken.None);
        var metadata = await scope.Resolve<IArtifactStore>().GetMetadataAsync(teamId, id, CancellationToken.None);
        return new ArtifactFact(id, bytes.LongLength, metadata!.Sha256);
    }

    private async Task<Guid> SeedRunAsync(Guid teamId, Guid? parentRunId, Guid? rootRunId, DateTimeOffset createdAt)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = parentRunId is null ? WorkflowRunSourceTypes.Snapshot : WorkflowRunSourceTypes.Rerun,
            ActorType = "user", ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}", RequestMetadataJson = "{}",
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = createdAt, VerifiedAt = createdAt, NormalizedAt = createdAt,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = parentRunId is null ? WorkflowRunSourceTypes.Snapshot : WorkflowRunSourceTypes.Rerun,
            DefinitionSnapshotJson = "{\"nodes\":[],\"edges\":[]}", DefinitionSnapshotHash = "sha256:test", ParentRunId = parentRunId,
            RootRunId = rootRunId, Status = WorkflowRunStatus.Success, OutputsJson = "{}", CreatedDate = createdAt,
            CreatedBy = SystemUsers.SeederId, LastModifiedDate = createdAt, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
        return runId;
    }

    private async Task<RecordFact> SeedCellAsync(Guid runId, IReadOnlyDictionary<string, object?> inputs,
        IReadOnlyDictionary<string, object?> outputs, string? error)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var started = new WorkflowRunRecord
        {
            Id = Guid.NewGuid(), RunId = runId, RecordType = WorkflowRunRecordTypes.NodeStarted, NodeId = "work",
            IterationKey = string.Empty, OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            PayloadJson = JsonSerializer.Serialize(new { inputs, config = new { configSecret = Bomb } }),
        };
        db.WorkflowRunRecord.Add(started);
        await db.SaveChangesAsync();
        var state = new WorkflowRunRecord
        {
            Id = Guid.NewGuid(), RunId = runId, RecordType = WorkflowRunRecordTypes.NodeCompleted, NodeId = "work",
            IterationKey = string.Empty, OccurredAt = DateTimeOffset.UtcNow,
            PayloadJson = JsonSerializer.Serialize(new { outputs, error }),
        };
        db.WorkflowRunRecord.Add(state);
        await db.SaveChangesAsync();
        return new RecordFact(started.Id, started.Sequence, state.Id, state.Sequence);
    }

    private async Task<RecordFact> SeedTerminalOnlyCellAsync(Guid runId, IReadOnlyDictionary<string, object?> outputs)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var state = new WorkflowRunRecord
        {
            Id = Guid.NewGuid(), RunId = runId, RecordType = WorkflowRunRecordTypes.NodeCompleted, NodeId = "work",
            IterationKey = string.Empty, OccurredAt = DateTimeOffset.UtcNow, PayloadJson = JsonSerializer.Serialize(new { outputs }),
        };
        db.WorkflowRunRecord.Add(state);
        await db.SaveChangesAsync();
        return new RecordFact(Guid.Empty, 0, state.Id, state.Sequence);
    }

    private async Task AppendFailedStateAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.WorkflowRunRecord.Add(new WorkflowRunRecord
        {
            Id = Guid.NewGuid(), RunId = runId, RecordType = WorkflowRunRecordTypes.NodeFailed, NodeId = "work",
            IterationKey = string.Empty, OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{\"outputs\":{},\"error\":\"later\"}",
        });
        await db.SaveChangesAsync();
    }

    private async Task BulkSeedForeignCellsAsync(Guid runId, int count)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO workflow_run_record (id, run_id, record_type, node_id, iteration_key, occurred_at, payload_json)
            SELECT gen_random_uuid(), @run_id, 'node.completed', 'foreign-' || value, '', now(), '{}'::jsonb
            FROM generate_series(1, @count) AS value
            """, connection);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("count", count);
        await command.ExecuteNonQueryAsync();
        await using var analyze = new NpgsqlCommand("ANALYZE workflow_run_record", connection);
        await analyze.ExecuteNonQueryAsync();
    }

    private static Dictionary<string, object?> Inputs(Guid artifactId) => new(StringComparer.Ordinal)
    {
        ["alpha"] = 1,
        ["pointerLike"] = Ref(artifactId, 123),
        ["zzBomb"] = Bomb + new string('i', 512 * 1024),
    };

    private static Dictionary<string, object?> Outputs(ArtifactFact artifact, ArtifactFact wrongTypeArtifact, ArtifactFact foreignArtifact,
        Guid missingId) => new(StringComparer.Ordinal)
    {
        ["inline"] = new { ok = true },
        ["stored"] = Ref(artifact.Id, artifact.Size),
        ["missing"] = Ref(missingId, 10),
        ["foreign"] = Ref(foreignArtifact.Id, foreignArtifact.Size),
        ["malformed"] = new Dictionary<string, object?> { ["$artifact_ref"] = new { id = "not-a-guid" } },
        ["mismatch"] = Ref(artifact.Id, artifact.Size + 1),
        ["wrongDeclaredType"] = new Dictionary<string, object?> { ["$artifact_ref"] = new { id = artifact.Id, size_bytes = artifact.Size, content_type = "text/plain" } },
        ["wrongStoredType"] = Ref(wrongTypeArtifact.Id, wrongTypeArtifact.Size),
    };

    private static Dictionary<string, object?> Ref(Guid id, long size) => new()
    {
        ["$artifact_ref"] = new { id, size_bytes = size, content_type = "application/json" },
    };

    private static async Task<string> ExplainSelectedCellAsync(CodeSpaceDbContext db, WorkflowRunViewAdmission admission)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var command = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT) " + WorkflowRunViewAdmissionService.SelectedCellsSql, connection);
            command.Parameters.AddWithValue("run_ids", admission.Lineage.Select(value => value.Id).ToArray());
            command.Parameters.AddWithValue("node_id", "work");
            command.Parameters.AddWithValue("iteration_key", string.Empty);
            command.Parameters.AddWithValue("take", 2);
            command.Parameters.AddWithValue("max_identity_chars", WorkflowRunViewAdmissionService.MaximumIdentityCharacters);
            return await ReadPlanAsync(command);
        }
        finally { await connection.CloseAsync(); }
    }

    private static async Task<string> ExplainFieldsAsync(CodeSpaceDbContext db, Guid runId, RecordFact records)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var command = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT) " + WorkflowRunCellFieldReader.FieldSql, connection);
            command.Parameters.AddWithValue("source_run_id", runId);
            command.Parameters.AddWithValue("node_id", "work");
            command.Parameters.AddWithValue("iteration_key", string.Empty);
            command.Parameters.AddWithValue("state_record_id", records.StateId);
            command.Parameters.AddWithValue("state_record_sequence", records.StateSequence);
            command.Parameters.AddWithValue("first_record_id", records.StartedId);
            command.Parameters.AddWithValue("first_record_sequence", records.StartedSequence);
            command.Parameters.Add("cursor_section", NpgsqlTypes.NpgsqlDbType.Integer).Value = DBNull.Value;
            command.Parameters.Add("cursor_name", NpgsqlTypes.NpgsqlDbType.Text).Value = DBNull.Value;
            command.Parameters.AddWithValue("max_name_chars", WorkflowRunCellFieldReader.MaximumFieldNameCharacters);
            command.Parameters.AddWithValue("max_name_bytes", WorkflowRunCellFieldReader.MaximumFieldNameUtf8Bytes);
            command.Parameters.AddWithValue("max_ref_id_chars", WorkflowRunCellFieldReader.MaximumArtifactIdCharacters);
            command.Parameters.AddWithValue("max_declared_size_chars", WorkflowRunCellFieldReader.MaximumDeclaredSizeCharacters);
            command.Parameters.AddWithValue("max_content_type_chars", WorkflowRunCellFieldReader.MaximumContentTypeCharacters);
            command.Parameters.AddWithValue("take", 101);
            return await ReadPlanAsync(command);
        }
        finally { await connection.CloseAsync(); }
    }

    private static async Task<string> ReadPlanAsync(NpgsqlCommand command)
    {
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
        return string.Join('\n', lines);
    }

    private sealed record ArtifactFact(Guid Id, long Size, string Sha256);
    private sealed record RecordFact(Guid StartedId, long StartedSequence, Guid StateId, long StateSequence);

    private sealed class ReadCommandRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
