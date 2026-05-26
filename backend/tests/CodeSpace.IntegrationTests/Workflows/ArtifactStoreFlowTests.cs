using System.Text;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Content-addressable storage. End-to-end coverage against the real DB:
///   - Round-trip: put bytes, get bytes back identical
///   - Idempotent dedup: same bytes from the same team → same id
///   - Tenant isolation: team A's artifact invisible to team B
///   - Metadata-only read: returns size + sha + content type without bytes
///   - Threshold rejection: oversize bytes raise InvalidOperationException
///   - Immutability trigger: UPDATE rejected
///   - Immutability trigger: DELETE rejected by default
///   - Immutability trigger: DELETE allowed when session bypass set
///   - Reference shape: an artifact id can be embedded in a workflow_run_record's payload_json
/// </summary>
[Collection(PostgresCollection.Name)]
public class ArtifactStoreFlowTests
{
    private readonly PostgresFixture _fixture;

    public ArtifactStoreFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Put_then_get_round_trips_identical_bytes()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var content = Encoding.UTF8.GetBytes("hello, artifact world — this is content");

        Guid artifactId;
        using (var scope = _fixture.BeginScope())
        {
            artifactId = await scope.Resolve<IArtifactStore>().PutAsync(teamId, content, "text/plain", CancellationToken.None);
        }

