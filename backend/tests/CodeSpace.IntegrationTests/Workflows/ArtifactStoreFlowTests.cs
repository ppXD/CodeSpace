using System.Text;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Backends;
using CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;
using CodeSpace.Core.Services.Workflows.Artifacts.Retention;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Artifacts;
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
///   - Threshold offload (D2): oversize bytes go out-of-band (storage_url) + round-trip identical; dedup holds
///   - Immutability trigger: UPDATE rejected
///   - Immutability trigger: DELETE rejected by default
///   - Immutability trigger: DELETE allowed when session bypass set
///   - Reference shape: an artifact id can be embedded in a workflow_run_record's payload_json
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
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
    public async Task A_tampered_offloaded_blob_refuses_to_read()
    {
        // P2 slice 2: the row's sha/size are the store's IDENTITY CLAIM about the content — a blob that no longer
        // matches (corruption, truncation, a foreign write under the content-addressed path) must never flow
        // silently into a prompt, a patch apply, or an evidence read.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var content = Encoding.UTF8.GetBytes(new string('x', 20_000));   // over the inline threshold → offloaded

        Guid artifactId;
        using (var scope = _fixture.BeginScope())
            artifactId = await scope.Resolve<IArtifactStore>().PutAsync(teamId, content, "text/plain", CancellationToken.None);

        string storageUrl;
        using (var scope = _fixture.BeginScope())
            storageUrl = (await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifact.AsNoTracking().SingleAsync(a => a.Id == artifactId)).StorageUrl!;

        await File.WriteAllBytesAsync(new Uri(storageUrl).LocalPath, Encoding.UTF8.GetBytes(new string('x', 19_999) + "y"));

        using (var scope = _fixture.BeginScope())
        {
            var ex = await Should.ThrowAsync<ArtifactContentUnavailableException>(
                scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, artifactId, CancellationToken.None));
            ex.Kind.ShouldBe(ArtifactContentUnavailableKind.IntegrityFailure, "same size, different bytes — the sha catches what the size cannot");
            ex.InnerException!.Message.ShouldContain("read-back verification", customMessage: "the diagnostic survives on the inner exception, where it reaches the log and not the caller");
        }
    }

    [Fact]
    public async Task A_deleted_offloaded_blob_reads_as_a_missing_physical_object()
    {
        // The local lane is the shipped state of every unrouted team, and it was the one whole-object read with no
        // verdict at all: a wiped root escaped as a raw FileNotFoundException, so nothing downstream could tell
        // "the bytes are gone" from "this code has a bug".
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var content = Encoding.UTF8.GetBytes(new string('m', 20_000));   // over the inline threshold → offloaded

        Guid artifactId;
        using (var scope = _fixture.BeginScope())
            artifactId = await scope.Resolve<IArtifactStore>().PutAsync(teamId, content, "text/plain", CancellationToken.None);

        string storageUrl;
        using (var scope = _fixture.BeginScope())
            storageUrl = (await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifact.AsNoTracking().SingleAsync(a => a.Id == artifactId)).StorageUrl!;

        File.Delete(new Uri(storageUrl).LocalPath);

        using (var scope = _fixture.BeginScope())
        {
            var ex = await Should.ThrowAsync<ArtifactContentUnavailableException>(
                scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, artifactId, CancellationToken.None));
            ex.ArtifactId.ShouldBe(artifactId);
            ex.Kind.ShouldBe(ArtifactContentUnavailableKind.PhysicalObjectMissing,
                "the row's identity claim survives its bytes — the read must say WHICH of the two is gone");
        }
    }

    [Fact]
    public async Task A_storage_url_the_backend_refuses_to_follow_is_a_storage_fact_not_an_escaping_bug()
    {
        // The local backend GUARDS its locator: a url resolving outside the configured root is refused before any
        // filesystem touch (LocalFileArtifactBlobBackend.ResolveUnderRoot), and it says so with an
        // InvalidOperationException. That is a storage-plane fact — the stored copy cannot be produced — not a bug in
        // the reading code, which is why the shared table must keep claiming it. Untyped it escapes the whole-object
        // read, the failure classifier reads it as the caller's fault, and the run-detail read answers 400 again:
        // the very defect this slice closes, reached by a moved mount instead of a rotted file.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var content = Encoding.UTF8.GetBytes(new string('o', 20_000) + Guid.NewGuid());   // over the inline threshold → offloaded; guid-unique so the object it leaves in the shared sha-keyed root is nobody else's file

        Guid artifactId;
        using (var scope = _fixture.BeginScope())
            artifactId = await scope.Resolve<IArtifactStore>().PutAsync(teamId, content, "text/plain", CancellationToken.None);

        // The operator repointed the durable mount. The row's own file:// url now names a place this backend will
        // not follow — the real production trigger, staged without touching the immutable row.
        var movedRoot = Path.Combine(Path.GetTempPath(), $"artifact-root-{Guid.NewGuid():N}");

        using var moved = _fixture.BeginScope(builder => builder
            .RegisterInstance(new LocalFileArtifactBlobBackend(movedRoot)).As<IArtifactBlobBackend>().SingleInstance());

        var ex = await Should.ThrowAsync<ArtifactContentUnavailableException>(
            moved.Resolve<IArtifactStore>().GetBytesAsync(teamId, artifactId, CancellationToken.None));

        ex.ArtifactId.ShouldBe(artifactId);
        ex.Kind.ShouldBe(ArtifactContentUnavailableKind.IntegrityFailure,
            "the row's own locator no longer describes an object the backend will produce — a refusal the reader must be able to shed, not an exception that kills the run");
    }

    [Fact]
    public async Task A_bounded_read_hands_back_a_disposal_defect_rather_than_dressing_it_as_a_storage_fact()
    {
        // What the bounded read never RAISES is a storage-plane FACT — which is not the same as "never throws": a
        // cancel already leaves as itself, and so does our own defect. An ObjectDisposedException used to come back
        // from here as an IntegrityFailure purely because it derives from the locator refusal the shared table does
        // claim. Reported, it sends an operator to restore a destination that is perfectly healthy while the leaked
        // lease keeps rotting cells; raised, it lands exactly where the whole-object read already puts it.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // Over the inline threshold, so the read has to go to the backend — and content-addressed with a fresh guid,
        // because the local blob root is one shared directory keyed by sha: fixed filler makes this test's leftover
        // object the very file a routed sibling asserts is absent.
        var content = Encoding.UTF8.GetBytes(new string('d', 20_000) + Guid.NewGuid());

        Guid artifactId;
        using (var scope = _fixture.BeginScope())
            artifactId = await scope.Resolve<IArtifactStore>().PutAsync(teamId, content, "text/plain", CancellationToken.None);

        using var leaked = _fixture.BeginScope(builder => builder
            .RegisterInstance(new AskedAfterItsLeaseWasLetGo()).As<IArtifactBlobBackend>().SingleInstance());

        await Should.ThrowAsync<ObjectDisposedException>(
            leaked.Resolve<IArtifactRangeReader>().ReadRangeAsync(teamId, artifactId, 0, 512, CancellationToken.None));
    }

    /// <summary>A backend asked for bytes after its own lease was let go. Our defect, whatever it derives from — never a verdict about the destination.</summary>
    private sealed class AskedAfterItsLeaseWasLetGo : IArtifactBlobBackend
    {
        public Task<string> WriteAsync(string sha256, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> ExistsAsync(string storageUrl, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<byte[]> ReadAsync(string storageUrl, CancellationToken cancellationToken) => throw new ObjectDisposedException(nameof(Stream));

        public Task<ArtifactBlobRange> ReadRangeAsync(string storageUrl, long offset, int length, CancellationToken cancellationToken) => throw new ObjectDisposedException(nameof(Stream));
    }

    [Fact]
    public async Task A_dedup_hit_restores_a_missing_blob_instead_of_returning_a_dead_reference()
    {
        // P2 slice 3: an offloaded blob under a wiped (or once-unconfigured) root can be gone while the row's
        // identity claim survives. The dedup hit HOLDS the exact bytes the claim describes — restoring beats
        // handing back an id whose read is doomed.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var content = Encoding.UTF8.GetBytes(new string('z', 20_000));   // over the inline threshold → offloaded

        Guid firstId;
        using (var scope = _fixture.BeginScope())
            firstId = await scope.Resolve<IArtifactStore>().PutAsync(teamId, content, "text/plain", CancellationToken.None);

        string storageUrl;
        using (var scope = _fixture.BeginScope())
            storageUrl = (await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifact.AsNoTracking().SingleAsync(a => a.Id == firstId)).StorageUrl!;

        File.Delete(new Uri(storageUrl).LocalPath);

        Guid secondId;
        using (var scope = _fixture.BeginScope())
            secondId = await scope.Resolve<IArtifactStore>().PutAsync(teamId, content, "text/plain", CancellationToken.None);

        secondId.ShouldBe(firstId, "the dedup contract holds — same (team, sha), same id");

        using (var scope = _fixture.BeginScope())
        {
            var fetched = await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, firstId, CancellationToken.None);
            fetched.ShouldNotBeNull().Bytes.ShouldBe(content, "the blob was restored — and the read's own verification proves the restored content byte-exact");
        }
    }

    [Fact]
    public async Task The_immutability_trigger_blocks_an_inline_mutation_at_the_database()
    {
        // The inline surface's REAL defense (discovered by this slice's first CI round): workflow_artifact carries
        // an immutability trigger — an UPDATE is rejected at the database, so a mutated inline row is structurally
        // unreachable through SQL. The read-back verification remains as defense-in-depth (and as the ONLY guard
        // for the offloaded blob surface, where no trigger can reach the filesystem).
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var content = Encoding.UTF8.GetBytes("small inline content");

        Guid artifactId;
        using (var scope = _fixture.BeginScope())
            artifactId = await scope.Resolve<IArtifactStore>().PutAsync(teamId, content, "text/plain", CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var row = await db.WorkflowArtifact.SingleAsync(a => a.Id == artifactId);
            row.InlineBytes = Encoding.UTF8.GetBytes("small inline CONTENT");

            var ex = await Should.ThrowAsync<DbUpdateException>(db.SaveChangesAsync());
            ex.InnerException.ShouldNotBeNull().Message.ShouldContain("immutable", customMessage: "the database itself refuses to rewrite an artifact's identity");
        }
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
    public async Task Concurrent_declared_and_plain_writes_of_the_same_sha_revoke_the_winner_declaration()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var content = Encoding.UTF8.GetBytes(new string('r', ArtifactStoreConfig.DefaultInlineThresholdBytes + 500) + Guid.NewGuid());
        var holderId = Guid.NewGuid();
        using var root = _fixture.BeginScope();
        var coordinator = new OrderedArtifactWriteRace();
        var inner = root.Resolve<IArtifactBlobBackend>();
        using var declaring = _fixture.BeginScope(builder => builder.RegisterInstance<IArtifactBlobBackend>(new OrderedRaceBlobBackend(inner, coordinator, declaredLane: true)));
        using var plain = _fixture.BeginScope(builder => builder.RegisterInstance<IArtifactBlobBackend>(new OrderedRaceBlobBackend(inner, coordinator, declaredLane: false)));

        async Task<ArtifactRetentionWrite> DeclareAsync()
        {
            try
            {
                return await declaring.Resolve<IArtifactRetentionWriter>().PutDeclaredAsync(new ArtifactRetentionWriteRequest(teamId, content, "application/json",
                    ArtifactRetentionClass.AgentRunEventData, "agent_run_event", holderId), CancellationToken.None);
            }
            finally
            {
                coordinator.DeclaredCommitted.TrySetResult();
            }
        }

        var declaredTask = DeclareAsync();
        var plainTask = plain.Resolve<IArtifactStore>().PutAsync(teamId, content, "application/json", CancellationToken.None);
        await Task.WhenAll(declaredTask, plainTask);

        declaredTask.Result.Declared.ShouldBeTrue("the declaring lane is released to commit first after both initial dedup reads miss");
        plainTask.Result.ShouldBe(declaredTask.Result.ArtifactId, "the forced unique-index loser returns the winner's content identity");
        using var verify = _fixture.BeginScope();
        var declaration = await verify.Resolve<CodeSpaceDbContext>().WorkflowArtifactRetention.AsNoTracking()
            .SingleAsync(row => row.ArtifactId == declaredTask.Result.ArtifactId);
        declaration.State.ShouldBe(ArtifactRetentionState.Revoked,
            "the unique-violation recovery must run the same revoke fence as an ordinary dedup hit before the shared id escapes");
        declaration.HolderId.ShouldBe(holderId);
        (await verify.Resolve<IArtifactStore>().GetBytesAsync(teamId, plainTask.Result, CancellationToken.None)).ShouldNotBeNull().Bytes.ShouldBe(content);
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
    public async Task Put_bytes_over_threshold_offloads_out_of_band_and_round_trips_identical_bytes()
    {
        // D2: oversize bytes are no longer rejected — they're offloaded to the IArtifactBlobBackend and the row
        // keeps only a storage_url (inline_bytes null), yet GetBytesAsync transparently resolves them back.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // Default threshold is 8 KiB; create 64 KiB of pseudo-random bytes (not all-same, so it's a real payload).
        var oversize = new byte[64 * 1024];
        for (var i = 0; i < oversize.Length; i++) oversize[i] = (byte)((i * 31 + 7) & 0xFF);

        Guid artifactId;
        using (var scope = _fixture.BeginScope())
            artifactId = await scope.Resolve<IArtifactStore>().PutAsync(teamId, oversize, "application/octet-stream", CancellationToken.None);

        // The DB row is the metadata-only shape: inline_bytes NULL, storage_url set, size recorded.
        using (var scope = _fixture.BeginScope())
        {
            var row = await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifact.AsNoTracking().SingleAsync(a => a.Id == artifactId);
            row.InlineBytes.ShouldBeNull("an offloaded artifact keeps NO bytes in the DB row");
            row.StorageUrl.ShouldNotBeNullOrEmpty("the offloaded row references the out-of-band blob");
            row.StorageUrl!.ShouldStartWith("file://", Case.Sensitive);
            row.SizeBytes.ShouldBe(oversize.Length);
        }

        // GetBytesAsync resolves the storage_url through the backend → exact bytes back.
        using (var scope = _fixture.BeginScope())
        {
            var fetched = await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, artifactId, CancellationToken.None);
            fetched.ShouldNotBeNull();
            fetched!.Bytes.ShouldBe(oversize, "the offloaded bytes round-trip byte-for-byte");
            fetched.Sha256.ShouldBe(ArtifactStore.ComputeSha256Hex(oversize));
        }
    }

    [Fact]
    public async Task Put_same_oversize_bytes_twice_dedups_to_one_id_via_the_backend()
    {
        // Content-addressed offload is idempotent: the same large payload from the same team returns the same id,
        // and the backend's content-addressed write is a no-op the second time.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var oversize = new byte[32 * 1024];
        for (var i = 0; i < oversize.Length; i++) oversize[i] = (byte)((i * 17 + 3) & 0xFF);

        Guid id1, id2;
        using (var scope = _fixture.BeginScope())
            id1 = await scope.Resolve<IArtifactStore>().PutAsync(teamId, oversize, "application/octet-stream", CancellationToken.None);
        using (var scope = _fixture.BeginScope())
            id2 = await scope.Resolve<IArtifactStore>().PutAsync(teamId, oversize, "application/octet-stream", CancellationToken.None);

        id2.ShouldBe(id1, "the same oversize content dedups to one artifact id");

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifact.AsNoTracking().CountAsync(a => a.TeamId == teamId && a.SizeBytes == oversize.Length))
                .ShouldBe(1, "no duplicate offloaded row");
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
            Slug = "artifact-" + workflowId.ToString("N")[..8],
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

    private sealed class OrderedArtifactWriteRace
    {
        private int _arrivals;
        public TaskCompletionSource BothInitialReadsMissed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DeclaredCommitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arrive()
        {
            if (Interlocked.Increment(ref _arrivals) == 2) BothInitialReadsMissed.TrySetResult();
        }
    }

    private sealed class OrderedRaceBlobBackend(IArtifactBlobBackend inner, OrderedArtifactWriteRace race, bool declaredLane) : IArtifactBlobBackend
    {
        public async Task<string> WriteAsync(string sha256, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            race.Arrive();
            await race.BothInitialReadsMissed.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            if (!declaredLane) await race.DeclaredCommitted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return await inner.WriteAsync(sha256, bytes, cancellationToken);
        }

        public Task<bool> ExistsAsync(string storageUrl, CancellationToken cancellationToken) => inner.ExistsAsync(storageUrl, cancellationToken);
        public Task<byte[]> ReadAsync(string storageUrl, CancellationToken cancellationToken) => inner.ReadAsync(storageUrl, cancellationToken);
        public Task<ArtifactBlobRange> ReadRangeAsync(string storageUrl, long offset, int length, CancellationToken cancellationToken) => inner.ReadRangeAsync(storageUrl, offset, length, cancellationToken);
    }
}

internal static class ArtifactBytesShouldlyExtensions
{
    /// <summary>Convenience for assertions on the size — the bytes payload is what matters, this is a sanity hint.</summary>
    public static void SizeBytesShouldMatch(this ArtifactBytes self, int expected) =>
        self.Bytes.Length.ShouldBe(expected);
}
