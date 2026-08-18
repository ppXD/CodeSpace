using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.Core.Settings;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts;

/// <summary>
/// The MAIN artifact plane honouring the operator's configured storage. Drives the real <see cref="IArtifactStore"/>
/// against the real CAS runtime and the real local-rwx provider driver over a temp root, so an assertion about "the
/// bytes landed where Settings said" is an assertion about a file that is actually there.
///
/// <para>The two properties this suite exists to protect: a team with NO route for <c>workflow-artifact/v1</c> — the
/// shipped state of every existing team — is byte-identical to before, and a routed read resolves the profile
/// revision its own location was stamped with rather than whatever the route points at today.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ArtifactStoreRoutedDestinationFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly List<string> _roots = [];

    public ArtifactStoreRoutedDestinationFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_unrouted_team_keeps_the_local_backend_and_its_exact_storage_url_shape()
    {
        // The compatibility pin. Every deployed team is in this state, and this is the hot path for patches,
        // manifests and model-call bodies: a change in the storage_url shape strands every row that already has one.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var content = Encoding.UTF8.GetBytes(new string('u', 20_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);

        var artifactId = await PutAsync(teamId, content);

        var row = await RowAsync(artifactId);
        row.InlineBytes.ShouldBeNull();
        row.CasArtifactObjectId.ShouldBeNull("an unrouted team must not acquire a CAS pointer");
        row.StorageUrl.ShouldBe(LocalUrlFor(sha), "the local backend's file:// shape is what every pre-existing row records");
        File.Exists(new Uri(row.StorageUrl!).LocalPath).ShouldBeTrue();

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<CodeSpaceDbContext>().ArtifactObject.CountAsync(o => o.TeamId == teamId)).ShouldBe(0,
            "no route means no CAS traffic at all — not even a transfer intent");
        (await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, artifactId, CancellationToken.None))!.Bytes.ShouldBe(content);
    }

    [Fact]
    public async Task An_unrouted_team_still_decides_inline_versus_offload_by_size_alone()
    {
        // Routing changes WHERE an offloaded blob goes, never WHETHER it is offloaded.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var small = Encoding.UTF8.GetBytes("small enough to stay in the row");

        var row = await RowAsync(await PutAsync(teamId, small));

        row.InlineBytes.ShouldBe(small);
        row.StorageUrl.ShouldBeNull();
        row.CasArtifactObjectId.ShouldBeNull();
    }

    [Fact]
    public async Task A_routed_team_places_the_bytes_through_the_coordinator_and_stamps_the_exact_profile_revision()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = NewRoot();
        var profileId = await SeedProfileAsync(teamId, root);
        await SeedRouteAsync(teamId, profileId);
        var content = Encoding.UTF8.GetBytes(new string('r', 20_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);

        var artifactId = await PutAsync(teamId, content);

        var row = await RowAsync(artifactId);
        row.InlineBytes.ShouldBeNull();
        row.StorageUrl.ShouldBeNull("a routed row must not also claim a local locator");
        row.CasArtifactObjectId.ShouldNotBeNull();
        File.Exists(ObjectPath(root, sha)).ShouldBeTrue(
            "the operator configured this root; the bytes have to actually be under it");

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var location = await db.ArtifactLocation.SingleAsync(l => l.TeamId == teamId && l.ArtifactObjectId == row.CasArtifactObjectId);
        location.State.ShouldBe(ArtifactLocationState.Available);
        var revision = await db.StorageProfileRevision.SingleAsync(r => r.Id == location.StorageProfileRevisionId);
        revision.StorageProfileId.ShouldBe(profileId);
        revision.Revision.ShouldBe(1, "the location carries the exact revision the write was authorised against");

        (await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, artifactId, CancellationToken.None))!.Bytes.ShouldBe(content);
    }

    [Fact]
    public async Task A_read_resolves_the_location_it_recorded_even_after_the_route_is_repointed()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var originalRoot = NewRoot();
        var originalProfileId = await SeedProfileAsync(teamId, originalRoot);
        await SeedRouteAsync(teamId, originalProfileId);
        var content = Encoding.UTF8.GetBytes(new string('h', 30_000));
        var artifactId = await PutAsync(teamId, content);

        var replacementRoot = NewRoot();
        await RepointRouteAsync(teamId, await SeedProfileAsync(teamId, replacementRoot));

        ArtifactBytes? fetched;
        using (var scope = _fixture.BeginScope())
            fetched = await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, artifactId, CancellationToken.None);

        fetched!.Bytes.ShouldBe(content, "the read must follow the artifact's own recorded location, not current policy");
        Directory.Exists(Path.Combine(replacementRoot, "objects")).ShouldBeFalse(
            "if the read had consulted the CURRENT route it would have looked here — and these bytes were never written here");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_destination_that_cannot_take_bytes_fails_the_write_closed_instead_of_using_local_disk(bool disableRoute)
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = NewRoot();
        var profileId = await SeedProfileAsync(teamId, root);
        await SeedRouteAsync(teamId, profileId);
        if (disableRoute) await SetRouteStateAsync(teamId, StorageRouteState.Disabled);
        else await SetProfileStateAsync(teamId, profileId, StorageProfileState.Disabled);
        var content = Encoding.UTF8.GetBytes(new string('f', 20_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);

        var exception = await Should.ThrowAsync<ArtifactStorageDestinationUnavailableException>(() => PutAsync(teamId, content));

        exception.RoutingProblem.ShouldBe(disableRoute
            ? WorkflowArtifactDestinationProblem.RouteNotActive
            : WorkflowArtifactDestinationProblem.ProfileNotActive);
        File.Exists(new Uri(LocalUrlFor(sha)).LocalPath).ShouldBeFalse(
            "a silent fallback to local disk is exactly the dishonesty this plane exists to remove");

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowArtifact.CountAsync(a => a.TeamId == teamId)).ShouldBe(0, "no row may claim bytes that were never placed");
        (await db.ArtifactTransferIntent.CountAsync(i => i.TeamId == teamId)).ShouldBe(0, "routing refuses before any provider effect is minted");
    }

    [Fact]
    public async Task Existing_local_artifacts_stay_readable_after_the_team_adopts_a_route()
    {
        // A read must never require a route to exist, and must never be re-pointed by one appearing later.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var content = Encoding.UTF8.GetBytes(new string('l', 20_000));
        var artifactId = await PutAsync(teamId, content);

        await SeedRouteAsync(teamId, await SeedProfileAsync(teamId, NewRoot()));

        using var scope = _fixture.BeginScope();
        var fetched = await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, artifactId, CancellationToken.None);

        fetched!.Bytes.ShouldBe(content);
        (await RowAsync(artifactId)).StorageUrl.ShouldNotBeNull("the pre-route row keeps its local locator forever");
    }

    [Fact]
    public async Task Routed_dedup_returns_the_same_id_and_never_restores_the_object_onto_local_disk()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = NewRoot();
        await SeedRouteAsync(teamId, await SeedProfileAsync(teamId, root));
        var content = Encoding.UTF8.GetBytes(new string('d', 20_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);
        var first = await PutAsync(teamId, content);

        var second = await PutAsync(teamId, content);

        second.ShouldBe(first, "(team, sha) dedup is unchanged by the destination");
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowArtifact.CountAsync(a => a.TeamId == teamId)).ShouldBe(1);
            (await db.ArtifactObject.CountAsync(o => o.TeamId == teamId)).ShouldBe(1);
            (await db.ArtifactTransferIntent.CountAsync(i => i.TeamId == teamId)).ShouldBe(1, "the dedup hit short-circuits before any second transfer");
        }

        // The local backend's self-healing restore exists because it has no location ledger. A routed row has no
        // storage_url, so it must never trigger — healing routed bytes onto local disk would be the silent fallback.
        File.Delete(ObjectPath(root, sha));

        (await PutAsync(teamId, content)).ShouldBe(first);
        File.Exists(new Uri(LocalUrlFor(sha)).LocalPath).ShouldBeFalse();

        using var readScope = _fixture.BeginScope();
        var failure = await Should.ThrowAsync<ArtifactContentUnavailableException>(
            () => readScope.Resolve<IArtifactStore>().GetBytesAsync(teamId, first, CancellationToken.None));
        failure.Kind.ShouldBe(ArtifactContentUnavailableKind.PhysicalObjectMissing,
            "a missing routed object is reported as a typed storage fact, never as empty content");
    }

    [Fact]
    public async Task A_bounded_range_read_works_through_the_routed_destination()
    {
        // The model-call viewer reads bodies this way; a routed team must not lose it.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedRouteAsync(teamId, await SeedProfileAsync(teamId, NewRoot()));
        var content = Encoding.UTF8.GetBytes(new string('a', 10_000) + "NEEDLE" + new string('b', 10_000));
        var artifactId = await PutAsync(teamId, content);

        using var scope = _fixture.BeginScope();
        var range = await scope.Resolve<IArtifactRangeReader>().ReadRangeAsync(teamId, artifactId, 10_000, 6, CancellationToken.None);

        range.State.ShouldBe(ArtifactRangeReadState.Available);
        Encoding.UTF8.GetString(range.Bytes!).ShouldBe("NEEDLE");
        range.TotalLength.ShouldBe(content.LongLength);
    }

    [Fact]
    public async Task Two_concurrent_writes_of_identical_bytes_both_return_the_same_id()
    {
        // The intent's idempotency key is the CONTENT, so overlapping writers of the same payload — the normal case
        // for a fan-out whose branches emit a shared prompt, file body or transcript prefix — contend for one lease.
        // The loser must not lose its contribution: PutAsync is idempotent and ALWAYS returns a valid id.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = NewRoot();
        await SeedRouteAsync(teamId, await SeedProfileAsync(teamId, root));
        var content = Encoding.UTF8.GetBytes(new string('c', 40_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);

        var ids = await Task.WhenAll(PutAsync(teamId, content), PutAsync(teamId, content));

        ids[1].ShouldBe(ids[0], "the writer that lost the transfer lease must still be handed the winner's artifact");
        File.Exists(ObjectPath(root, sha)).ShouldBeTrue();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowArtifact.CountAsync(a => a.TeamId == teamId)).ShouldBe(1);
        (await db.ArtifactObject.CountAsync(o => o.TeamId == teamId)).ShouldBe(1, "identical content is one object, however many writers raced for it");
        (await db.ArtifactTransferIntent.CountAsync(i => i.TeamId == teamId)).ShouldBe(1, "the content-keyed intent is shared, not duplicated");
        (await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, ids[0], CancellationToken.None))!.Bytes.ShouldBe(content);
    }

    [Fact]
    public async Task A_write_whose_content_is_already_claimed_waits_for_that_transfer_and_finishes_it()
    {
        // The DETERMINISTIC half of the race above. Another worker holds a live lease on the intent for these exact
        // bytes at the moment PutAsync runs, so ClaimAsync's `worker_lease_expires_at <= clock_timestamp()` predicate
        // refuses this caller and the coordinator returns Deferred(TransferInProgress) on the FIRST attempt. Treating
        // that as a terminal failure would discard a contribution for content the platform is mid-way through storing.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = NewRoot();
        var profileId = await SeedProfileAsync(teamId, root);
        await SeedRouteAsync(teamId, profileId);
        var content = Encoding.UTF8.GetBytes(new string('w', 20_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);
        var intentId = await SeedLeasedIntentAsync(teamId, profileId, content, TimeSpan.FromSeconds(3));

        var artifactId = await PutAsync(teamId, content);

        (await RowAsync(artifactId)).CasArtifactObjectId.ShouldNotBeNull("the deferred transfer must be completed, not abandoned");
        File.Exists(ObjectPath(root, sha)).ShouldBeTrue();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var intent = await db.ArtifactTransferIntent.AsNoTracking().SingleAsync(i => i.TeamId == teamId);
        intent.Id.ShouldBe(intentId, "the waiter claimed the intent it was deferred behind rather than minting a second one");
        intent.State.ShouldBe(ArtifactTransferState.Committed);
        intent.WorkerFenceEpoch.ShouldBe(2, "the abandoned lease lapsed and this caller took the next fence");
    }

    [Fact]
    public async Task A_repaired_destination_lets_the_same_bytes_through_after_a_terminal_failure()
    {
        // The poisoned-content property. A non-retryable problem records Failed against the CONTENT, and the database
        // guard offers no route back out of Failed (0131: 'terminal rows cannot be claimed', and a plain transition
        // needs an unexpired lease a terminal row may not hold). Repairing what broke the transfer does not bump
        // storage_profile_revision either — here the repair is entirely outside the database — so unless a repaired
        // attempt mints a FRESH intent, these exact bytes are unstorable for this team forever.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = NewRoot();
        var profileId = await SeedProfileAsync(teamId, root);
        await SeedRouteAsync(teamId, profileId);
        var content = Encoding.UTF8.GetBytes(new string('x', 20_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);
        PlaceForeignObject(root, sha);

        await Should.ThrowAsync<ArtifactStorageDestinationUnavailableException>(() => PutAsync(teamId, content));
        await AssertIntentsAsync(teamId, [(ArtifactStore.IdempotencyKeyFor(sha, 0), ArtifactTransferState.Failed)]);

        File.Delete(ObjectPath(root, sha));
        var artifactId = await PutAsync(teamId, content);

        (await RowAsync(artifactId)).CasArtifactObjectId.ShouldNotBeNull(
            "a repaired destination must be able to store content an earlier misconfiguration failed on");
        (await File.ReadAllBytesAsync(ObjectPath(root, sha))).ShouldBe(content);
        await AssertIntentsAsync(teamId, [
            (ArtifactStore.IdempotencyKeyFor(sha, 0), ArtifactTransferState.Failed),
            (ArtifactStore.IdempotencyKeyFor(sha, 1), ArtifactTransferState.Committed),
        ]);
    }

    [Fact]
    public async Task A_still_broken_destination_keeps_failing_closed_one_fresh_intent_at_a_time()
    {
        // The other half: minting a fresh intent must not turn a genuine refusal into a success, and must not replay
        // the burned key forever either. Each call steps exactly one generation and records its own terminal outcome.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = NewRoot();
        var profileId = await SeedProfileAsync(teamId, root);
        await SeedRouteAsync(teamId, profileId);
        var content = Encoding.UTF8.GetBytes(new string('b', 20_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);
        PlaceForeignObject(root, sha);

        var first = await Should.ThrowAsync<ArtifactStorageDestinationUnavailableException>(() => PutAsync(teamId, content));
        var second = await Should.ThrowAsync<ArtifactStorageDestinationUnavailableException>(() => PutAsync(teamId, content));

        first.RoutingProblem.ShouldBeNull("routing admitted this write; the transfer is what refused it");
        first.TransferProblem.ShouldBe(ArtifactCasProblemCode.TargetCorrupt);
        second.TransferProblem.ShouldBe(ArtifactCasProblemCode.TargetCorrupt,
            "the second call ran a REAL attempt of its own rather than replaying the first one's stored verdict");
        await AssertIntentsAsync(teamId, [
            (ArtifactStore.IdempotencyKeyFor(sha, 0), ArtifactTransferState.Failed),
            (ArtifactStore.IdempotencyKeyFor(sha, 1), ArtifactTransferState.Failed),
        ]);

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifact.CountAsync(a => a.TeamId == teamId)).ShouldBe(0,
            "no row may claim bytes that were never placed");
    }

    [Fact]
    public async Task A_routed_team_never_gains_new_local_disk_bytes_when_a_pre_route_blob_is_missing()
    {
        // The dedup self-heal restores a missing local blob. For a team that has since adopted a route, that restore
        // would write NEW bytes to local disk — the same silent fallback the write path refuses — and would make the
        // outcome depend on whether the content happens to already have a row.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var content = Encoding.UTF8.GetBytes(new string('m', 20_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);
        var artifactId = await PutAsync(teamId, content);
        var localPath = new Uri(LocalUrlFor(sha)).LocalPath;
        File.Exists(localPath).ShouldBeTrue("precondition: the pre-route write landed on local disk");

        await SeedRouteAsync(teamId, await SeedProfileAsync(teamId, NewRoot()));
        File.Delete(localPath);

        (await PutAsync(teamId, content)).ShouldBe(artifactId, "dedup still answers for content the store already knows");
        File.Exists(localPath).ShouldBeFalse(
            "a routed team must not gain new local-disk bytes; the dead reference surfaces as a typed read failure instead");
    }

    [Fact]
    public async Task A_bounded_read_serves_a_window_far_into_a_routed_object()
    {
        // The model-call viewer pages by offset. The routed range read asks the PROVIDER for the window — reading the
        // whole object and slicing it would cost O(offset) provider bytes on every page of every large transcript.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedRouteAsync(teamId, await SeedProfileAsync(teamId, NewRoot()));
        var content = Encoding.UTF8.GetBytes(new string('a', 400_000) + "NEEDLE" + new string('b', 400_000));
        var artifactId = await PutAsync(teamId, content);

        using var scope = _fixture.BeginScope();
        var range = await scope.Resolve<IArtifactRangeReader>().ReadRangeAsync(teamId, artifactId, 400_000, 6, CancellationToken.None);

        range.State.ShouldBe(ArtifactRangeReadState.Available);
        Encoding.UTF8.GetString(range.Bytes!).ShouldBe("NEEDLE");
        range.TotalLength.ShouldBe(content.LongLength);
    }

    [Fact]
    public async Task A_corrupt_routed_object_reports_a_bounded_read_failure_instead_of_throwing()
    {
        // IArtifactRangeReader's contract is that it never throws — it classifies. The routed path has to hold that
        // for a truncated or foreign-written object too, not only for a healthy one.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = NewRoot();
        await SeedRouteAsync(teamId, await SeedProfileAsync(teamId, root));
        var content = Encoding.UTF8.GetBytes(new string('t', 30_000));
        var sha = ArtifactStore.ComputeSha256Hex(content);
        var artifactId = await PutAsync(teamId, content);

        await File.WriteAllBytesAsync(ObjectPath(root, sha), content[..1_000]);

        using var scope = _fixture.BeginScope();
        var range = await scope.Resolve<IArtifactRangeReader>().ReadRangeAsync(teamId, artifactId, 0, 512, CancellationToken.None);

        range.State.ShouldBe(ArtifactRangeReadState.IntegrityFailure,
            "a truncated provider object is a typed storage fact, never an exception out of a bounded read");
    }

    private async Task<Guid> PutAsync(Guid teamId, byte[] content)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IArtifactStore>().PutAsync(teamId, content, "text/plain", CancellationToken.None);
    }

    /// <summary>
    /// Stages "another worker is mid-transfer of these exact bytes" the ONLY way the schema allows. The BEFORE INSERT
    /// arm of <c>artifact_cas_transfer_guard</c> admits a first revision of 1 in state Intended and unclaimed, and
    /// nothing else; the claim is then a real fence advance (NULL to 1) with an expiring lease, which is exactly what
    /// <c>ClaimAsync</c> writes. Inserting a Failed or already-claimed row directly is rejected by the trigger, so a
    /// test that tries it never reaches its assertions.
    /// </summary>
    private async Task<Guid> SeedLeasedIntentAsync(Guid teamId, Guid profileId, byte[] content, TimeSpan leaseFor)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var revision = await db.StorageProfileRevision.AsNoTracking().SingleAsync(r => r.TeamId == teamId && r.StorageProfileId == profileId && r.Revision == 1);
        var sha = ArtifactStore.ComputeSha256Hex(content);
        var objectKey = ObjectKeyFor(sha);
        var now = DateTimeOffset.UtcNow;
        var intentId = Guid.NewGuid();

        db.ArtifactTransferIntent.Add(new ArtifactTransferIntent
        {
            Id = intentId, TeamId = teamId, StorageProfileRevisionId = revision.Id,
            IdempotencyKey = ArtifactStore.IdempotencyKeyFor(sha, 0),
            ExpectedDigestAlgorithm = ArtifactDigestAlgorithm.Sha256, ExpectedDigest = Convert.FromHexString(sha),
            ExpectedSizeBytes = content.Length, TargetLocator = objectKey, TargetObjectKey = objectKey,
            State = ArtifactTransferState.Intended, Revision = 1, RetryCount = 0,
            CreatedDate = now, CreatedBy = SystemUsers.SeederId, LastModifiedDate = now, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();

        var leaseMilliseconds = (long)leaseFor.TotalMilliseconds;
        var claimed = await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE artifact_transfer_intent
            SET worker_fence_epoch = 1,
                worker_lease_expires_at = clock_timestamp() + ({{leaseMilliseconds}} * INTERVAL '1 millisecond'),
                revision = revision + 1,
                last_modified_date = clock_timestamp()
            WHERE team_id = {{teamId}} AND id = {{intentId}}
            """);
        claimed.ShouldBe(1, "the staged claim must be a legal fence advance; the guard rejects anything else");
        return intentId;
    }

    /// <summary>
    /// Puts a foreign object of the wrong length at the target key, which is the cheapest reachable NON-retryable
    /// transfer failure for the local-rwx provider: <c>HeadCanMatch</c> compares the head's length against the
    /// expected size, so the coordinator raises <c>TargetCorrupt</c> — <c>Problem(code)</c> defaults to
    /// non-retryable — and <c>HandleProblemAsync</c> drives the intent to Failed. The credential-shaped problems the
    /// blocker names (CredentialInvalid, CredentialUnavailable) reach the SAME classification, but this driver has no
    /// credentials to break. Deleting this file is a repair entirely outside the database, which is the point: it
    /// leaves storage_profile_revision untouched.
    /// </summary>
    private static void PlaceForeignObject(string root, string sha)
    {
        var path = ObjectPath(root, sha);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("not the bytes this key promises"));
    }

    /// <summary>Every intent this team owns, in key order, as (idempotency key, state) — so a test names the exact generations it expects and nothing else.</summary>
    private async Task AssertIntentsAsync(Guid teamId, (string Key, ArtifactTransferState State)[] expected)
    {
        using var scope = _fixture.BeginScope();
        var intents = await scope.Resolve<CodeSpaceDbContext>().ArtifactTransferIntent.AsNoTracking()
            .Where(i => i.TeamId == teamId).OrderBy(i => i.IdempotencyKey)
            .Select(i => new { i.IdempotencyKey, i.State }).ToListAsync();

        intents.Select(i => (i.IdempotencyKey, i.State)).ShouldBe(expected);
    }

    /// <summary>The object key the routed write targets. Content-addressed and generation-INVARIANT, so every attempt at the same bytes dedups on the provider.</summary>
    private static string ObjectKeyFor(string sha) => $"workflow-artifacts/{sha[..2]}/{sha.Substring(2, 2)}/{sha}";

    /// <summary>
    /// Where the local-rwx driver actually puts an object: it namespaces every key under an <c>objects/</c> directory
    /// beneath the configured root. Computed once here so a change to that layout fails in one place.
    /// </summary>
    private static string ObjectPath(string root, string sha) =>
        Path.Combine(root, "objects", Path.Combine(ObjectKeyFor(sha).Split('/')));

    private async Task<WorkflowArtifact> RowAsync(Guid artifactId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifact.AsNoTracking().SingleAsync(a => a.Id == artifactId);
    }

    /// <summary>Computed through the production resolver so a change to where local artifacts live fails here, not silently.</summary>
    private static string LocalUrlFor(string sha) =>
        new Uri(Path.Combine(DurableRoots.ArtifactStore(RuntimeSettings.Current.ArtifactStoreDirectory), sha[..2], sha.Substring(2, 2), sha)).AbsoluteUri;

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codespace-artifact-route-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    private async Task<Guid> SeedProfileAsync(Guid teamId, string rootPath)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { rootPath }));
        var canonical = StorageProfileRules.CanonicalJson(document.RootElement);
        using var canonicalDocument = JsonDocument.Parse(canonical);

        var profile = new StorageProfile
        {
            Id = Guid.NewGuid(), TeamId = teamId, StableName = $"routed-{Guid.NewGuid():N}", CurrentRevision = 1,
            State = StorageProfileState.Active, CreatedDate = now, CreatedBy = SystemUsers.SeederId,
            LastModifiedDate = now, LastModifiedBy = SystemUsers.SeederId,
        };
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageProfileId = profile.Id, Revision = 1,
            ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, NonSecretConfigJson = canonical,
            NamespaceFingerprint = StorageProfileRules.NamespaceFingerprint(LocalRwxArtifactStorageDriverFactory.TypeKey, canonicalDocument.RootElement),
            CreatedDate = now, CreatedBy = SystemUsers.SeederId,
        });
        db.StorageProfile.Add(profile);
        await db.SaveChangesAsync();
        return profile.Id;
    }

    private async Task SeedRouteAsync(Guid teamId, Guid profileId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;

        // The table's triggers require a route to be born Draft at revision 1 and reach Active by update.
        var route = new StorageRoute
        {
            Id = Guid.NewGuid(), TeamId = teamId, DataClassTypeKey = WorkflowArtifactDestinationResolver.DataClassTypeKey,
            CurrentRevision = 1, State = StorageRouteState.Draft, CreatedDate = now, CreatedBy = SystemUsers.SeederId,
            LastModifiedDate = now, LastModifiedBy = SystemUsers.SeederId,
        };
        route.Revisions.Add(new StorageRouteRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageRouteId = route.Id, Revision = 1, StorageProfileId = profileId,
            ProfileRevisionMode = StorageProfileRevisionMode.CurrentAtWrite, CreatedDate = now, CreatedBy = SystemUsers.SeederId,
        });
        db.StorageRoute.Add(route);
        await db.SaveChangesAsync();

        route.State = StorageRouteState.Active;
        await db.SaveChangesAsync();
    }

    private async Task RepointRouteAsync(Guid teamId, Guid profileId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var route = await db.StorageRoute.SingleAsync(r => r.TeamId == teamId && r.DataClassTypeKey == WorkflowArtifactDestinationResolver.DataClassTypeKey);

        db.StorageRouteRevision.Add(new StorageRouteRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageRouteId = route.Id, Revision = route.CurrentRevision + 1,
            StorageProfileId = profileId, ProfileRevisionMode = StorageProfileRevisionMode.CurrentAtWrite,
            CreatedDate = DateTimeOffset.UtcNow, CreatedBy = SystemUsers.SeederId,
        });
        route.CurrentRevision += 1;
        route.LastModifiedDate = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task SetRouteStateAsync(Guid teamId, StorageRouteState state)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().StorageRoute
            .Where(r => r.TeamId == teamId && r.DataClassTypeKey == WorkflowArtifactDestinationResolver.DataClassTypeKey)
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.State, state));
    }

    private async Task SetProfileStateAsync(Guid teamId, Guid profileId, StorageProfileState state)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().StorageProfile
            .Where(p => p.TeamId == teamId && p.Id == profileId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.State, state));
    }

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