        ArtifactBytes? fetched;
        using (var scope = _fixture.BeginScope())
        {
            fetched = await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, artifactId, CancellationToken.None);
        }

        fetched.ShouldNotBeNull();
        fetched!.Bytes.ShouldBe(content);
        fetched.ContentType.ShouldBe("text/plain");
        fetched.Id.ShouldBe(artifactId);
        fetched.Sha256.ShouldBe(ArtifactStore.ComputeSha256Hex(content));
    }

    [Fact]
    public async Task Put_same_bytes_twice_same_team_returns_same_id_no_duplicate_row()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var content = Encoding.UTF8.GetBytes("dedup-this");

        Guid id1, id2;
        using (var scope = _fixture.BeginScope())
        {
            id1 = await scope.Resolve<IArtifactStore>().PutAsync(teamId, content, "application/octet-stream", CancellationToken.None);
        }
        using (var scope = _fixture.BeginScope())
        {
            id2 = await scope.Resolve<IArtifactStore>().PutAsync(teamId, content, "application/octet-stream", CancellationToken.None);
        }

        id1.ShouldBe(id2, "idempotency contract: same (team, sha) returns the original id");

        // Verify only one row exists.
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var sha = ArtifactStore.ComputeSha256Hex(content);
        var rowCount = await db.WorkflowArtifact.AsNoTracking().CountAsync(a => a.TeamId == teamId && a.Sha256 == sha);
        rowCount.ShouldBe(1);
    }

    [Fact]
    public async Task Put_same_bytes_two_teams_creates_two_distinct_rows()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var content = Encoding.UTF8.GetBytes("cross-team-same-content");

        Guid idA, idB;
        using (var scope = _fixture.BeginScope())
        {
            idA = await scope.Resolve<IArtifactStore>().PutAsync(teamA, content, "text/plain", CancellationToken.None);
        }
        using (var scope = _fixture.BeginScope())
        {
            idB = await scope.Resolve<IArtifactStore>().PutAsync(teamB, content, "text/plain", CancellationToken.None);
        }

        idA.ShouldNotBe(idB,
            "cross-team dedup is intentionally OFF — same bytes from two teams produce distinct rows " +
            "so an artifact's existence isn't observable across the tenancy boundary");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var sha = ArtifactStore.ComputeSha256Hex(content);
        (await db.WorkflowArtifact.AsNoTracking().CountAsync(a => a.Sha256 == sha)).ShouldBe(2);
    }

    [Fact]
    public async Task Get_with_wrong_team_id_returns_null()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var content = Encoding.UTF8.GetBytes("team-A-only");

        Guid artifactId;
        using (var scope = _fixture.BeginScope())
        {
            artifactId = await scope.Resolve<IArtifactStore>().PutAsync(teamA, content, "text/plain", CancellationToken.None);
        }

        using var verify = _fixture.BeginScope();
        var store = verify.Resolve<IArtifactStore>();

        var bytesFromB = await store.GetBytesAsync(teamB, artifactId, CancellationToken.None);
        var metaFromB = await store.GetMetadataAsync(teamB, artifactId, CancellationToken.None);

        bytesFromB.ShouldBeNull("team B has no membership of team A's artifacts; conflated not-found / not-yours");
        metaFromB.ShouldBeNull();

        // Sanity: team A still sees it.
        var bytesFromA = await store.GetBytesAsync(teamA, artifactId, CancellationToken.None);
        bytesFromA.ShouldNotBeNull();
    }

    [Fact]
    public async Task Metadata_query_returns_size_sha_content_type_without_bytes()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var content = Encoding.UTF8.GetBytes("metadata-only-please");

        Guid artifactId;
        using (var scope = _fixture.BeginScope())
        {
            artifactId = await scope.Resolve<IArtifactStore>().PutAsync(teamId, content, "application/json", CancellationToken.None);
        }

        using var verify = _fixture.BeginScope();
        var meta = await verify.Resolve<IArtifactStore>().GetMetadataAsync(teamId, artifactId, CancellationToken.None);

        meta.ShouldNotBeNull();
        meta!.Id.ShouldBe(artifactId);
        meta.SizeBytes.ShouldBe(content.Length);
        meta.ContentType.ShouldBe("application/json");
        meta.Sha256.ShouldBe(ArtifactStore.ComputeSha256Hex(content));
        meta.CreatedAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Put_with_empty_content_type_throws_ArgumentException()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScope();
        var store = scope.Resolve<IArtifactStore>();

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
        {
            await store.PutAsync(teamId, Encoding.UTF8.GetBytes("data"), contentType: "", CancellationToken.None);
        });

        ex.ParamName.ShouldBe("contentType");
    }

    [Fact]
    public async Task Put_bytes_over_threshold_throws_InvalidOperationException_with_actionable_message()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // Default threshold is 8 KiB; create 16 KiB to comfortably exceed it.
        var oversize = new byte[16 * 1024];
        Array.Fill<byte>(oversize, 0x42);

        using var scope = _fixture.BeginScope();
        var store = scope.Resolve<IArtifactStore>();

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await store.PutAsync(teamId, oversize, "application/octet-stream", CancellationToken.None);
        });

        // Error message must surface both the size + the env var to flip — Rule 11 + 8.
        ex.Message.ShouldContain("inline threshold");
        ex.Message.ShouldContain(ArtifactStoreConfig.InlineThresholdEnvVar);
    }

    [Fact]
    public async Task Direct_UPDATE_on_artifact_row_rejected_by_immutability_trigger()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        Guid artifactId;
        using (var scope = _fixture.BeginScope())
        {
            artifactId = await scope.Resolve<IArtifactStore>().PutAsync(teamId, Encoding.UTF8.GetBytes("immutable"), "text/plain", CancellationToken.None);
        }

        using var scope2 = _fixture.BeginScope();
        var db = scope2.Resolve<CodeSpaceDbContext>();

        var ex = await Should.ThrowAsync<Npgsql.PostgresException>(async () =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE workflow_artifact SET content_type = 'text/html' WHERE id = {artifactId}");
        });
        ex.MessageText.ShouldContain("immutable");
    }

    [Fact]
    public async Task Direct_DELETE_on_artifact_row_rejected_by_default()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        Guid artifactId;
        using (var scope = _fixture.BeginScope())
        {
            artifactId = await scope.Resolve<IArtifactStore>().PutAsync(teamId, Encoding.UTF8.GetBytes("survives-delete-attempt"), "text/plain", CancellationToken.None);
        }

        using var scope2 = _fixture.BeginScope();
        var db = scope2.Resolve<CodeSpaceDbContext>();

        var ex = await Should.ThrowAsync<Npgsql.PostgresException>(async () =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM workflow_artifact WHERE id = {artifactId}");
        });
        ex.MessageText.ShouldContain("immutable");
    }

    [Fact]
    public async Task Direct_DELETE_allowed_when_session_bypass_set()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        Guid artifactId;
        using (var scope = _fixture.BeginScope())
        {
            artifactId = await scope.Resolve<IArtifactStore>().PutAsync(teamId, Encoding.UTF8.GetBytes("purgeable-with-bypass"), "text/plain", CancellationToken.None);
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            // SET LOCAL is transaction-scoped. The default EF execution model uses an
            // implicit auto-commit transaction per statement; wrap in an explicit
            // transaction so the SET LOCAL stays in scope through the DELETE.
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("SET LOCAL codespace.artifact_purge_allowed = 'on'");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM workflow_artifact WHERE id = {artifactId}");
            await tx.CommitAsync();
        }

        // After purge, the row is gone.
        using var verify = _fixture.BeginScope();
        var db2 = verify.Resolve<CodeSpaceDbContext>();
        (await db2.WorkflowArtifact.AsNoTracking().CountAsync(a => a.Id == artifactId)).ShouldBe(0);
    }

    [Fact]
    public async Task Record_can_reference_artifact_id_in_payload_json_round_trip()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await SeedWorkflowAsync(teamId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        // Put an artifact, then write a workflow_run_record whose payload_json references it
        // by id — exercise the canonical wire shape that external_call.completed will use.
        Guid artifactId;
        using (var scope = _fixture.BeginScope())
        {
            artifactId = await scope.Resolve<IArtifactStore>().PutAsync(teamId, Encoding.UTF8.GetBytes("response-body"), "application/json", CancellationToken.None);
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunRecord.Add(new WorkflowRunRecord
            {
                Id = Guid.NewGuid(),
                RunId = runId,
                RecordType = WorkflowRunRecordTypes.ExternalCallCompleted,
                NodeId = "http_call",
                IterationKey = string.Empty,
                CorrelationId = Guid.NewGuid(),
                OccurredAt = DateTimeOffset.UtcNow,
                PayloadJson = $$"""{"status":200,"response_artifact_id":"{{artifactId}}","duration_ms":42}""",
            });
            await db.SaveChangesAsync();
        }

        using var verify = _fixture.BeginScope();
        var verifyDb = verify.Resolve<CodeSpaceDbContext>();
        var rec = await verifyDb.WorkflowRunRecord.AsNoTracking()
            .SingleAsync(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.ExternalCallCompleted);

        var payload = System.Text.Json.JsonDocument.Parse(rec.PayloadJson).RootElement;
        var refId = Guid.Parse(payload.GetProperty("response_artifact_id").GetString()!);
        refId.ShouldBe(artifactId,
            "the record payload must round-trip the artifact id so the UI can resolve and render it");

        // And the artifact is still fetchable by that id.
        var bytes = await verify.Resolve<IArtifactStore>().GetBytesAsync(teamId, refId, CancellationToken.None);
        bytes.ShouldNotBeNull();
        bytes!.Bytes.ShouldBe(Encoding.UTF8.GetBytes("response-body"));
    }

    [Fact]
    public async Task Empty_byte_array_round_trips()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        Guid artifactId;
        using (var scope = _fixture.BeginScope())
        {
            artifactId = await scope.Resolve<IArtifactStore>().PutAsync(teamId, Array.Empty<byte>(), "application/octet-stream", CancellationToken.None);
        }

        using var verify = _fixture.BeginScope();
        var bytes = await verify.Resolve<IArtifactStore>().GetBytesAsync(teamId, artifactId, CancellationToken.None);

        bytes.ShouldNotBeNull();
        bytes!.Bytes.Length.ShouldBe(0);
        bytes.SizeBytesShouldMatch(0);
        bytes.Sha256.ShouldBe("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "SHA-256 of empty string is a well-known constant");
    }

    [Fact]
    public async Task At_threshold_boundary_8KiB_exactly_is_accepted_as_inline()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // Exactly the threshold should be accepted (the check is "> threshold", not ">=").
        var atThreshold = new byte[ArtifactStoreConfig.DefaultInlineThresholdBytes];
        Array.Fill<byte>(atThreshold, 0xA5);

        using var scope = _fixture.BeginScope();
        var store = scope.Resolve<IArtifactStore>();

        var id = await store.PutAsync(teamId, atThreshold, "application/octet-stream", CancellationToken.None);

        var fetched = await store.GetBytesAsync(teamId, id, CancellationToken.None);
        fetched.ShouldNotBeNull();
        fetched!.Bytes.Length.ShouldBe(ArtifactStoreConfig.DefaultInlineThresholdBytes);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> SeedWorkflowAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var workflowId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Workflow.Add(new Workflow
        {
            Id = workflowId,
            TeamId = teamId,
            Name = "artifact-test-" + Guid.NewGuid().ToString("N")[..6],
            DefinitionJson = "{}",
            LatestVersion = 1,
            Enabled = true,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });
        db.WorkflowVersion.Add(new WorkflowVersion
        {
            WorkflowId = workflowId,
            Version = 1,
            DefinitionJson = "{}",
            DefinitionHash = "0000000000000000000000000000000000000000000000000000000000000000",
            CommittedAt = now,
            CreatedDate = now,
        });
        await db.SaveChangesAsync();
        return workflowId;
    }
}

internal static class ArtifactBytesShouldlyExtensions
{
    /// <summary>Convenience for assertions on the size — the bytes payload is what matters, this is a sanity hint.</summary>
    public static void SizeBytesShouldMatch(this ArtifactBytes self, int expected) =>
        self.Bytes.Length.ShouldBe(expected);
}
