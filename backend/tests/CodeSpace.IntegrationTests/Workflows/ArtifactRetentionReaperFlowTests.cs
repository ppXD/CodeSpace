using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Retention;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Artifacts;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The retention reaper against a real database, where the parts that cannot be unit-tested actually live: the
/// immutability trigger's purge gate, the declaration that rides the artifact INSERT, the revoke that a dedup hit fires,
/// and the fact that two sweeps over one object delete it at most once.
///
/// <para>Every test here is a COUNTER-EXAMPLE: it builds an artifact that is one property away from collectable and
/// asserts the bytes survive. The single positive control at the top is what proves the others are not passing
/// vacuously.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ArtifactRetentionReaperFlowTests
{
    private readonly PostgresFixture _fixture;

    public ArtifactRetentionReaperFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_declared_artifact_nobody_referenced_is_collected_after_the_age_floor_and_the_quarantine_window()
    {
        // The positive control. Simulates the crash this class of bytes exists for: the declaring write landed, the
        // artifact_manifest row that would have referenced it never did.
        var world = await SeedWorldAsync();
        var artifactId = await DeclareAsync(world, "orphaned deliverable bytes");
        await AgeDeclarationAsync(artifactId, TimeSpan.FromDays(30));

        var first = await SweepAsync();
        first.Quarantined.ShouldBeGreaterThanOrEqualTo(1, "the first observation of an unreferenced artifact only starts the quarantine clock");
        (await ArtifactExistsAsync(artifactId)).ShouldBeTrue("the sweep that first noticed the artifact must not delete it");

        await AgeQuarantineAsync(artifactId, TimeSpan.FromDays(2));
        var second = await SweepAsync();

        second.Collected.ShouldBeGreaterThanOrEqualTo(1);
        (await ArtifactExistsAsync(artifactId)).ShouldBeFalse("both waits elapsed with no reference at any site");
        (await DeclarationAsync(artifactId)).ShouldBeNull("the ledger row goes with its artifact through the cascade");
    }

    [Fact]
    public async Task A_declared_artifact_the_manifest_row_actually_references_is_never_collected()
    {
        var world = await SeedWorldAsync();
        var artifactId = await DeclareAsync(world, "referenced deliverable bytes");
        await ReferenceFromManifestAsync(world, artifactId);
        await AgeDeclarationAsync(artifactId, TimeSpan.FromDays(30));

        await SweepAsync();
        await AgeQuarantineAsync(artifactId, TimeSpan.FromDays(2));
        await SweepAsync();

        (await ArtifactExistsAsync(artifactId)).ShouldBeTrue("a live soft link from artifact_manifest outranks any retention window");
        (await DeclarationAsync(artifactId))!.State.ShouldBe(ArtifactRetentionState.Referenced, "finding the reference settles the declaration terminally");
    }

    [Fact]
    public async Task A_reference_from_a_probed_site_the_declaration_never_named_still_keeps_the_artifact()
    {
        // The oracle checks EVERY site, not the holder the declaration named. A reference planted in a table the
        // declaring producer never writes still has to keep the bytes.
        var world = await SeedWorldAsync();
        var artifactId = await DeclareAsync(world, "bytes an agent-run event also points at");
        await ReferenceFromAgentRunEventAsync(world, artifactId);
        await AgeDeclarationAsync(artifactId, TimeSpan.FromDays(30));

        await SweepAsync();
        await AgeQuarantineAsync(artifactId, TimeSpan.FromDays(2));
        await SweepAsync();

        (await ArtifactExistsAsync(artifactId)).ShouldBeTrue("agent_run_event.data_artifact_id is a probed reference site even though no manifest row exists");
        (await DeclarationAsync(artifactId))!.State.ShouldBe(ArtifactRetentionState.Referenced);
    }

    [Fact]
    public async Task Bytes_referenced_only_from_inside_record_payload_json_are_never_candidates_at_all()
    {
        // The reference no foreign key and no column probe can see. It is safe not because the reaper inspects the
        // JSON, but because a plain PutAsync mints NO declaration — so the artifact never enters the candidate set.
        var world = await SeedWorldAsync();
        using var scope = _fixture.BeginScope();
        var store = scope.Resolve<IArtifactStore>();
        var artifactId = await store.PutAsync(world.TeamId, "a node output offloaded into payload_json"u8.ToArray(), "application/json", CancellationToken.None);
        await ReferenceFromRecordPayloadAsync(world, artifactId);
        await BackdateArtifactAsync(artifactId, TimeSpan.FromDays(400));

        await SweepAsync();

        (await DeclarationAsync(artifactId)).ShouldBeNull("a plain PutAsync must never declare — that is what keeps every JSON-borne reference safe");
        (await ArtifactExistsAsync(artifactId)).ShouldBeTrue("an undeclared artifact is unreachable by the reaper however old it is");
    }

    [Fact]
    public async Task A_second_writer_of_the_same_bytes_revokes_the_declaration_so_its_own_reference_cannot_be_orphaned()
    {
        // The dedup hazard: content addressing hands the SAME id to a later producer whose references the oracle
        // cannot enumerate. That later write must disarm the declaration rather than inherit it.
        var world = await SeedWorldAsync();
        const string content = "bytes two different producers both write";
        var artifactId = await DeclareAsync(world, content);
        (await DeclarationAsync(artifactId))!.State.ShouldBe(ArtifactRetentionState.Declared);

        using var scope = _fixture.BeginScope();
        var deduped = await scope.Resolve<IArtifactStore>().PutAsync(world.TeamId, System.Text.Encoding.UTF8.GetBytes(content), "text/plain", CancellationToken.None);
        deduped.ShouldBe(artifactId, "the content-addressed store must still dedup");

        var declaration = (await DeclarationAsync(artifactId)).ShouldNotBeNull();
        declaration.State.ShouldBe(ArtifactRetentionState.Revoked);
        declaration.LastErrorCode.ShouldBe("declaration-revoked-by-later-writer");

        await AgeDeclarationAsync(artifactId, TimeSpan.FromDays(30));
        await SweepAsync();
        await SweepAsync();

        (await ArtifactExistsAsync(artifactId)).ShouldBeTrue("a revoked declaration is terminal, so no later sweep can collect the artifact");
    }

    [Fact]
    public async Task A_freshly_declared_artifact_is_not_even_claimed_before_its_age_floor()
    {
        // Retention has an age floor, so a producer whose reference write is still in flight cannot be raced. The
        // declaration is left completely untouched — not merely re-queued.
        var world = await SeedWorldAsync();
        var artifactId = await DeclareAsync(world, "bytes written moments ago");
        var before = (await DeclarationAsync(artifactId)).ShouldNotBeNull();

        await SweepAsync();

        var after = (await DeclarationAsync(artifactId)).ShouldNotBeNull();
        after.State.ShouldBe(ArtifactRetentionState.Declared);
        after.Revision.ShouldBe(before.Revision, "a row below its age floor must not even be claimed, so nothing bumps its revision");
        after.OwnerId.ShouldBeNull();
        (await ArtifactExistsAsync(artifactId)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_declaration_whose_class_the_running_policy_does_not_register_is_kept_forever()
    {
        var world = await SeedWorldAsync();
        var artifactId = await DeclareAsync(world, "bytes filed under a class this build does not know");
        await AgeDeclarationAsync(artifactId, TimeSpan.FromDays(30));
        await SetClassAsync(artifactId, "SomeClassARollbackRemoved");

        await SweepAsync();

        var declaration = (await DeclarationAsync(artifactId)).ShouldNotBeNull();
        declaration.LastErrorCode.ShouldBe("retention-class-unregistered");
        (await ArtifactExistsAsync(artifactId)).ShouldBeTrue("'I cannot tell' must never resolve to 'delete'");
    }

    [Fact]
    public async Task An_offloaded_artifact_on_the_local_backend_is_purged_with_its_bytes()
    {
        // The lane's positive control: bytes past the inline threshold went to the local blob backend and used to be
        // permanently unreapable. Both waits elapse, then the file and the row go together.
        var world = await SeedWorldAsync();
        var artifactId = await DeclareLocalOffloadedAsync(world, "orphaned oversize deliverable");
        var blob = await BlobPathAsync(artifactId);
        File.Exists(blob).ShouldBeTrue("the declaring write must have offloaded the bytes to the local backend");

        await AgeDeclarationAsync(artifactId, TimeSpan.FromDays(30));
        var first = await SweepAsync();

        first.Quarantined.ShouldBeGreaterThanOrEqualTo(1, "the first observation of an unreferenced offloaded artifact only starts the quarantine clock");
        File.Exists(blob).ShouldBeTrue("the sweep that first noticed the artifact must not touch its bytes");

        await AgeQuarantineAsync(artifactId, TimeSpan.FromDays(2));
        var second = await SweepAsync();

        second.Collected.ShouldBeGreaterThanOrEqualTo(1);
        File.Exists(blob).ShouldBeFalse("the offloaded bytes are what this lane exists to reclaim");
        (await ArtifactExistsAsync(artifactId)).ShouldBeFalse();
        (await DeclarationAsync(artifactId)).ShouldBeNull("the ledger row goes with its artifact through the cascade");
    }

    [Fact]
    public async Task An_offloaded_artifact_whose_blob_another_team_also_names_is_kept_and_says_so()
    {
        // The local blob path is content-addressed but NOT team-scoped: <root>/ab/cd/<sha> is one file however many
        // rows name it. A second namer the reaper cannot collect makes the bytes unpurgeable, and the row must stay.
        var world = await SeedWorldAsync();
        var neighbour = await SeedWorldAsync();
        const string content = "oversize bytes two tenants both produced";
        var artifactId = await DeclareLocalOffloadedAsync(world, content);
        var blob = await BlobPathAsync(artifactId);
        var neighbourId = await PutLocalOffloadedAsync(neighbour, content);

        neighbourId.ShouldNotBe(artifactId, "artifacts do not dedup across teams, so this is a genuine second namer of one file");
        (await BlobPathAsync(neighbourId)).ShouldBe(blob, "both rows must resolve to the same physical blob for this test to mean anything");

        await AgeDeclarationAsync(artifactId, TimeSpan.FromDays(30));
        await SweepAsync();
        await AgeQuarantineAsync(artifactId, TimeSpan.FromDays(2));
        await SweepAsync();

        var declaration = (await DeclarationAsync(artifactId)).ShouldNotBeNull();
        declaration.LastErrorCode.ShouldBe("artifact-blob-shared", "the kept row must say WHY, not merely survive");
        declaration.State.ShouldBe(ArtifactRetentionState.Indeterminate, "an undeclared second namer is an unknown, and unknown means keep");
        File.Exists(blob).ShouldBeTrue("deleting a shared blob would break the neighbour's artifact");
        (await ArtifactExistsAsync(artifactId)).ShouldBeTrue();
    }

    [Fact]
    public async Task The_sharing_probe_has_its_own_index_so_it_is_not_a_scan_of_the_whole_table()
    {
        // The probe cannot be team-scoped (that is the hazard it exists to see), so no pre-existing index on the table
        // serves it. Pinned because losing the index turns a per-candidate probe into a sequential scan of the
        // platform's largest artifact table, and nothing else in the suite would notice.
        using var scope = _fixture.BeginScope();
        var indexes = await scope.Resolve<CodeSpaceDbContext>().Database
            .SqlQueryRaw<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE tablename = 'workflow_artifact'").ToListAsync();

        indexes.ShouldContain(definition => definition.Contains("ix_workflow_artifact_storage_url") && definition.Contains("(storage_url)"));
    }

    [Fact]
    public async Task A_sweep_after_the_bytes_are_already_gone_finishes_the_row_instead_of_stalling()
    {
        // The crash state: the byte delete happened, the row delete did not. The next sweep must complete it, which is
        // only true if the backend reports an absent blob as success rather than as a failure.
        var world = await SeedWorldAsync();
        var artifactId = await DeclareLocalOffloadedAsync(world, "bytes a crashed sweep already removed");
        var blob = await BlobPathAsync(artifactId);
        await AgeDeclarationAsync(artifactId, TimeSpan.FromDays(30));
        await SweepAsync();
        await AgeQuarantineAsync(artifactId, TimeSpan.FromDays(2));

        File.Delete(blob);

        var resumed = await SweepAsync();

        resumed.Collected.ShouldBeGreaterThanOrEqualTo(1, "'already gone' is success — otherwise a crashed purge strands its row forever");
        (await ArtifactExistsAsync(artifactId)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_refused_byte_delete_leaves_the_bytes_the_row_and_a_retryable_declaration()
    {
        // Rule 12 tier: medium-mock. Real reaper, real database, real declaration — only the backend's DeleteAsync is
        // substituted, because provoking a genuine unlink failure means chmod'ing a directory of the shared fixture's
        // blob root, which is both platform-dependent and leaks if the test dies mid-way.
        var world = await SeedWorldAsync();
        var artifactId = await DeclareLocalOffloadedAsync(world, "bytes a broken backend will not release");
        var blob = await BlobPathAsync(artifactId);
        await AgeDeclarationAsync(artifactId, TimeSpan.FromDays(30));
        await SweepAsync();
        await AgeQuarantineAsync(artifactId, TimeSpan.FromDays(2));

        var summary = await SweepWithRefusedPurgeAsync();

        summary.Collected.ShouldBe(0, "a backend that will not remove the bytes must not let the row go either");
        File.Exists(blob).ShouldBeTrue();
        (await ArtifactExistsAsync(artifactId)).ShouldBeTrue("deleting the row here would strand bytes nothing remembers");

        var declaration = (await DeclarationAsync(artifactId)).ShouldNotBeNull();
        declaration.State.ShouldBe(ArtifactRetentionState.Quarantined, "the declaration stays LIVE so the next sweep retries");
        declaration.LastErrorCode.ShouldBe("artifact-blob-delete-refused");
        declaration.AttemptCount.ShouldBe(1, "a provider failure spends exactly one of the budgeted attempts");

        // And the retry is real: the same declaration collects once a working backend gets to it.
        await AgeQuarantineAsync(artifactId, TimeSpan.FromDays(2));
        var recovered = await SweepAsync();

        recovered.Collected.ShouldBeGreaterThanOrEqualTo(1, "the refusal must have been a pause, not a one-way door");
        File.Exists(blob).ShouldBeFalse();
    }

    [Fact]
    public async Task A_routed_artifact_is_left_declared_rather_than_settled_terminally()
    {
        // Routed bytes are refused by this lane, and the refusal must not be a ONE-WAY door: the declaration stays
        // live so a later lane that can purge them still finds the row. A guard, not a red-first case — today's claim
        // query already excludes the row, for a different reason.
        var world = await SeedWorldAsync();
        var artifactId = await DeclareRoutedAsync(world);
        var before = (await DeclarationAsync(artifactId)).ShouldNotBeNull();
        await AgeDeclarationAsync(artifactId, TimeSpan.FromDays(30));

        await SweepAsync();

        var after = (await DeclarationAsync(artifactId)).ShouldNotBeNull();
        after.State.ShouldBe(ArtifactRetentionState.Declared, "a routed row must not be claimed at all");
        after.Revision.ShouldBe(before.Revision, "an unclaimed row's revision is untouched, which is what keeps a future lane able to reach it");
        (await ArtifactExistsAsync(artifactId)).ShouldBeTrue();
    }

    [Fact]
    public async Task Two_concurrent_sweeps_collect_one_artifact_at_most_once_and_neither_fails()
    {
        var world = await SeedWorldAsync();
        var artifactId = await DeclareAsync(world, "bytes two reapers race for");
        await AgeDeclarationAsync(artifactId, TimeSpan.FromDays(30));
        await SweepAsync();
        await AgeQuarantineAsync(artifactId, TimeSpan.FromDays(2));

        var summaries = await Task.WhenAll(SweepAsync(), SweepAsync());

        summaries.Sum(summary => summary.Collected).ShouldBeLessThanOrEqualTo(1, "the row lock plus the team-scoped DELETE make collection exactly-once");
        (await ArtifactExistsAsync(artifactId)).ShouldBeFalse();
        (await DeclarationAsync(artifactId)).ShouldBeNull();
    }

    [Fact]
    public async Task An_empty_artifact_id_is_refused_rather_than_answered_as_unreferenced()
    {
        // Guid.Empty matches no row, so every probe would honestly return "no reference" — an answer about nothing.
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var verdict = await scope.Resolve<IArtifactReferenceOracle>().ClassifyAsync(db, Guid.Empty, CancellationToken.None);

        verdict.ShouldBe(ArtifactReferenceVerdict.Indeterminate);
    }

    [Fact]
    public async Task A_delete_that_did_not_ask_for_the_purge_permission_is_rejected_by_the_table_itself()
    {
        // The reaper's SET LOCAL is load-bearing: without it the trigger from migration 0016 refuses the DELETE, so no
        // path other than a deliberate purge can remove an artifact row.
        var world = await SeedWorldAsync();
        var artifactId = await DeclareAsync(world, "bytes a careless DELETE tries to remove");

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var rejected = await Should.ThrowAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM workflow_artifact WHERE id = {artifactId}"));

        rejected.Message.ShouldContain("workflow_artifact is immutable");
        (await ArtifactExistsAsync(artifactId)).ShouldBeTrue();
    }

    // ─── World + helpers ─────────────────────────────────────────────────────

    private async Task<ArtifactRetentionSweepSummary> SweepAsync()
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<IArtifactRetentionReaper>().SweepAsync(CancellationToken.None);
    }

    /// <summary>The same sweep, driven by a reaper whose blob backend refuses every removal. Everything else — the oracle, the database, the policy — is production.</summary>
    private async Task<ArtifactRetentionSweepSummary> SweepWithRefusedPurgeAsync()
    {
        using var scope = _fixture.BeginScope();
        var reaper = new ArtifactRetentionReaper(scope.Resolve<DbContextOptions<CodeSpaceDbContext>>(), scope.Resolve<IArtifactReferenceOracle>(),
            new RefusingPurgeBackend(scope.Resolve<IArtifactBlobBackend>()), NullLogger<ArtifactRetentionReaper>.Instance);

        return await reaper.SweepAsync(CancellationToken.None);
    }

    /// <summary>The real backend for every operation except removal, which always refuses — the shape of a provider outage or a read-only mount.</summary>
    private sealed class RefusingPurgeBackend : IArtifactBlobBackend, IArtifactBlobPurge
    {
        private readonly IArtifactBlobBackend _inner;

        public RefusingPurgeBackend(IArtifactBlobBackend inner) => _inner = inner;

        public Task<ArtifactBlobPurgeOutcome> DeleteAsync(string storageUrl, CancellationToken cancellationToken) => Task.FromResult(ArtifactBlobPurgeOutcome.Refused);
        public Task<string> WriteAsync(string sha256, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) => _inner.WriteAsync(sha256, bytes, cancellationToken);
        public Task<bool> ExistsAsync(string storageUrl, CancellationToken cancellationToken) => _inner.ExistsAsync(storageUrl, cancellationToken);
        public Task<byte[]> ReadAsync(string storageUrl, CancellationToken cancellationToken) => _inner.ReadAsync(storageUrl, cancellationToken);
        public Task<ArtifactBlobRange> ReadRangeAsync(string storageUrl, long offset, int length, CancellationToken cancellationToken) => _inner.ReadRangeAsync(storageUrl, offset, length, cancellationToken);
    }

    /// <summary>A declaring write through the production seam — the same call <c>ArtifactManifestStore</c> makes.</summary>
    private async Task<Guid> DeclareAsync(World world, string content)
    {
        using var scope = _fixture.BeginScope();
        var request = new ArtifactRetentionWriteRequest(world.TeamId, System.Text.Encoding.UTF8.GetBytes(content), "text/markdown",
            ArtifactRetentionClass.ArtifactManifestContent, "artifact_manifest", world.AgentRunId);

        var write = await scope.Resolve<IArtifactRetentionWriter>().PutDeclaredAsync(request, CancellationToken.None);

        write.Declared.ShouldBeTrue("an inline first write must mint the declaration this suite then exercises");

        return write.ArtifactId;
    }

    /// <summary>Bytes past the inline threshold, deterministic from <paramref name="content"/> so two teams can write the identical payload.</summary>
    private static byte[] OffloadedBytes(string content)
    {
        var unit = System.Text.Encoding.UTF8.GetBytes(content + "\n");
        var repeats = (ArtifactStoreConfig.InlineThresholdBytes / unit.Length) + 2;

        return Enumerable.Range(0, repeats).SelectMany(_ => unit).ToArray();
    }

    /// <summary>A declaring write whose bytes are OFFLOADED to the local blob backend — the shape this lane exists for.</summary>
    private async Task<Guid> DeclareLocalOffloadedAsync(World world, string content)
    {
        using var scope = _fixture.BeginScope();
        var request = new ArtifactRetentionWriteRequest(world.TeamId, OffloadedBytes(content), "application/octet-stream",
            ArtifactRetentionClass.ArtifactManifestContent, "artifact_manifest", world.AgentRunId);

        var write = await scope.Resolve<IArtifactRetentionWriter>().PutDeclaredAsync(request, CancellationToken.None);

        write.Declared.ShouldBeTrue("an offloaded write on the local backend must declare — its bytes now have a purge path");

        return write.ArtifactId;
    }

    /// <summary>A plain undeclared offloaded write — the second namer of one physical blob.</summary>
    private async Task<Guid> PutLocalOffloadedAsync(World world, string content)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<IArtifactStore>().PutAsync(world.TeamId, OffloadedBytes(content), "application/octet-stream", CancellationToken.None);
    }

    /// <summary>The local file the row's <c>storage_url</c> resolves to. Read from the row, so the test never needs to know the configured root.</summary>
    private async Task<string> BlobPathAsync(Guid artifactId)
    {
        using var scope = _fixture.BeginScope();
        var url = await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifact.AsNoTracking()
            .Where(row => row.Id == artifactId).Select(row => row.StorageUrl).SingleAsync();

        return new Uri(url.ShouldNotBeNull("the row must be offloaded for this helper to mean anything")).LocalPath;
    }

    /// <summary>A ROUTED artifact plus a hand-planted declaration — a shape the production seam refuses to mint, asserted separately here.</summary>
    private async Task<Guid> DeclareRoutedAsync(World world)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var objectId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var digest = System.Security.Cryptography.SHA256.HashData(OffloadedBytes($"routed {artifactId:N}"));
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO artifact_object (id, team_id, digest_algorithm, digest, size_bytes, created_date, created_by)
            VALUES ({objectId}, {world.TeamId}, 'Sha256', {digest}, 1024, clock_timestamp(), {world.ActorId})
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO workflow_artifact (id, team_id, sha256, content_type, size_bytes, cas_artifact_object_id, created_at)
            VALUES ({artifactId}, {world.TeamId}, {Convert.ToHexStringLower(digest)}, 'application/octet-stream', 1024, {objectId}, clock_timestamp())
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO workflow_artifact_retention (artifact_id, team_id, retention_class, holder_kind, holder_id, state, declared_at, next_sweep_at, revision, last_modified_at)
            VALUES ({artifactId}, {world.TeamId}, 'ArtifactManifestContent', 'artifact_manifest', {world.AgentRunId}, 'Declared', clock_timestamp(), clock_timestamp(), 1, clock_timestamp())
            """);

        return artifactId;
    }

    private async Task ReferenceFromManifestAsync(World world, Guid artifactId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.ArtifactManifest.Add(new ArtifactManifest
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, AgentRunId = world.AgentRunId, FenceEpoch = 1,
            Kind = ArtifactManifestKind.Document, LogicalPath = $"docs/{artifactId:N}.md", ContentArtifactId = artifactId,
            Sha256 = new string('a', 64), SizeBytes = 1, ContentType = "text/markdown",
            CreatedDate = now, CreatedBy = world.ActorId, LastModifiedDate = now, LastModifiedBy = world.ActorId,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>A reference from a site the declaring producer never writes — proves the oracle probes all of them, not just the declared holder.</summary>
    private async Task ReferenceFromAgentRunEventAsync(World world, Guid artifactId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO agent_run_event (id, agent_run_id, kind, text, data_artifact_id)
            VALUES ({Guid.NewGuid()}, {world.AgentRunId}, 'Info', 'offloaded event payload', {artifactId})
            """);
    }

    /// <summary>
    /// The reference shape <c>NodeOutputArtifacts</c> writes into the append-only ledger: an id buried inside
    /// <c>payload_json</c>, which no foreign key and no column probe can see.
    /// </summary>
    private async Task ReferenceFromRecordPayloadAsync(World world, Guid artifactId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var payload = $"{{\"outputs\":{{\"body\":{{\"$artifact_ref\":{{\"id\":\"{artifactId}\",\"size_bytes\":42,\"content_type\":\"application/json\"}}}}}}}}";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO workflow_run_record (id, run_id, record_type, payload_json, occurred_at)
            VALUES ({Guid.NewGuid()}, {world.WorkflowRunId}, 'node.completed', CAST({payload} AS jsonb), clock_timestamp())
            """);

        // Proves the planted reference really is invisible to the column oracle — otherwise this test would pass for
        // the wrong reason and tell us nothing about why the artifact survived.
        (await scope.Resolve<IArtifactReferenceOracle>().ClassifyAsync(db, artifactId, CancellationToken.None))
            .ShouldBe(ArtifactReferenceVerdict.Unreferenced, "a JSON-borne reference is exactly the kind the oracle cannot see, which is why such bytes are never declared");
    }

    /// <summary>Backdates the artifact AND its declaration's queue position so the age floor is behind us without waiting a week.</summary>
    private async Task AgeDeclarationAsync(Guid artifactId, TimeSpan age)
    {
        await BackdateArtifactAsync(artifactId, age);
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE workflow_artifact_retention
            SET declared_at = clock_timestamp() - {age}::interval, next_sweep_at = clock_timestamp() - {age}::interval, last_modified_at = clock_timestamp()
            WHERE artifact_id = {artifactId}
            """);
    }

    private async Task BackdateArtifactAsync(Guid artifactId, TimeSpan age)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        // workflow_artifact rejects UPDATE outright (migration 0016) and this suite must not weaken that trigger, so the
        // row is aged with the trigger disabled — inside ONE transaction, which is what actually scopes it. DISABLE
        // TRIGGER is a catalog change visible to EVERY session, not to this statement, and it is transactional: without
        // the transaction a throw between disable and enable would leave artifact immutability disarmed for the rest of
        // this shared fixture's run, and a concurrent class could mutate a row the schema promises is append-only.
        await using var transaction = await db.Database.BeginTransactionAsync();

        await db.Database.ExecuteSqlRawAsync("ALTER TABLE workflow_artifact DISABLE TRIGGER workflow_artifact_enforce_immutability");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE workflow_artifact SET created_at = clock_timestamp() - {age}::interval WHERE id = {artifactId}");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE workflow_artifact ENABLE TRIGGER workflow_artifact_enforce_immutability");

        await transaction.CommitAsync();
    }

    private async Task AgeQuarantineAsync(Guid artifactId, TimeSpan age)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE workflow_artifact_retention
            SET quarantined_at = clock_timestamp() - {age}::interval, next_sweep_at = clock_timestamp() - {age}::interval, last_modified_at = clock_timestamp()
            WHERE artifact_id = {artifactId} AND state = 'Quarantined'
            """);
    }

    private async Task SetClassAsync(Guid artifactId, string retentionClass)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE workflow_artifact_retention SET retention_class = {retentionClass}, last_modified_at = clock_timestamp() WHERE artifact_id = {artifactId}");
    }

    private async Task<bool> ArtifactExistsAsync(Guid artifactId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifact.AsNoTracking().AnyAsync(row => row.Id == artifactId);
    }

    private async Task<WorkflowArtifactRetention?> DeclarationAsync(Guid artifactId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifactRetention.AsNoTracking().SingleOrDefaultAsync(row => row.ArtifactId == artifactId);
    }

    private async Task<World> SeedWorldAsync()
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var agentRunId = Guid.NewGuid();
        var workflowRunId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"artifact-retention-{actorId:N}@test.local", Name = "Artifact Retention" });
        db.Team.Add(new Team { Id = teamId, Slug = $"artifact-retention-{teamId:N}", Name = "Artifact Retention", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        await db.SaveChangesAsync();
        db.AgentRun.Add(new AgentRun
        {
            Id = agentRunId, TeamId = teamId, Harness = "test-harness", Status = AgentRunStatus.Succeeded, TaskJson = "{}", FenceEpoch = 1,
            CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        });

        // A real workflow run, because workflow_run_record.run_id is a foreign key — the JSON-reference test needs a
        // genuine ledger row, not a fabricated one.
        var requestId = Guid.NewGuid();
        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, WorkflowId = null, SourceType = WorkflowRunSourceTypes.Snapshot,
            ActorType = "user", ActorId = actorId, NormalizedPayloadJson = "{}",
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = workflowRunId, WorkflowId = null, WorkflowVersion = null, TeamId = teamId, RunRequestId = requestId,
            SourceType = WorkflowRunSourceTypes.Snapshot, Status = WorkflowRunStatus.Success,
            ScopeRepositoryIds = [], ScopeProjectIds = [], CreatedDate = now, CreatedBy = actorId, LastModifiedBy = actorId,
        });
        await db.SaveChangesAsync();

        return new World(teamId, actorId, agentRunId, workflowRunId);
    }

    private sealed record World(Guid TeamId, Guid ActorId, Guid AgentRunId, Guid WorkflowRunId);
}
