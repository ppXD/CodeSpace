using System.Data.Common;
using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Backends;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local.Legacy;
using CodeSpace.Core.Services.Workflows.Artifacts.Retention;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Artifacts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts;

/// <summary>
/// Phase two against real Postgres, the real immutable workflow-artifact row, and the real pre-CAS filesystem layout.
/// The location is a sidecar observation only: neither the row nor its read and retention lanes are cut over.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class LegacyPlacementAdoptionTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly List<string> _roots = [];

    public LegacyPlacementAdoptionTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task One_confirmed_destination_mints_available_missing_and_corrupt_sidecars_without_relinking_any_legacy_row()
    {
        var world = await SeedAsync(
            Candidate("available legacy bytes"),
            Candidate("missing legacy bytes", LegacyShape.Missing),
            Candidate("corrupt legacy bytes", LegacyShape.SameLengthCorrupt));

        var evidence = await AdoptAsync(world);
        evidence.Phase.ShouldBe(LegacyPlacementAdoptionPhaseValue.Evidence);
        evidence.AdoptionAdmissible.ShouldBeTrue();
        evidence.DestinationConfirmed.ShouldBeTrue();
        evidence.Available.ShouldBe(0, "the evidence pass is structurally write-free");
        (await CountsAsync(world)).Locations.ShouldBe(0);

        var minted = await AdoptAsync(world, evidence.NextCursor);

        minted.Phase.ShouldBe(LegacyPlacementAdoptionPhaseValue.Minting);
        minted.Available.ShouldBe(1);
        minted.Missing.ShouldBe(1, "Missing is admissible only because another resolved object re-confirmed this exact destination");
        minted.Corrupt.ShouldBe(1, "HEAD length alone cannot hide same-length foreign bytes from the full-stream re-hash");
        minted.Retryable.ShouldBe(0);
        var counts = await CountsAsync(world);
        counts.Objects.ShouldBe(3);
        counts.Locations.ShouldBe(3);
        counts.Events.ShouldBe(3, "each first observation commits with its matching append-only event");

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var eventSources = (await db.ArtifactLocationEvent.AsNoTracking().Where(value => value.TeamId == world.TeamId)
                .Select(value => value.DetailsJson).ToListAsync())
            .Select(SourceId)
            .ToHashSet();
        eventSources.SetEquals(world.Artifacts.Select(value => value.Id)).ShouldBeTrue(
            "the append-only event must retain which immutable source authorized the sidecar after retention deletes that source");
        var rows = await db.WorkflowArtifact.AsNoTracking().Where(value => value.TeamId == world.TeamId).ToListAsync();
        rows.ShouldAllBe(value => value.StorageUrl != null && value.CasArtifactObjectId == null,
            "phase two is a sidecar adoption, not an UPDATE of the immutable row or a reader cutover");

        var available = world.Artifacts.Single(value => value.Shape == LegacyShape.Available);
        var reader = new ArtifactStore(db, new LocalFileArtifactBlobBackend(world.RootPath), scope.Resolve<IWorkflowArtifactDestinationResolver>(),
            scope.Resolve<ArtifactRoutedPlane>(), scope.Resolve<TimeProvider>());
        var read = await reader.GetBytesAsync(world.TeamId, available.Id, CancellationToken.None);
        read.ShouldNotBeNull().Bytes.ShouldBe(available.ExpectedBytes,
            "an adopted legacy row still reads its recorded storage_url; the new ArtifactObject cannot redirect it");
    }

    [Fact]
    public async Task Retention_keeps_the_immutable_local_lane_and_closes_the_sidecar_only_after_the_blob_is_reclaimed()
    {
        var isolated = new PostgresFixture();
        await isolated.InitializeAsync();
        try
        {
            using var scenario = new LegacyPlacementAdoptionTests(isolated);
            await scenario.RetentionClosesSidecarAsync();
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    private async Task RetentionClosesSidecarAsync()
    {
        var world = await SeedAsync();
        var retained = await DeclareRetainedOffloadAsync(world, "legacy sidecar must not redirect retention");
        await RepointAsync(world, retained.RootPath);

        var evidence = await AdoptAsync(world);
        evidence.AdoptionAdmissible.ShouldBeTrue();
        var minted = await AdoptAsync(world, evidence.NextCursor);
        minted.Available.ShouldBe(1);

        var placement = await SidecarAsync(world, retained.ArtifactId);
        var originalUrl = retained.StorageUrl;
        await AgeDeclarationAsync(retained.ArtifactId, TimeSpan.FromDays(30));

        using (var scope = _fixture.BeginScope())
        {
            var blobs = new CountingBlobBackend(scope.Resolve<IArtifactBlobBackend>(), originalUrl);
            var routed = new TrackingCasPurgeCoordinator(scope.Resolve<IArtifactCasPurgeCoordinator>(), placement.ArtifactObjectId);
            var reaper = new ArtifactRetentionReaper(scope.Resolve<DbContextOptions<CodeSpaceDbContext>>(), scope.Resolve<IArtifactReferenceOracle>(),
                blobs, routed, NullLogger<ArtifactRetentionReaper>.Instance);

            await SweepUntilAsync(reaper, retained.ArtifactId, ArtifactRetentionState.Quarantined);
            await AgeQuarantineAsync(retained.ArtifactId, TimeSpan.FromDays(2));
            await SweepUntilCollectedAsync(reaper, retained.ArtifactId);

            blobs.TargetDeleteCalls.ShouldBe(1, "the immutable StorageUrl selects exactly one local-backend removal");
            routed.TargetCalls.ShouldBe(0, "a sidecar ArtifactObject must not turn a legacy WorkflowArtifact into a routed artifact");
        }

        File.Exists(new Uri(originalUrl).LocalPath).ShouldBeFalse();
        await AssertCollectedLegacyRowAsync(retained.ArtifactId, placement);

        using (var censusScope = _fixture.BeginScope())
        {
            var census = await censusScope.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking().Select(value => value.Id).ToListAsync();
            census.ShouldBe([placement.LocationId], "the isolated database makes one bounded verifier pass a deterministic census, not a deployment-wide polling race");
        }

        using (var verificationScope = _fixture.BeginScope())
        {
            var verified = await verificationScope.Resolve<IArtifactLocationVerifier>().VerifyStaleAsync(1, CancellationToken.None);
            verified.Checked.ShouldBe(1, "the only location in the isolated verifier population must be the sidecar under test");
            verified.Missing.ShouldBe(1, "the pass must positively prove it examined and demoted the reclaimed sidecar");
        }

        using (var observedScope = _fixture.BeginScope())
        {
            var db = observedScope.Resolve<CodeSpaceDbContext>();
            var missing = await db.ArtifactLocation.AsNoTracking().SingleAsync(value => value.Id == placement.LocationId);
            missing.State.ShouldBe(ArtifactLocationState.Missing,
                "the verifier, not local retention, is the first component allowed to translate the reclaimed blob into sidecar state");
            missing.Revision.ShouldBe(2);
            (await db.ArtifactLocationEvent.AsNoTracking().CountAsync(value => value.ArtifactLocationId == placement.LocationId)).ShouldBe(2);
        }

        using var closureScope = _fixture.BeginScope();
        var closure = await closureScope.Resolve<IProfileAbandonmentService>()
            .AbandonAsync(world.TeamId, world.ActorId, world.ProfileId, batchSize: 50, CancellationToken.None);
        closure.Abandoned.ShouldBe(1, "the live legacy root corroborates the now-missing object, so abandonment can close the sidecar");
        closure.Remaining.ShouldBe(0);
        var closedDb = closureScope.Resolve<CodeSpaceDbContext>();
        var closed = await closedDb.ArtifactLocation.AsNoTracking().SingleAsync(value => value.Id == placement.LocationId);
        closed.State.ShouldBe(ArtifactLocationState.Purged);
        closed.Revision.ShouldBe(4, "claim and conclusive abandonment each append one fenced transition after verification");
        (await closedDb.ArtifactLocationEvent.AsNoTracking().CountAsync(value => value.ArtifactLocationId == placement.LocationId)).ShouldBe(4);
    }

    [Fact]
    public async Task Neither_zero_resolved_keys_nor_zero_destination_confirmations_can_mint_a_single_row()
    {
        var displaced = await SeedAsync(Candidate("layout mismatch"));
        await RepointAsync(displaced, NewRoot("displaced"));

        var unresolved = await AdoptAsync(displaced);

        unresolved.Resolved.ShouldBe(0);
        unresolved.AdoptionAdmissible.ShouldBeFalse();
        unresolved.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.AdmissionEvidenceMissing);
        (await CountsAsync(displaced)).Locations.ShouldBe(0);

        var unmounted = await SeedAsync(Candidate("destination absent"));
        Directory.Delete(unmounted.RootPath, recursive: true);

        var unconfirmed = await AdoptAsync(unmounted);

        unconfirmed.Resolved.ShouldBe(1);
        unconfirmed.Confirmed.ShouldBe(0);
        unconfirmed.AdoptionAdmissible.ShouldBeFalse();
        unconfirmed.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.DestinationUnavailable,
            "a missing key beneath a vanished root is not evidence that the object is missing");
        unconfirmed.Retryable.ShouldBe(1);
        unconfirmed.NextCursor.ShouldNotBeNull("the only row in the snapshot must remain reachable after the mount returns");
        Directory.Exists(unmounted.RootPath).ShouldBeFalse("a read-only phase-two pass must never recreate a vanished mount");
        (await CountsAsync(unmounted)).Locations.ShouldBe(0);
    }

    [Fact]
    public async Task Concurrent_and_replayed_minting_converges_on_one_location_and_one_event()
    {
        var world = await SeedAsync(Candidate("concurrent adoption"));
        var evidence = await AdoptAsync(world);

        var results = await Task.WhenAll(AdoptAsync(world, evidence.NextCursor), AdoptAsync(world, evidence.NextCursor));

        results.Sum(value => value.Available).ShouldBe(1);
        results.Count(value => value.Refusal is LegacyPlacementAdoptionRefusalValue.ArcBusy
            or LegacyPlacementAdoptionRefusalValue.CursorSuperseded).ShouldBe(1,
            "one lease-fenced winner advances the arc; a loser must not replay stale provider observations");
        var afterRace = await CountsAsync(world);
        afterRace.Locations.ShouldBe(1);
        afterRace.Events.ShouldBe(1);

        var replay = await AdoptAsync(world, evidence.NextCursor);
        replay.ShouldBe(results.Single(value => value.Available == 1),
            "the terminal tombstone must replay the lost final response instead of opening a new population");
        (await CountsAsync(world)).Events.ShouldBe(1, "terminal replay appends no duplicate event");
    }

    [Fact]
    public async Task A_live_claim_fences_the_same_cursor_before_a_second_worker_can_repeat_provider_io()
    {
        var world = await SeedAsync(Candidate("one lease-fenced object"));
        var evidence = await AdoptAsync(world);
        var gate = new BlockingRead();
        var loserBroker = new CountingUnavailableBroker();
        LegacyPlacementAdoptionSummary? winner = null;
        using (var scope = DecoratingScope(lease => new BlockingReadDriver(lease, gate)))
        {
            Task<LegacyPlacementAdoptionSummary>? inFlight = null;
            try
            {
                inFlight = scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                    new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, evidence.NextCursor), CancellationToken.None);
                await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

                LegacyPlacementAdoptionSummary superseded;
                LegacyPlacementAdoptionSummary fenced;
                using (var loserScope = BrokerScope(loserBroker))
                {
                    superseded = await loserScope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                        new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, evidence.NextCursor), CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(10));
                    fenced = await loserScope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                        new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, superseded.NextCursor), CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(10));
                }
                superseded.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.CursorSuperseded,
                    "claim acquisition advances the durable revision before provider I/O, so the stale cursor cannot address that claim");
                fenced.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.ArcBusy,
                    "the server-current cursor must still be refused while the first worker owns its live claim");
                loserBroker.Calls.ShouldBe(0, "stale and busy workers are fenced before broker activation or any provider I/O");
                fenced.Available.ShouldBe(0);
                AssertSameLogicalPage(world, evidence.NextCursor!, fenced.NextCursor!,
                    "the busy answer must preserve the claimed logical page for response-driven clients");
                (await CountsAsync(world)).Locations.ShouldBe(0,
                    "the first worker is still blocked before commit and the fenced worker performed no duplicate commit");
            }
            finally
            {
                gate.Continue.TrySetResult();
                if (inFlight != null) winner = await inFlight.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        winner.ShouldNotBeNull().Available.ShouldBe(1);
        (await CountsAsync(world)).ShouldBe((1, 1, 1));
    }

    [Fact]
    public async Task A_retryable_object_keeps_the_same_page_cursor_until_that_object_is_observed()
    {
        var world = await SeedAsync(Candidate("witness bytes"), Candidate("temporarily locked bytes"));
        var evidence = await AdoptAsync(world);
        using var cursorScope = _fixture.BeginScope();
        var protector = cursorScope.Resolve<IDataProtectionProvider>().CreateProtector(LegacyPlacementAdoptionCursor.ProtectorPurpose);
        LegacyPlacementAdoptionCursor.TryDecode(evidence.NextCursor!, world.ProfileId, protector, out var cursor).ShouldBeTrue();
        var witness = await cursorScope.Resolve<CodeSpaceDbContext>().LegacyPlacementAdoptionArc.AsNoTracking()
            .Where(value => value.Id == cursor.ArcId).Select(value => value.WitnessWorkflowArtifactId).SingleAsync();
        var blocked = world.Artifacts.Single(value => value.Id != witness);

        LegacyPlacementAdoptionSummary first;
        await using (var exclusive = new FileStream(new Uri(blocked.StorageUrl).LocalPath, FileMode.Open, FileAccess.Read, FileShare.None))
            first = await AdoptAsync(world, evidence.NextCursor);

        first.Retryable.ShouldBe(1);
        (await CountsAsync(world)).ShouldBe((0, 0, 0),
            "one retryable row makes the whole page non-committable; a healthy sibling cannot mint ahead of the durable cursor");
        AssertSameLogicalPage(world, evidence.NextCursor!, first.NextCursor!,
            "an answer called retryable may rotate the lease revision but cannot advance out of the immutable population");

        var retried = await AdoptAsync(world, first.NextCursor);
        retried.Retryable.ShouldBe(0);
        retried.Available.ShouldBe(2, "the retrying pass commits the whole page atomically after every row has a terminal observation");
        retried.AlreadyRecorded.ShouldBe(0, "the failed pass minted no healthy sibling ahead of its cursor");
        retried.NextCursor.ShouldBeNull();
        (await CountsAsync(world)).Locations.ShouldBe(2);
    }

    [Fact]
    public async Task A_destination_lost_after_evidence_is_retryable_and_never_becomes_a_page_of_missing_locations()
    {
        var world = await SeedAsync(Candidate("witness on a mount that disappears"), Candidate("second object on that mount"));
        var evidence = await AdoptAsync(world);
        Directory.Delete(world.RootPath, recursive: true);

        var result = await AdoptAsync(world, evidence.NextCursor);

        result.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.DestinationUnavailable);
        result.Retryable.ShouldBe(2);
        result.Missing.ShouldBe(0, "an absent namespace cannot testify that either object was deleted");
        AssertSameLogicalPage(world, evidence.NextCursor!, result.NextCursor!,
            "a dead destination rotates only the claim revision, never the minting page");
        (await CountsAsync(world)).Locations.ShouldBe(0);
    }

    [Fact]
    public async Task A_driver_release_failure_in_evidence_keeps_the_same_population_reachable()
    {
        var world = await SeedAsync(Candidate("evidence survives handle cleanup"));
        LegacyPlacementAdoptionSummary failed;
        using (var scope = DecoratingScope(lease => new UnreleasableDriver(lease)))
            failed = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, null), CancellationToken.None);

        failed.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.DestinationUnavailable);
        failed.Retryable.ShouldBe(1);
        failed.NextCursor.ShouldNotBeNull("a release fault occurs after the page answer and must not erase the page that still needs a clean pass");
        (await CountsAsync(world)).Locations.ShouldBe(0);

        var recovered = await AdoptAsync(world, failed.NextCursor, batchSize: 1);
        recovered.AdoptionAdmissible.ShouldBeTrue();
        recovered.NextCursor.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_driver_release_failure_after_hashing_commits_nothing_and_replays_the_same_minting_page()
    {
        var world = await SeedAsync(Candidate("minting survives handle cleanup"));
        var evidence = await AdoptAsync(world);
        LegacyPlacementAdoptionSummary failed;
        using (var scope = DecoratingScope(lease => new UnreleasableDriver(lease)))
            failed = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, evidence.NextCursor), CancellationToken.None);

        failed.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.DestinationUnavailable);
        failed.Retryable.ShouldBe(1);
        AssertSameLogicalPage(world, evidence.NextCursor!, failed.NextCursor!,
            "driver release failure rotates only the claim revision, never the minting page");
        (await CountsAsync(world)).Locations.ShouldBe(0, "provider I/O is not durable until the driver scope has closed cleanly and the database transaction commits");

        var recovered = await AdoptAsync(world, failed.NextCursor, batchSize: 1);
        recovered.Available.ShouldBe(1);
        recovered.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task Ephemeral_etags_neither_arm_a_read_fence_nor_become_durable_location_identity()
    {
        var world = await SeedAsync(Candidate("same bytes behind changing request etags"));
        var fault = new EphemeralMetadataFault(changeETag: true, lieAboutHeadLength: false);
        LegacyPlacementAdoptionSummary evidence;
        using (var scope = DecoratingScope(lease => new EphemeralMetadataDriver(lease, fault)))
            evidence = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, null), CancellationToken.None);
        using (var scope = DecoratingScope(lease => new EphemeralMetadataDriver(lease, fault)))
        {
            var minted = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, evidence.NextCursor), CancellationToken.None);
            minted.Available.ShouldBe(1);
            minted.Corrupt.ShouldBe(0);
        }

        fault.ExpectedETags.ShouldAllBe(value => value == null,
            "an ETag is a fence only when the actual activated driver declares StableETag");
        using var read = _fixture.BeginScope();
        (await read.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking()
            .SingleAsync(value => value.TeamId == world.TeamId)).ProviderETag.ShouldBeNull();
    }

    [Fact]
    public async Task A_complete_matching_hash_outvotes_a_stale_head_length_when_no_durable_fence_exists()
    {
        var world = await SeedAsync(Candidate("the streamed identity is authoritative"));
        var fault = new EphemeralMetadataFault(changeETag: false, lieAboutHeadLength: true);
        LegacyPlacementAdoptionSummary evidence;
        using (var scope = DecoratingScope(lease => new EphemeralMetadataDriver(lease, fault)))
            evidence = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, null), CancellationToken.None);
        using (var scope = DecoratingScope(lease => new EphemeralMetadataDriver(lease, fault)))
        {
            var minted = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, evidence.NextCursor), CancellationToken.None);
            minted.Available.ShouldBe(1);
            minted.Corrupt.ShouldBe(0,
                "without a token tying HEAD to the stream, only the complete stream length and SHA identify the immutable artifact");
        }
    }

    [Fact]
    public async Task A_stream_that_never_ends_is_classified_after_at_most_the_claimed_size_plus_one_byte()
    {
        var world = await SeedAsync(Candidate("bounded witness"));
        var evidence = await AdoptAsync(world);
        var bytes = world.Artifacts.Single().ExpectedBytes;
        var stream = new EndlessAfterPrefix(bytes);

        LegacyPlacementAdoptionSummary result;
        using (var scope = DecoratingScope(lease => new EndlessReadDriver(lease, stream)))
            result = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, evidence.NextCursor), CancellationToken.None);

        stream.BytesRead.ShouldBe(bytes.Length + 1L,
            "a provider may lie about its declared length or never return EOF; adoption owns a hard expected-size-plus-one byte budget");
        result.Available.ShouldBe(0);
        result.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.AdmissionEvidenceMissing,
            "an overlong witness no longer proves that this namespace is the one evidence observed");
        (await CountsAsync(world)).Locations.ShouldBe(0);
    }

    [Fact]
    public async Task A_head_collision_cannot_use_the_wrong_bytes_as_a_witness_for_missing_rows()
    {
        var world = await SeedAsync(Candidate("witness was exact"), Candidate("would become missing"));
        var evidence = await AdoptAsync(world, batchSize: 2);
        evidence.AdoptionAdmissible.ShouldBeTrue();

        var witness = world.Artifacts[0];
        File.WriteAllBytes(new Uri(witness.StorageUrl).LocalPath, witness.ExpectedBytes.Select(value => (byte)(value ^ 0x5a)).ToArray());
        File.Delete(new Uri(world.Artifacts[1].StorageUrl).LocalPath);

        var result = await AdoptAsync(world, evidence.NextCursor, batchSize: 2);

        result.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.AdmissionEvidenceMissing,
            "HEAD existence is not namespace evidence: only the full size and SHA of the immutable source can authorize Missing observations");
        result.Missing.ShouldBe(0);
        result.Corrupt.ShouldBe(0);
        (await CountsAsync(world)).Locations.ShouldBe(0, "a false witness must authorize none of the page, including itself");
    }

    [Fact]
    public async Task Legacy_adoption_refuses_a_module_or_activated_driver_that_cannot_probe_destination_liveness()
    {
        var world = await SeedAsync(Candidate("health probe is part of the generic contract"));

        using (var scope = CapabilityCatalogScope(StorageProviderCapabilities.HealthProbe))
        {
            var moduleRefusal = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, null), CancellationToken.None);
            moduleRefusal.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.ProviderHasNoHealthProbe);
        }
        (await LiveArcCountAsync(world.TeamId)).ShouldBe(0,
            "a capability preflight refusal must close the manifest it created instead of permanently owning the team singleton");

        using (var scope = DecoratingScope(lease => new MissingCapabilityDriver(lease, StorageProviderCapabilities.HealthProbe)))
        {
            var driverRefusal = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, null), CancellationToken.None);
            driverRefusal.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.ProviderHasNoHealthProbe);
        }

        (await CountsAsync(world)).Locations.ShouldBe(0);
        (await LiveArcCountAsync(world.TeamId)).ShouldBe(0);
    }

    [Fact]
    public async Task A_broker_activation_failure_preserves_the_exact_resumable_cursor_in_evidence_and_minting()
    {
        var world = await SeedAsync(Candidate("activation recovers"));
        var unavailable = new StorageRuntimeDriverResolution.ProfileUnavailable(StorageRuntimeProfileFailureReason.ResolutionFailed);

        LegacyPlacementAdoptionSummary first;
        using (var scope = BrokerResolutionScope(unavailable))
            first = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, null), CancellationToken.None);

        first.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.DestinationUnavailable);
        first.NextCursor.ShouldNotBeNull("even the initial call has already fixed a snapshot cursor before broker activation");
        var evidence = await AdoptAsync(world, first.NextCursor);
        evidence.AdoptionAdmissible.ShouldBeTrue();

        LegacyPlacementAdoptionSummary interrupted;
        using (var scope = BrokerResolutionScope(unavailable))
            interrupted = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, evidence.NextCursor), CancellationToken.None);

        interrupted.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.DestinationUnavailable);
        AssertSameLogicalPage(world, evidence.NextCursor!, interrupted.NextCursor!,
            "a response-driven client receives the same minting page at a newer lease revision, not a false completion");
        var recovered = await AdoptAsync(world, interrupted.NextCursor);
        recovered.Available.ShouldBe(1);
    }

    [Fact]
    public async Task A_cancelled_provider_read_releases_its_claim_without_waiting_for_the_one_hour_crash_lease()
    {
        var world = await SeedAsync(Candidate("cancelled HTTP pass resumes"));
        var evidence = await AdoptAsync(world);
        var gate = new BlockingRead();
        using var cancellation = new CancellationTokenSource();
        using (var scope = DecoratingScope(lease => new BlockingReadDriver(lease, gate)))
        {
            var interrupted = scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, evidence.NextCursor), cancellation.Token);
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await interrupted.ShouldThrowAsync<OperationCanceledException>();
        }

        var current = await AdoptAsync(world, evidence.NextCursor);
        current.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.CursorSuperseded,
            "the interrupted worker advanced and then released the claim revision; the old client cursor receives the server current cursor");
        var recovered = await AdoptAsync(world, current.NextCursor);
        recovered.Available.ShouldBe(1);
        recovered.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.None,
            "a routine request cancellation must not leave ArcBusy until the crash-only lease expires");
    }

    [Fact]
    public async Task A_terminal_cursor_replays_its_tombstone_after_the_profile_is_retired()
    {
        var world = await SeedAsync(Candidate("terminal replay does not depend on live profile eligibility"));
        var unavailable = new StorageRuntimeDriverResolution.ProfileUnavailable(StorageRuntimeProfileFailureReason.ResolutionFailed);
        LegacyPlacementAdoptionSummary started;
        using (var scope = BrokerResolutionScope(unavailable))
            started = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, null), CancellationToken.None);

        LegacyPlacementAdoptionSummary terminal;
        using (var scope = ThrowingLayoutScope(world.Artifacts.Single().StorageUrl))
            terminal = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, started.NextCursor), CancellationToken.None);
        terminal.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.AdmissionEvidenceMissing);
        await RetireProfileAsync(world);

        var replay = await AdoptAsync(world, started.NextCursor);
        replay.ShouldBe(terminal, "the compact terminal summary is the authority for a lost response throughout its retention window");
        (await AdoptAsync(world)).Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.ProfileRetired,
            "only a cursor-addressed tombstone bypasses current profile eligibility; a new arc remains forbidden");
    }

    [Fact]
    public async Task A_retired_profile_closes_its_unfinished_manifest_without_the_original_cursor_returning()
    {
        var world = await SeedAsync(Candidate("retired profile cannot strand the team singleton"));
        var unavailable = new StorageRuntimeDriverResolution.ProfileUnavailable(StorageRuntimeProfileFailureReason.ResolutionFailed);
        LegacyPlacementAdoptionSummary started;
        using (var scope = BrokerResolutionScope(unavailable))
            started = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, null), CancellationToken.None);
        started.NextCursor.ShouldNotBeNull();
        await RetireProfileAsync(world);

        var closed = await AdoptAsync(world);

        closed.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.ProfileRetired);
        closed.NextCursor.ShouldBeNull("bounded cleanup reaches a compact tombstone without the original response-driven client");
        (await LiveArcCountAsync(world.TeamId)).ShouldBe(0, "a retired profile cannot retain the team singleton indefinitely");
        (await AdoptAsync(world, started.NextCursor)).ShouldBe(closed,
            "the original cursor replays the same terminal audit summary after out-of-band cleanup");
    }

    [Fact]
    public async Task A_source_deleted_after_provider_io_is_terminally_skipped_and_never_mints_an_orphan_sidecar()
    {
        var world = await SeedAsync(Candidate("source reclaimed during phase two"));
        var evidence = await AdoptAsync(world);
        var gate = new BlockingRead();
        LegacyPlacementAdoptionSummary result;
        using (var scope = DecoratingScope(lease => new BlockingReadDriver(lease, gate)))
        {
            var adoption = scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, evidence.NextCursor), CancellationToken.None);
            try
            {
                await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await DeleteSourceAsync(world.Artifacts.Single().Id);
            }
            finally
            {
                gate.Continue.TrySetResult();
            }
            result = await adoption;
        }

        result.Conflicts.ShouldBe(1, "a vanished immutable source is a terminal row answer, not a retry that can lock the page forever");
        result.Retryable.ShouldBe(0);
        result.NextCursor.ShouldBeNull();
        (await CountsAsync(world)).Locations.ShouldBe(0,
            "the exact team/id/url/digest/size source identity must still exist under the commit transaction's lock");
    }

    [Fact]
    public async Task Retention_cannot_delete_bytes_between_adoption_hashing_and_source_revalidation()
    {
        var isolated = new PostgresFixture();
        await isolated.InitializeAsync();
        try
        {
            using var scenario = new LegacyPlacementAdoptionTests(isolated);
            await scenario.RetentionRaceCoreAsync();
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    private async Task RetentionRaceCoreAsync()
    {
        var world = await SeedAsync();
        var retained = await DeclareRetainedOffloadAsync(world, "retention races phase-two commit");
        await RepointAsync(world, retained.RootPath);
        await AgeDeclarationAsync(retained.ArtifactId, TimeSpan.FromDays(30));
        var evidence = await AdoptAsync(world);

        using (var quarantineScope = _fixture.BeginScope())
        {
            var pass = await quarantineScope.Resolve<IArtifactRetentionReaper>().SweepAsync(CancellationToken.None);
            pass.Quarantined.ShouldBe(1);
        }
        await AgeQuarantineAsync(retained.ArtifactId, TimeSpan.FromDays(2));

        var release = new AsyncGate();
        var lockAttempt = new SqlAttemptGate("FROM workflow_artifact_retention", "FOR SHARE");
        Task<LegacyPlacementAdoptionSummary> adoption;
        using var adoptionScope = RetentionRaceScope(release, lockAttempt);
        adoption = adoptionScope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
            new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, evidence.NextCursor), CancellationToken.None);
        var deletion = new AsyncGate();
        Task<ArtifactRetentionSweepSummary>? sweep = null;
        var blockedAtRetention = false;
        try
        {
            await release.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using (var reaperScope = _fixture.BeginScope())
            {
                var backend = new BlockingAfterDeleteBlobBackend(reaperScope.Resolve<IArtifactBlobBackend>(), deletion);
                var reaper = new ArtifactRetentionReaper(reaperScope.Resolve<DbContextOptions<CodeSpaceDbContext>>(), reaperScope.Resolve<IArtifactReferenceOracle>(),
                    backend, reaperScope.Resolve<IArtifactCasPurgeCoordinator>(), NullLogger<ArtifactRetentionReaper>.Instance);
                sweep = reaper.SweepAsync(CancellationToken.None);
                await deletion.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                File.Exists(new Uri(retained.StorageUrl).LocalPath).ShouldBeFalse("the reaper has removed bytes while its retention UPDATE lock is still held");

                release.Continue.TrySetResult();
                await AssertBlockedOnRetentionAsync(lockAttempt, retained.ArtifactId);
                blockedAtRetention = true;
                deletion.Continue.TrySetResult();
                await sweep;
            }
        }
        finally
        {
            release.Continue.TrySetResult();
            deletion.Continue.TrySetResult();
            if (sweep != null) await DrainAsync(sweep);
            await DrainAsync(adoption);
        }

        var result = await adoption;
        blockedAtRetention.ShouldBeTrue("the retention row lock must order source revalidation after the in-flight physical delete");
        result.Conflicts.ShouldBe(1, "after the reaper commits, source revalidation must observe the deleted row and mint nothing");
        result.Available.ShouldBe(0);
        (await CountsAsync(world)).Locations.ShouldBe(0);
    }

    [Fact]
    public async Task Retention_of_a_witness_behind_the_mint_cursor_invalidates_a_later_page_before_it_can_commit()
    {
        var isolated = new PostgresFixture();
        await isolated.InitializeAsync();
        try
        {
            using var scenario = new LegacyPlacementAdoptionTests(isolated);
            await scenario.WitnessRetentionRaceCoreAsync();
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    private async Task WitnessRetentionRaceCoreAsync()
    {
        var world = await SeedAsync();
        var first = await DeclareRetainedOffloadAsync(world, new string('L', 4096));
        var second = await DeclareRetainedOffloadAsync(world, "s");
        var third = await DeclareRetainedOffloadAsync(world, new string('M', 2048));
        first.RootPath.ShouldBe(second.RootPath, "all immutable rows must resolve through the exact same legacy destination");
        first.RootPath.ShouldBe(third.RootPath);
        await RepointAsync(world, first.RootPath);
        await AgeDeclarationAsync(first.ArtifactId, TimeSpan.FromDays(33));
        await AgeDeclarationAsync(second.ArtifactId, TimeSpan.FromDays(32));
        await AgeDeclarationAsync(third.ArtifactId, TimeSpan.FromDays(31));
        await DeferDeclarationAsync(first.ArtifactId);
        await DeferDeclarationAsync(third.ArtifactId);

        var evidence = await AdoptAsync(world, batchSize: 1);
        evidence.Phase.ShouldBe(LegacyPlacementAdoptionPhaseValue.Evidence,
            "a witness on the first page is provisional until every sealed member has passed Evidence");
        evidence.AdoptionAdmissible.ShouldBeFalse();
        evidence = await AdoptAsync(world, evidence.NextCursor, batchSize: 1);
        evidence.AdoptionAdmissible.ShouldBeFalse();
        evidence = await AdoptAsync(world, evidence.NextCursor, batchSize: 1);
        evidence.AdoptionAdmissible.ShouldBeTrue();
        var firstMint = await AdoptAsync(world, evidence.NextCursor, batchSize: 2);
        firstMint.Available.ShouldBe(2);
        firstMint.NextCursor.ShouldNotBeNull("the third manifest member must remain after the first two mint positions advance");

        Guid witnessId;
        Guid laterId;
        using (var censusScope = _fixture.BeginScope())
        {
            var protector = censusScope.Resolve<IDataProtectionProvider>().CreateProtector(LegacyPlacementAdoptionCursor.ProtectorPurpose);
            LegacyPlacementAdoptionCursor.TryDecode(firstMint.NextCursor!, world.ProfileId, protector, out var cursor).ShouldBeTrue();
            var db = censusScope.Resolve<CodeSpaceDbContext>();
            var arc = await db.LegacyPlacementAdoptionArc.AsNoTracking().SingleAsync(value => value.Id == cursor.ArcId);
            witnessId = arc.WitnessWorkflowArtifactId.ShouldNotBeNull();
            witnessId.ShouldBe(second.ArtifactId, "the smallest confirmed member is position two, never the manifest's first row");
            var witnessPosition = await db.LegacyPlacementAdoptionMember.AsNoTracking()
                .Where(value => value.ArcId == arc.Id && value.WorkflowArtifactId == witnessId).Select(value => value.Position).SingleAsync();
            var later = await db.LegacyPlacementAdoptionMember.AsNoTracking()
                .Where(value => value.ArcId == arc.Id && value.Position > cursor.Position).OrderBy(value => value.Position)
                .Select(value => new { value.Position, value.WorkflowArtifactId }).SingleAsync();
            witnessPosition.ShouldBeLessThanOrEqualTo(cursor.Position,
                "the evidence witness must be retained behind the durable mint cursor, not be part of the current page");
            later.Position.ShouldBeGreaterThan(cursor.Position);
            laterId = later.WorkflowArtifactId;
        }

        var retained = new[] { first, second, third }.Single(value => value.ArtifactId == witnessId);
        laterId.ShouldBe(third.ArtifactId);
        using (var quarantineScope = _fixture.BeginScope())
        {
            var pass = await quarantineScope.Resolve<IArtifactRetentionReaper>().SweepAsync(CancellationToken.None);
            pass.Quarantined.ShouldBe(1);
        }
        await AgeQuarantineAsync(witnessId, TimeSpan.FromDays(2));

        var release = new AsyncGate();
        var lockAttempt = new SqlAttemptGate("FROM workflow_artifact_retention", "FOR SHARE");
        using var adoptionScope = RetentionRaceScope(release, lockAttempt);
        var adoption = adoptionScope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
            new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, firstMint.NextCursor), CancellationToken.None);
        var deletion = new AsyncGate();
        Task<ArtifactRetentionSweepSummary>? sweep = null;
        var blockedAtWitnessRetention = false;
        try
        {
            await release.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using (var reaperScope = _fixture.BeginScope())
            {
                var backend = new BlockingAfterDeleteBlobBackend(reaperScope.Resolve<IArtifactBlobBackend>(), deletion);
                var reaper = new ArtifactRetentionReaper(reaperScope.Resolve<DbContextOptions<CodeSpaceDbContext>>(), reaperScope.Resolve<IArtifactReferenceOracle>(),
                    backend, reaperScope.Resolve<IArtifactCasPurgeCoordinator>(), NullLogger<ArtifactRetentionReaper>.Instance);
                sweep = reaper.SweepAsync(CancellationToken.None);
                await deletion.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                File.Exists(new Uri(retained.StorageUrl).LocalPath).ShouldBeFalse("retention removed the behind-cursor witness while its declaration lock remains held");

                release.Continue.TrySetResult();
                await AssertBlockedOnRetentionAsync(lockAttempt, witnessId, laterId);
                blockedAtWitnessRetention = true;
                deletion.Continue.TrySetResult();
                await sweep;
            }
        }
        finally
        {
            release.Continue.TrySetResult();
            deletion.Continue.TrySetResult();
            if (sweep != null) await DrainAsync(sweep);
            await DrainAsync(adoption);
        }

        var result = await adoption;
        blockedAtWitnessRetention.ShouldBeTrue("the current page commit must lock the durable witness declaration even though that source is behind its cursor");
        result.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.AdmissionEvidenceMissing);
        result.Conflicts.ShouldBe(1);
        result.Available.ShouldBe(0, "the later page cannot use provider observations authorized by a witness that retention already removed");
        var counts = await CountsAsync(world);
        counts.Locations.ShouldBe(2, "the two earlier sidecars remain, but the later page mints nothing");
        counts.Events.ShouldBe(2);
        using var finalScope = _fixture.BeginScope();
        var eventSources = (await finalScope.Resolve<CodeSpaceDbContext>().ArtifactLocationEvent.AsNoTracking()
            .Select(value => value.DetailsJson).ToListAsync()).Select(SourceId).ToHashSet();
        eventSources.SetEquals(new[] { first.ArtifactId, witnessId }).ShouldBeTrue();
    }

    [Fact]
    public async Task One_permanently_unresolved_member_refuses_the_whole_closed_population()
    {
        var world = await SeedAsync(Candidate("layout throws forever"), Candidate("layout still resolves this row"));
        var malformed = world.Artifacts[0].StorageUrl;
        LegacyPlacementAdoptionSummary evidence;
        using (var scope = ThrowingLayoutScope(malformed))
            evidence = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 2, null), CancellationToken.None);

        evidence.AdoptionAdmissible.ShouldBeFalse();
        evidence.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.AdmissionEvidenceMissing);
        evidence.Resolved.ShouldBe(1);
        evidence.Unresolved.ShouldBe(1);
        evidence.Retryable.ShouldBe(0);
        evidence.NextCursor.ShouldBeNull("pure layout resolution is fixed by the profile revision and must not lock a page in retry");
        (await CountsAsync(world)).Locations.ShouldBe(0);
    }

    [Fact]
    public async Task A_confirmed_early_page_cannot_admit_minting_before_a_later_unresolved_member_is_examined()
    {
        var world = await SeedAsync();
        await AddArtifactAsync(world, Candidate("confirmed first page"), DateTimeOffset.UnixEpoch.AddDays(10));
        var malformed = await AddArtifactAsync(world, Candidate("unresolved later page"), DateTimeOffset.UnixEpoch.AddDays(11));

        LegacyPlacementAdoptionSummary first;
        using (var scope = ThrowingLayoutScope(malformed.StorageUrl))
            first = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, null), CancellationToken.None);

        first.Phase.ShouldBe(LegacyPlacementAdoptionPhaseValue.Evidence);
        first.AdoptionAdmissible.ShouldBeFalse("a provisional witness cannot authorize Minting before the closed manifest reaches its end");
        first.NextCursor.ShouldNotBeNull();
        LegacyPlacementAdoptionSummary refused;
        using (var scope = ThrowingLayoutScope(malformed.StorageUrl))
            refused = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, first.NextCursor), CancellationToken.None);

        refused.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.AdmissionEvidenceMissing);
        refused.Unresolved.ShouldBe(1);
        refused.NextCursor.ShouldBeNull();
        (await CountsAsync(world)).Locations.ShouldBe(0);
    }

    [Fact]
    public async Task An_unresolved_early_page_fails_closed_instead_of_being_outvoted_by_a_later_witness()
    {
        var world = await SeedAsync();
        var malformed = await AddArtifactAsync(world, Candidate("unresolved first page"), DateTimeOffset.UnixEpoch.AddDays(20));
        await AddArtifactAsync(world, Candidate("confirmable later page"), DateTimeOffset.UnixEpoch.AddDays(21));

        LegacyPlacementAdoptionSummary refused;
        using (var scope = ThrowingLayoutScope(malformed.StorageUrl))
            refused = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, null), CancellationToken.None);

        refused.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.AdmissionEvidenceMissing);
        refused.Unresolved.ShouldBe(1);
        refused.NextCursor.ShouldBeNull("a permanent layout failure is terminal for this exact profile revision, not a page to skip");
        (await CountsAsync(world)).Locations.ShouldBe(0);
    }

    [Fact]
    public async Task Evidence_replaces_a_large_provisional_witness_with_the_smallest_later_confirmed_member()
    {
        var world = await SeedAsync();
        var large = await AddArtifactAsync(world, Candidate(new string('L', 64 * 1024)), DateTimeOffset.UnixEpoch.AddDays(30));
        var small = await AddArtifactAsync(world, Candidate("small durable witness"), DateTimeOffset.UnixEpoch.AddDays(31));

        var first = await AdoptAsync(world, batchSize: 1);
        first.AdoptionAdmissible.ShouldBeFalse();
        var finalEvidence = await AdoptAsync(world, first.NextCursor, batchSize: 1);
        finalEvidence.AdoptionAdmissible.ShouldBeTrue();

        using var scope = _fixture.BeginScope();
        var protector = scope.Resolve<IDataProtectionProvider>().CreateProtector(LegacyPlacementAdoptionCursor.ProtectorPurpose);
        LegacyPlacementAdoptionCursor.TryDecode(finalEvidence.NextCursor!, world.ProfileId, protector, out var cursor).ShouldBeTrue();
        var witness = await scope.Resolve<CodeSpaceDbContext>().LegacyPlacementAdoptionArc.AsNoTracking()
            .Where(value => value.Id == cursor.ArcId).Select(value => value.WitnessWorkflowArtifactId).SingleAsync();
        witness.ShouldBe(small.Id, "every mint page re-hashes the witness, so Evidence must minimize (size, position) across the whole manifest");
        witness.ShouldNotBe(large.Id);
    }

    [Fact]
    public async Task Cursor_is_typed_when_invalid_or_stale_and_keeps_one_immutable_population_across_both_phases()
    {
        var world = await SeedAsync(Candidate("first snapshot row"), Candidate("second snapshot row"));

        var invalid = await AdoptAsync(world, "not-a-cursor");
        invalid.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.CursorInvalid);

        var evidence = await AdoptAsync(world, batchSize: 1);
        evidence.AdoptionAdmissible.ShouldBeFalse("the first bounded Evidence page has not validated the full sealed population");
        await AddArtifactAsync(world, Candidate("arrived after the snapshot"));

        var cursor = evidence.NextCursor;
        while (cursor != null)
        {
            var page = await AdoptAsync(world, cursor, batchSize: 1);
            cursor = page.NextCursor;
        }

        (await CountsAsync(world)).Locations.ShouldBe(2, "a later insert is outside the snapshot cutoff and belongs to the next adoption arc");

        var staleWorld = await SeedAsync(Candidate("revision pinned"));
        var staleEvidence = await AdoptAsync(staleWorld);
        await AdvanceRevisionAsync(staleWorld);

        var stale = await AdoptAsync(staleWorld, staleEvidence.NextCursor);
        stale.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.CursorStale);
        (await CountsAsync(staleWorld)).Locations.ShouldBe(0);
        (await LiveArcCountAsync(staleWorld.TeamId)).ShouldBe(0,
            "the obsolete revision must reach its compact Stale tombstone instead of owning the live singleton forever");
        var currentRevision = await AdoptAsync(staleWorld);
        currentRevision.Refusal.ShouldNotBe(LegacyPlacementAdoptionRefusalValue.ArcAlreadyActive);
        currentRevision.ProfileRevision.ShouldBe(2, "a new arc can bind the profile's current revision immediately after bounded stale cleanup");
    }

    [Fact]
    public async Task An_expired_arc_can_be_boundedly_cleaned_by_another_profile_without_the_original_cursor()
    {
        var original = await SeedAsync(Candidate("orphaned original arc"));
        var replacement = await SeedSiblingProfileAsync(original);
        var unavailable = new StorageRuntimeDriverResolution.ProfileUnavailable(StorageRuntimeProfileFailureReason.ResolutionFailed);
        using (var scope = BrokerResolutionScope(unavailable))
        {
            var started = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(original.TeamId, original.ActorId, original.ProfileId, 1, null), CancellationToken.None);
            started.NextCursor.ShouldNotBeNull();
        }
        await ExpireLiveArcAsync(original.TeamId);

        var cleanup = await AdoptAsync(replacement);
        cleanup.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.ArcAlreadyActive,
            "the competing profile performs one bounded cleanup pass but cannot adopt the expired profile's identity");
        (await LiveArcCountAsync(original.TeamId)).ShouldBe(0);

        var acquired = await AdoptAsync(replacement);
        acquired.Refusal.ShouldNotBe(LegacyPlacementAdoptionRefusalValue.ArcAlreadyActive,
            "the next bounded call can acquire the team singleton without the original profile or cursor returning");
        acquired.ProfileId.ShouldBe(replacement.ProfileId);
    }

    [Fact]
    public async Task A_missing_profile_request_cannot_stale_another_profiles_live_arc()
    {
        var owner = await SeedAsync(Candidate("live owner remains authoritative"));
        var unavailable = new StorageRuntimeDriverResolution.ProfileUnavailable(StorageRuntimeProfileFailureReason.ResolutionFailed);
        LegacyPlacementAdoptionSummary started;
        using (var scope = BrokerResolutionScope(unavailable))
            started = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(owner.TeamId, owner.ActorId, owner.ProfileId, 1, null), CancellationToken.None);
        var before = await LiveArcSnapshotAsync(owner.TeamId);

        using (var scope = _fixture.BeginScope())
        {
            var intrusion = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(owner.TeamId, owner.ActorId, Guid.NewGuid(), 1, null), CancellationToken.None);
            intrusion.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.ArcAlreadyActive);
        }

        (await LiveArcSnapshotAsync(owner.TeamId)).ShouldBe(before,
            "an unrelated missing profile has no authority to mutate the owner's revision, state, or sealed membership");
        var resumed = await AdoptAsync(owner, started.NextCursor);
        resumed.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.None);
        resumed.AdoptionAdmissible.ShouldBeTrue();
    }

    [Fact]
    public async Task A_slow_clock_insert_behind_an_advanced_evidence_position_cannot_join_the_sealed_population()
    {
        var world = await SeedAsync(Candidate("first manifest row is missing", LegacyShape.Missing), Candidate("second manifest row confirms the destination"));
        var firstEvidence = await AdoptAsync(world, batchSize: 1);
        firstEvidence.AdoptionAdmissible.ShouldBeFalse();
        firstEvidence.NextCursor.ShouldNotBeNull();
        await AssertArcCensusAsync(world, firstEvidence.NextCursor!, expectedMembers: 2,
            LegacyPlacementAdoptionCursorMode.Evidence, positionMustBePositive: true);

        var late = await AddArtifactAsync(world, Candidate("slow-clock row committed behind the evidence cursor"), DateTimeOffset.UnixEpoch.AddDays(1));
        await AssertOutsideArcAsync(world, firstEvidence.NextCursor!, late.Id);

        var cursor = firstEvidence.NextCursor;
        while (cursor != null) cursor = (await AdoptAsync(world, cursor, batchSize: 1)).NextCursor;

        var counts = await CountsAsync(world);
        counts.Locations.ShouldBe(2, "the late row was never part of this closed arc even though its application timestamp sorts before the durable position");
        counts.Events.ShouldBe(2);
    }

    [Fact]
    public async Task A_slow_clock_insert_after_evidence_cannot_enter_minting_without_having_supplied_evidence()
    {
        var world = await SeedAsync(Candidate("sealed witness"));
        var evidence = await AdoptAsync(world, batchSize: 1);
        evidence.AdoptionAdmissible.ShouldBeTrue();
        await AssertArcCensusAsync(world, evidence.NextCursor!, expectedMembers: 1,
            LegacyPlacementAdoptionCursorMode.Minting, positionMustBePositive: false);

        var late = await AddArtifactAsync(world, Candidate("slow-clock row after evidence"), DateTimeOffset.UnixEpoch.AddDays(2));
        await AssertOutsideArcAsync(world, evidence.NextCursor!, late.Id);
        var completed = await AdoptAsync(world, evidence.NextCursor, batchSize: 1);

        completed.Available.ShouldBe(1);
        completed.NextCursor.ShouldBeNull();
        (await CountsAsync(world)).Locations.ShouldBe(1,
            "minting must consume exactly the Evidence manifest, never a live row that appeared after destination confirmation");
    }

    [Fact]
    public async Task A_resumed_page_is_a_composite_index_range_over_the_production_query_not_a_team_history_scan()
    {
        var world = await SeedAsync(Candidate("first indexed row"), Candidate("middle indexed row"), Candidate("last indexed row"));
        var captured = new CapturedLegacyPage();
        using (var pass = CapturingScope(captured))
        {
            var result = await pass.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, null), CancellationToken.None);
            result.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.None);
        }

        captured.CommandText.ShouldNotBeNull("the adopter must issue its bounded page query before any plan can be asserted");
        captured.CommandText.ShouldContain("legacy_placement_adoption_member", customMessage: "pages must read the closed manifest, never the live artifact population");
        captured.CommandText.ShouldContain("position", customMessage: "the resume point is the manifest's DB-owned monotonic identity");
        captured.CommandText.ShouldContain(">", customMessage: "the position cursor must be a keyset lower bound");

        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "SET LOCAL enable_seqscan = off");
        await ExecuteAsync(connection, transaction, "SET LOCAL enable_sort = off");
        await ExecuteAsync(connection, transaction, "SET LOCAL enable_incremental_sort = off");

        var plan = await ExplainAsync(connection, transaction, captured);

        plan.ShouldContain("legacy_placement_adoption_member_pkey",
            customMessage: "the production page must use the manifest's (arc_id,position) primary-key order");
        plan.ShouldContain("Index Cond:", customMessage: "arc identity and cursor position must constrain the btree scan itself");
        plan.ShouldNotContain("Seq Scan on legacy_placement_adoption_member");
        plan.ShouldNotContain("Filter:", customMessage: "a cursor applied after the scan still makes a deep page linear in prior rows");
        plan.ShouldNotContain("Sort", customMessage: "the primary-key order must satisfy position directly");
        await transaction.RollbackAsync();
    }

    private async Task<LegacyPlacementAdoptionSummary> AdoptAsync(World world, string? cursor = null, int batchSize = LegacyPlacementAdoptionLimits.DefaultRowsPerPass)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
            new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, batchSize, cursor), CancellationToken.None);
    }

    private void AssertSameLogicalPage(World world, string expected, string actual, string message)
    {
        using var scope = _fixture.BeginScope();
        var protector = scope.Resolve<IDataProtectionProvider>().CreateProtector(LegacyPlacementAdoptionCursor.ProtectorPurpose);
        LegacyPlacementAdoptionCursor.TryDecode(expected, world.ProfileId, protector, out var before).ShouldBeTrue();
        LegacyPlacementAdoptionCursor.TryDecode(actual, world.ProfileId, protector, out var after).ShouldBeTrue();
        after.ArcId.ShouldBe(before.ArcId, message);
        after.Mode.ShouldBe(before.Mode, message);
        after.Position.ShouldBe(before.Position, message);
        after.ArcRevision.ShouldBeGreaterThan(before.ArcRevision, "claim acquire/release is itself revision-fenced");
    }

    private ILifetimeScope CapturingScope(CapturedLegacyPage captured) => _fixture.BeginScope(builder => builder
        .Register<ILegacyPlacementAdopter>(context => new LegacyPlacementAdopter(
            new DbContextOptionsBuilder<CodeSpaceDbContext>(context.Resolve<DbContextOptions<CodeSpaceDbContext>>()).AddInterceptors(captured).Options,
            context.Resolve<IStorageProviderModuleCatalog>(), context.Resolve<IStorageRuntimeDriverBroker>(),
            context.Resolve<IDataProtectionProvider>(), context.Resolve<ILogger<LegacyPlacementAdopter>>()))
        .InstancePerLifetimeScope());

    private ILifetimeScope DecoratingScope(Func<StorageRuntimeDriverLease, IArtifactStorageDriver> decorate) => _fixture.BeginScope(builder => builder
        .Register<IStorageRuntimeDriverBroker>(context => new DecoratingBroker(context.Resolve<StorageRuntimeDriverBroker>(), decorate))
        .InstancePerLifetimeScope());

    private ILifetimeScope RetentionRaceScope(AsyncGate release, SqlAttemptGate lockAttempt) => _fixture.BeginScope(builder =>
    {
        builder.Register<IStorageRuntimeDriverBroker>(context => new DecoratingBroker(
            context.Resolve<StorageRuntimeDriverBroker>(), lease => new BlockingDisposeDriver(lease, release))).InstancePerLifetimeScope();
        builder.Register<ILegacyPlacementAdopter>(context => new LegacyPlacementAdopter(
            new DbContextOptionsBuilder<CodeSpaceDbContext>(context.Resolve<DbContextOptions<CodeSpaceDbContext>>()).AddInterceptors(lockAttempt).Options,
            context.Resolve<IStorageProviderModuleCatalog>(), context.Resolve<IStorageRuntimeDriverBroker>(),
            context.Resolve<IDataProtectionProvider>(), context.Resolve<ILogger<LegacyPlacementAdopter>>())).InstancePerLifetimeScope();
    });

    private ILifetimeScope CapabilityCatalogScope(StorageProviderCapabilities removed)
    {
        IStorageProviderModuleCatalog inner;
        using (var scope = _fixture.BeginScope()) inner = scope.Resolve<IStorageProviderModuleCatalog>();

        return _fixture.BeginScope(builder => builder.RegisterInstance(new CapabilityMaskCatalog(inner, removed))
            .As<IStorageProviderModuleCatalog>().SingleInstance());
    }

    private ILifetimeScope BrokerResolutionScope(StorageRuntimeDriverResolution resolution) => _fixture.BeginScope(builder => builder
        .RegisterInstance(new FixedResolutionBroker(resolution)).As<IStorageRuntimeDriverBroker>().SingleInstance());

    private ILifetimeScope BrokerScope(IStorageRuntimeDriverBroker broker) => _fixture.BeginScope(builder => builder
        .RegisterInstance(broker).As<IStorageRuntimeDriverBroker>().SingleInstance());

    private ILifetimeScope ThrowingLayoutScope(string malformedLocator)
    {
        IStorageProviderModuleCatalog inner;
        using (var scope = _fixture.BeginScope()) inner = scope.Resolve<IStorageProviderModuleCatalog>();

        return _fixture.BeginScope(builder => builder.RegisterInstance(new ThrowingLayoutCatalog(inner, malformedLocator))
            .As<IStorageProviderModuleCatalog>().SingleInstance());
    }

    private static async Task<string> ExplainAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CapturedLegacyPage captured)
    {
        await using var command = new NpgsqlCommand("EXPLAIN (COSTS OFF) " + captured.CommandText, connection, transaction);
        foreach (var (name, value) in captured.Parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);

        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
        return string.Join('\n', lines);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DrainAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch { /* Preserve the assertion that initiated cleanup; the normal path awaits the task again. */ }
    }

    private async Task AssertBlockedOnRetentionAsync(SqlAttemptGate gate, params Guid[] requiredArtifactIds)
    {
        var backendProcessId = await gate.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        requiredArtifactIds.All(gate.ArtifactIds.Contains).ShouldBeTrue(
            "the retention-first lock set must include every current page source and the durable witness even when it is behind the cursor");
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var command = new NpgsqlCommand("SELECT cardinality(pg_blocking_pids(@pid))", connection);
            command.Parameters.AddWithValue("pid", backendProcessId);
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) > 0) return;
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        throw new Xunit.Sdk.XunitException($"Adoption backend {backendProcessId} never became blocked on the retention transaction.");
    }

    private static Guid SourceId(string details)
    {
        using var document = JsonDocument.Parse(details);
        return document.RootElement.GetProperty("workflow_artifact_id").GetGuid();
    }

    private async Task<World> SeedAsync(params CandidateSeed[] candidates)
    {
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = NewRoot("adoption");
        var world = new World
        {
            TeamId = teamId, ActorId = actorId, ProfileId = Guid.NewGuid(), RevisionId = Guid.NewGuid(), RootPath = root,
        };
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        AddProfile(db, world, revision: 1, root);
        await db.SaveChangesAsync();

        foreach (var candidate in candidates) await AddArtifactAsync(world, candidate);
        return world;
    }

    private async Task<World> SeedSiblingProfileAsync(World original)
    {
        var root = NewRoot("replacement-adoption");
        var world = new World
        {
            TeamId = original.TeamId, ActorId = original.ActorId, ProfileId = Guid.NewGuid(), RevisionId = Guid.NewGuid(), RootPath = root,
        };
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        AddProfile(db, world, revision: 1, root);
        await db.SaveChangesAsync();
        return world;
    }

    private async Task<LegacyArtifact> AddArtifactAsync(World world, CandidateSeed seed, DateTimeOffset? createdAt = null)
    {
        var backend = new LocalFileArtifactBlobBackend(world.RootPath);
        var bytes = Encoding.UTF8.GetBytes(seed.Content);
        var sha = ArtifactStore.ComputeSha256Hex(bytes);
        var storageUrl = await backend.WriteAsync(sha, bytes, CancellationToken.None);
        var artifact = new LegacyArtifact(Guid.NewGuid(), bytes, seed.Shape, storageUrl);

        if (seed.Shape == LegacyShape.Missing) File.Delete(new Uri(storageUrl).LocalPath);
        if (seed.Shape == LegacyShape.SameLengthCorrupt)
        {
            var foreign = bytes.Select(value => (byte)(value ^ 0x5a)).ToArray();
            File.WriteAllBytes(new Uri(storageUrl).LocalPath, foreign);
        }

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.WorkflowArtifact.Add(new WorkflowArtifact
        {
            Id = artifact.Id, TeamId = world.TeamId, Sha256 = sha, ContentType = "text/plain", SizeBytes = bytes.Length,
            StorageUrl = storageUrl, CasArtifactObjectId = null, CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        world.Artifacts.Add(artifact);
        return artifact;
    }

    private async Task AssertOutsideArcAsync(World world, string encodedCursor, Guid artifactId)
    {
        using var scope = _fixture.BeginScope();
        var protector = scope.Resolve<IDataProtectionProvider>().CreateProtector(LegacyPlacementAdoptionCursor.ProtectorPurpose);
        LegacyPlacementAdoptionCursor.TryDecode(encodedCursor, world.ProfileId, protector, out var cursor).ShouldBeTrue();
        (await scope.Resolve<CodeSpaceDbContext>().LegacyPlacementAdoptionMember.AsNoTracking()
            .AnyAsync(value => value.ArcId == cursor.ArcId && value.WorkflowArtifactId == artifactId)).ShouldBeFalse(
            "membership is materialized by one SQL statement; later rows cannot enter regardless of pod timestamp or UUID order");
    }

    private async Task AssertArcCensusAsync(World world, string encodedCursor, int expectedMembers, LegacyPlacementAdoptionCursorMode expectedMode, bool positionMustBePositive)
    {
        using var scope = _fixture.BeginScope();
        var protector = scope.Resolve<IDataProtectionProvider>().CreateProtector(LegacyPlacementAdoptionCursor.ProtectorPurpose);
        LegacyPlacementAdoptionCursor.TryDecode(encodedCursor, world.ProfileId, protector, out var cursor).ShouldBeTrue();
        cursor.Mode.ShouldBe(expectedMode, "the closed census must survive the exact Evidence-to-Minting phase boundary under test");
        if (positionMustBePositive) cursor.Position.ShouldBeGreaterThan(0, "the late row is deliberately behind an already advanced Evidence position");
        var db = scope.Resolve<CodeSpaceDbContext>();
        var arc = await db.LegacyPlacementAdoptionArc.AsNoTracking().SingleAsync(value => value.Id == cursor.ArcId);
        arc.MemberCount.ShouldBe(expectedMembers, "the seal records the original closed population, not the later live table census");
        (await db.LegacyPlacementAdoptionMember.AsNoTracking().CountAsync(value => value.ArcId == cursor.ArcId)).ShouldBe(expectedMembers);
    }

    private async Task<RetainedArtifact> DeclareRetainedOffloadAsync(World world, string label)
    {
        var unit = Encoding.UTF8.GetBytes(label + "\n");
        var bytes = Enumerable.Range(0, ArtifactStoreConfig.InlineThresholdBytes / unit.Length + 2).SelectMany(_ => unit).ToArray();
        using var scope = _fixture.BeginScope();
        var request = new ArtifactRetentionWriteRequest(world.TeamId, bytes, "application/octet-stream",
            ArtifactRetentionClass.ArtifactManifestContent, "artifact_manifest", world.ActorId);
        var write = await scope.Resolve<IArtifactRetentionWriter>().PutDeclaredAsync(request, CancellationToken.None);
        write.Declared.ShouldBeTrue();

        var row = await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifact.AsNoTracking()
            .SingleAsync(value => value.Id == write.ArtifactId);
        var storageUrl = row.StorageUrl.ShouldNotBeNull("the payload is above the inline threshold");
        var path = new Uri(storageUrl).LocalPath;
        var root = Directory.GetParent(Directory.GetParent(Directory.GetParent(path)!.FullName)!.FullName)!.FullName;
        return new RetainedArtifact(write.ArtifactId, storageUrl, root);
    }

    private async Task<SidecarPlacement> SidecarAsync(World world, Guid workflowArtifactId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var artifact = await db.WorkflowArtifact.AsNoTracking().SingleAsync(value => value.Id == workflowArtifactId);
        artifact.StorageUrl.ShouldNotBeNull();
        artifact.CasArtifactObjectId.ShouldBeNull("sidecar adoption cannot relink the immutable row");
        var digest = Convert.FromHexString(artifact.Sha256);
        var placement = await db.ArtifactLocation.AsNoTracking()
            .Where(value => value.TeamId == world.TeamId && value.ArtifactObject.Digest == digest)
            .Select(value => new SidecarPlacement(value.ArtifactObjectId, value.Id))
            .SingleAsync();
        return placement;
    }

    private async Task AssertCollectedLegacyRowAsync(Guid workflowArtifactId, SidecarPlacement placement)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowArtifact.AsNoTracking().AnyAsync(value => value.Id == workflowArtifactId)).ShouldBeFalse(
            "an Available sidecar must not make the immutable local row unreapable");
        (await db.WorkflowArtifactRetention.AsNoTracking().AnyAsync(value => value.ArtifactId == workflowArtifactId)).ShouldBeFalse();
        (await db.ArtifactObject.AsNoTracking().AnyAsync(value => value.Id == placement.ArtifactObjectId)).ShouldBeTrue();
        var location = await db.ArtifactLocation.AsNoTracking().SingleAsync(value => value.Id == placement.LocationId);
        location.State.ShouldBe(ArtifactLocationState.Available,
            "local retention owns only the WorkflowArtifact's StorageUrl; the sidecar closes through its profile lifecycle later");
        (await db.ArtifactLocationEvent.AsNoTracking().CountAsync(value => value.ArtifactLocationId == placement.LocationId)).ShouldBe(1,
            "local retention must not append a routed delete event to an unrelated sidecar");
    }

    private async Task AgeDeclarationAsync(Guid artifactId, TimeSpan age)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE workflow_artifact DISABLE TRIGGER workflow_artifact_enforce_immutability");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE workflow_artifact SET created_at = clock_timestamp() - {age}::interval WHERE id = {artifactId}");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE workflow_artifact ENABLE TRIGGER workflow_artifact_enforce_immutability");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE workflow_artifact_retention
            SET declared_at = clock_timestamp() - {age}::interval,
                next_sweep_at = clock_timestamp() - {age}::interval,
                last_modified_at = clock_timestamp()
            WHERE artifact_id = {artifactId}
            """);
        await transaction.CommitAsync();
    }

    private async Task DeleteSourceAsync(Guid artifactId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SET LOCAL codespace.artifact_purge_allowed = on");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM workflow_artifact WHERE id = {artifactId}");
        await transaction.CommitAsync();
    }

    private async Task RetireProfileAsync(World world)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().StorageProfile
            .Where(value => value.TeamId == world.TeamId && value.Id == world.ProfileId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.State, StorageProfileState.Retired));
    }

    private async Task ExpireLiveArcAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE legacy_placement_adoption_arc
            SET expires_at = created_at + interval '1 microsecond',
                last_modified_at = clock_timestamp(),
                revision = revision + 1
            WHERE team_id = {teamId} AND state = 'Active'
            """);
    }

    private async Task<int> LiveArcCountAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().LegacyPlacementAdoptionArc.AsNoTracking()
            .CountAsync(value => value.TeamId == teamId && (value.State == LegacyPlacementAdoptionArcState.Active
                || value.State == LegacyPlacementAdoptionArcState.Cleaning));
    }

    private async Task<(Guid Id, long Revision, LegacyPlacementAdoptionArcState State, long DeclaredMembers, int RemainingMembers)> LiveArcSnapshotAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var arc = await db.LegacyPlacementAdoptionArc.AsNoTracking().SingleAsync(value => value.TeamId == teamId
            && (value.State == LegacyPlacementAdoptionArcState.Active || value.State == LegacyPlacementAdoptionArcState.Cleaning));
        var remaining = await db.LegacyPlacementAdoptionMember.AsNoTracking().CountAsync(value => value.ArcId == arc.Id);
        return (arc.Id, arc.Revision, arc.State, arc.MemberCount, remaining);
    }

    private async Task AgeQuarantineAsync(Guid artifactId, TimeSpan age)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE workflow_artifact_retention
            SET quarantined_at = clock_timestamp() - {age}::interval,
                next_sweep_at = clock_timestamp() - {age}::interval,
                last_modified_at = clock_timestamp()
            WHERE artifact_id = {artifactId} AND state = 'Quarantined'
            """);
    }

    private async Task DeferDeclarationAsync(Guid artifactId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE workflow_artifact_retention
            SET next_sweep_at = clock_timestamp() + interval '30 days', last_modified_at = clock_timestamp()
            WHERE artifact_id = {artifactId}
            """);
    }

    private async Task SweepUntilAsync(IArtifactRetentionReaper reaper, Guid artifactId, ArtifactRetentionState expected)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            await reaper.SweepAsync(CancellationToken.None);
            using var scope = _fixture.BeginScope();
            var state = await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifactRetention.AsNoTracking()
                .Where(value => value.ArtifactId == artifactId).Select(value => (ArtifactRetentionState?)value.State).SingleOrDefaultAsync();
            if (state == expected) return;
        }

        throw new Xunit.Sdk.XunitException($"Artifact {artifactId} did not reach retention state {expected} in 12 bounded sweeps.");
    }

    private async Task SweepUntilCollectedAsync(IArtifactRetentionReaper reaper, Guid artifactId)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            await reaper.SweepAsync(CancellationToken.None);
            using var scope = _fixture.BeginScope();
            if (!await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifact.AsNoTracking().AnyAsync(value => value.Id == artifactId)) return;
        }

        throw new Xunit.Sdk.XunitException($"Artifact {artifactId} was not collected in 12 bounded sweeps.");
    }

    private static void AddProfile(CodeSpaceDbContext db, World world, int revision, string root)
    {
        var now = DateTimeOffset.UtcNow;
        var config = JsonSerializer.Serialize(new { rootPath = Path.GetFullPath(root) });
        using var document = JsonDocument.Parse(config);
        if (revision == 1)
        {
            db.StorageProfile.Add(new StorageProfile
            {
                Id = world.ProfileId, TeamId = world.TeamId, StableName = $"legacy-adopt-{world.ProfileId:N}", CurrentRevision = 1,
                State = StorageProfileState.Active, CreatedDate = now, CreatedBy = world.ActorId, LastModifiedDate = now, LastModifiedBy = world.ActorId,
                Revisions =
                {
                    Revision(world, new RevisionSeed
                    {
                        RevisionId = world.RevisionId, Revision = revision, Config = config,
                        Configuration = document.RootElement, CreatedAt = now,
                    }),
                },
            });
            return;
        }

        db.StorageProfileRevision.Add(Revision(world, new RevisionSeed
        {
            RevisionId = Guid.NewGuid(), Revision = revision, Config = config,
            Configuration = document.RootElement, CreatedAt = now,
        }));
    }

    private static StorageProfileRevision Revision(World world, RevisionSeed seed) => new()
    {
        Id = seed.RevisionId, TeamId = world.TeamId, StorageProfileId = world.ProfileId, Revision = seed.Revision,
        ProviderTypeKey = LocalLegacyArtifactStorageDriverFactory.TypeKey, NonSecretConfigJson = seed.Config, CredentialRef = null,
        NamespaceFingerprint = StorageProfileRules.NamespaceFingerprint(LocalLegacyArtifactStorageDriverFactory.TypeKey, seed.Configuration),
        CreatedDate = seed.CreatedAt, CreatedBy = world.ActorId,
    };

    private async Task RepointAsync(World world, string root)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var profile = await db.StorageProfile.SingleAsync(value => value.Id == world.ProfileId);
        AddProfile(db, world, revision: 2, root);
        profile.CurrentRevision = 2;
        await db.SaveChangesAsync();
    }

    private async Task AdvanceRevisionAsync(World world) => await RepointAsync(world, world.RootPath);

    private async Task<(int Objects, int Locations, int Events)> CountsAsync(World world)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        return (await db.ArtifactObject.AsNoTracking().CountAsync(value => value.TeamId == world.TeamId),
            await db.ArtifactLocation.AsNoTracking().CountAsync(value => value.TeamId == world.TeamId),
            await db.ArtifactLocationEvent.AsNoTracking().CountAsync(value => value.TeamId == world.TeamId));
    }

    private string NewRoot(string label)
    {
        var root = Path.Combine(Path.GetTempPath(), $"codespace-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    public void Dispose()
    {
        foreach (var root in _roots.Where(Directory.Exists))
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class CountingBlobBackend : IArtifactBlobBackend, IArtifactBlobPurge
    {
        private readonly IArtifactBlobBackend _inner;
        private readonly IArtifactBlobPurge _purge;
        private readonly string _target;
        private int _targetDeleteCalls;

        public CountingBlobBackend(IArtifactBlobBackend inner, string target)
        {
            _inner = inner;
            _purge = inner.ShouldBeAssignableTo<IArtifactBlobPurge>();
            _target = target;
        }

        public int TargetDeleteCalls => Volatile.Read(ref _targetDeleteCalls);
        public Task<string> WriteAsync(string sha256, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) => _inner.WriteAsync(sha256, bytes, cancellationToken);
        public Task<bool> ExistsAsync(string storageUrl, CancellationToken cancellationToken) => _inner.ExistsAsync(storageUrl, cancellationToken);
        public Task<byte[]> ReadAsync(string storageUrl, CancellationToken cancellationToken) => _inner.ReadAsync(storageUrl, cancellationToken);
        public Task<ArtifactBlobRange> ReadRangeAsync(string storageUrl, long offset, int length, CancellationToken cancellationToken) => _inner.ReadRangeAsync(storageUrl, offset, length, cancellationToken);

        public Task<ArtifactBlobPurgeOutcome> DeleteAsync(string storageUrl, CancellationToken cancellationToken)
        {
            if (string.Equals(storageUrl, _target, StringComparison.Ordinal)) Interlocked.Increment(ref _targetDeleteCalls);
            return _purge.DeleteAsync(storageUrl, cancellationToken);
        }
    }

    private sealed class BlockingAfterDeleteBlobBackend : IArtifactBlobBackend, IArtifactBlobPurge
    {
        private readonly IArtifactBlobBackend _inner;
        private readonly IArtifactBlobPurge _purge;
        private readonly AsyncGate _gate;

        public BlockingAfterDeleteBlobBackend(IArtifactBlobBackend inner, AsyncGate gate)
        {
            _inner = inner;
            _purge = inner.ShouldBeAssignableTo<IArtifactBlobPurge>();
            _gate = gate;
        }

        public Task<string> WriteAsync(string sha256, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) => _inner.WriteAsync(sha256, bytes, cancellationToken);
        public Task<bool> ExistsAsync(string storageUrl, CancellationToken cancellationToken) => _inner.ExistsAsync(storageUrl, cancellationToken);
        public Task<byte[]> ReadAsync(string storageUrl, CancellationToken cancellationToken) => _inner.ReadAsync(storageUrl, cancellationToken);
        public Task<ArtifactBlobRange> ReadRangeAsync(string storageUrl, long offset, int length, CancellationToken cancellationToken) => _inner.ReadRangeAsync(storageUrl, offset, length, cancellationToken);

        public async Task<ArtifactBlobPurgeOutcome> DeleteAsync(string storageUrl, CancellationToken cancellationToken)
        {
            var result = await _purge.DeleteAsync(storageUrl, cancellationToken);
            _gate.Started.TrySetResult();
            await _gate.Continue.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class TrackingCasPurgeCoordinator : IArtifactCasPurgeCoordinator
    {
        private readonly IArtifactCasPurgeCoordinator _inner;
        private readonly Guid _targetObjectId;
        private int _targetCalls;

        public TrackingCasPurgeCoordinator(IArtifactCasPurgeCoordinator inner, Guid targetObjectId)
        {
            _inner = inner;
            _targetObjectId = targetObjectId;
        }

        public int TargetCalls => Volatile.Read(ref _targetCalls);

        public Task<ArtifactCasPurgeClaimResult> ClaimAsync(ArtifactCasPurgeRequest request, CancellationToken cancellationToken)
        {
            Count(request.ArtifactObjectId);
            return _inner.ClaimAsync(request, cancellationToken);
        }

        public Task<ArtifactCasPurgeResult> DeleteAsync(ArtifactCasPurgeClaim claim, CancellationToken cancellationToken)
        {
            Count(claim.ArtifactObjectId);
            return _inner.DeleteAsync(claim, cancellationToken);
        }

        public Task<ArtifactCasReleaseOutcome> ReleaseAsync(ArtifactCasPurgeClaim claim, ArtifactCasReleaseEvidence evidence, CancellationToken cancellationToken)
        {
            Count(claim.ArtifactObjectId);
            return _inner.ReleaseAsync(claim, evidence, cancellationToken);
        }

        public Task<ArtifactCasPurgeResult> PurgeAsync(ArtifactCasPurgeRequest request, CancellationToken cancellationToken)
        {
            Count(request.ArtifactObjectId);
            return _inner.PurgeAsync(request, cancellationToken);
        }

        public Task<ArtifactCasAbandonResult> AbandonAsync(ArtifactCasPurgeClaim claim, CancellationToken cancellationToken)
        {
            Count(claim.ArtifactObjectId);
            return _inner.AbandonAsync(claim, cancellationToken);
        }

        private void Count(Guid artifactObjectId)
        {
            if (artifactObjectId == _targetObjectId) Interlocked.Increment(ref _targetCalls);
        }
    }

    private sealed class DecoratingBroker : IStorageRuntimeDriverBroker
    {
        private readonly IStorageRuntimeDriverBroker _inner;
        private readonly Func<StorageRuntimeDriverLease, IArtifactStorageDriver> _decorate;

        public DecoratingBroker(IStorageRuntimeDriverBroker inner, Func<StorageRuntimeDriverLease, IArtifactStorageDriver> decorate)
        {
            _inner = inner;
            _decorate = decorate;
        }

        public async ValueTask<StorageRuntimeDriverResolution> OpenAsync(StorageRuntimeDriverRequest request, CancellationToken cancellationToken)
        {
            var resolution = await _inner.OpenAsync(request, cancellationToken);
            return resolution is StorageRuntimeDriverResolution.Ready ready
                ? new StorageRuntimeDriverResolution.Ready(new StorageRuntimeDriverLease(_decorate(ready.Lease)))
                : resolution;
        }
    }

    private sealed class FixedResolutionBroker : IStorageRuntimeDriverBroker
    {
        private readonly StorageRuntimeDriverResolution _resolution;

        public FixedResolutionBroker(StorageRuntimeDriverResolution resolution) => _resolution = resolution;

        public ValueTask<StorageRuntimeDriverResolution> OpenAsync(StorageRuntimeDriverRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(_resolution);
    }

    private sealed class CountingUnavailableBroker : IStorageRuntimeDriverBroker
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<StorageRuntimeDriverResolution> OpenAsync(StorageRuntimeDriverRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return ValueTask.FromResult<StorageRuntimeDriverResolution>(
                new StorageRuntimeDriverResolution.ProfileUnavailable(StorageRuntimeProfileFailureReason.ResolutionFailed));
        }
    }

    private abstract class DelegatingDriver : IArtifactStorageDriver
    {
        private readonly StorageRuntimeDriverLease _lease;

        protected DelegatingDriver(StorageRuntimeDriverLease lease) => _lease = lease;

        protected IArtifactStorageDriver Inner => _lease.Driver;
        public virtual StorageProviderCapabilities Capabilities => Inner.Capabilities;
        public virtual ValueTask<ArtifactStoragePutResult> PutAsync(ArtifactStoragePutRequest request, CancellationToken cancellationToken) => Inner.PutAsync(request, cancellationToken);
        public virtual ValueTask<ArtifactStorageHeadResult> HeadAsync(ArtifactStorageHeadRequest request, CancellationToken cancellationToken) => Inner.HeadAsync(request, cancellationToken);
        public virtual ValueTask<ArtifactStorageReadResult> OpenReadAsync(ArtifactStorageReadRequest request, CancellationToken cancellationToken) => Inner.OpenReadAsync(request, cancellationToken);
        public virtual ValueTask<ArtifactStorageDeleteResult> DeleteAsync(ArtifactStorageDeleteRequest request, CancellationToken cancellationToken) => Inner.DeleteAsync(request, cancellationToken);
        public virtual ValueTask<ArtifactStorageProbeResult> ProbeAsync(ArtifactStorageProbeRequest request, CancellationToken cancellationToken) => Inner.ProbeAsync(request, cancellationToken);
        public virtual ValueTask DisposeAsync() => _lease.DisposeAsync();
    }

    private sealed class UnreleasableDriver : DelegatingDriver
    {
        public UnreleasableDriver(StorageRuntimeDriverLease lease) : base(lease) { }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            throw new IOException("The provider handle could not be released cleanly.");
        }
    }

    private sealed class BlockingDisposeDriver : DelegatingDriver
    {
        private readonly AsyncGate _gate;

        public BlockingDisposeDriver(StorageRuntimeDriverLease lease, AsyncGate gate) : base(lease) => _gate = gate;

        public override async ValueTask DisposeAsync()
        {
            _gate.Started.TrySetResult();
            await _gate.Continue.Task;
            await base.DisposeAsync();
        }
    }

    private sealed class MissingCapabilityDriver : DelegatingDriver
    {
        private readonly StorageProviderCapabilities _removed;

        public MissingCapabilityDriver(StorageRuntimeDriverLease lease, StorageProviderCapabilities removed) : base(lease) => _removed = removed;

        public override StorageProviderCapabilities Capabilities => base.Capabilities & ~_removed;
    }

    private sealed class EndlessReadDriver : DelegatingDriver
    {
        private readonly EndlessAfterPrefix _stream;

        public EndlessReadDriver(StorageRuntimeDriverLease lease, EndlessAfterPrefix stream) : base(lease) => _stream = stream;

        public override async ValueTask<ArtifactStorageReadResult> OpenReadAsync(ArtifactStorageReadRequest request, CancellationToken cancellationToken)
        {
            var result = await base.OpenReadAsync(request, cancellationToken);
            if (!result.IsSuccess) return result;
            await result.Content!.DisposeAsync();
            return ArtifactStorageReadResult.Opened(_stream, result.ContentLength, result.TotalLength, result.Metadata!);
        }
    }

    private sealed class EphemeralMetadataFault
    {
        public EphemeralMetadataFault(bool changeETag, bool lieAboutHeadLength)
        {
            ChangeETag = changeETag;
            LieAboutHeadLength = lieAboutHeadLength;
        }

        public bool ChangeETag { get; }
        public bool LieAboutHeadLength { get; }
        public List<string?> ExpectedETags { get; } = [];
    }

    private sealed class EphemeralMetadataDriver : DelegatingDriver
    {
        private readonly EphemeralMetadataFault _fault;

        public EphemeralMetadataDriver(StorageRuntimeDriverLease lease, EphemeralMetadataFault fault) : base(lease) => _fault = fault;

        public override async ValueTask<ArtifactStorageHeadResult> HeadAsync(ArtifactStorageHeadRequest request, CancellationToken cancellationToken)
        {
            var result = await base.HeadAsync(request, cancellationToken);
            if (!result.IsSuccess) return result;
            var metadata = result.Metadata! with
            {
                ETag = _fault.ChangeETag ? "ephemeral-head" : result.Metadata!.ETag,
                Length = _fault.LieAboutHeadLength ? result.Metadata!.Length + 1 : result.Metadata!.Length,
            };
            return ArtifactStorageHeadResult.Found(metadata);
        }

        public override async ValueTask<ArtifactStorageReadResult> OpenReadAsync(ArtifactStorageReadRequest request, CancellationToken cancellationToken)
        {
            _fault.ExpectedETags.Add(request.ExpectedETag);
            var result = await base.OpenReadAsync(request, cancellationToken);
            if (!result.IsSuccess) return result;
            var metadata = result.Metadata! with { ETag = _fault.ChangeETag ? "ephemeral-read" : result.Metadata!.ETag };
            return ArtifactStorageReadResult.Opened(result.Content!, result.ContentLength, result.TotalLength, metadata);
        }
    }

    private sealed class BlockingRead
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class BlockingReadDriver : DelegatingDriver
    {
        private readonly BlockingRead _gate;

        public BlockingReadDriver(StorageRuntimeDriverLease lease, BlockingRead gate) : base(lease) => _gate = gate;

        public override async ValueTask<ArtifactStorageReadResult> OpenReadAsync(ArtifactStorageReadRequest request, CancellationToken cancellationToken)
        {
            var result = await base.OpenReadAsync(request, cancellationToken);
            _gate.Started.TrySetResult();
            try
            {
                await _gate.Continue.Task.WaitAsync(cancellationToken);
                return result;
            }
            catch
            {
                if (result.Content != null) await result.Content.DisposeAsync();
                throw;
            }
        }
    }

    private sealed class ThrowingLayoutCatalog : IStorageProviderModuleCatalog
    {
        private readonly IStorageProviderModuleCatalog _inner;
        private readonly ThrowingLayoutModule _legacy;

        public ThrowingLayoutCatalog(IStorageProviderModuleCatalog inner, string malformedLocator)
        {
            _inner = inner;
            _legacy = new ThrowingLayoutModule(inner.Require(LocalLegacyArtifactStorageDriverFactory.TypeKey), malformedLocator);
        }

        public IReadOnlyList<IStorageProviderModule> Modules => _inner.Modules
            .Select(module => module.TypeKey == _legacy.TypeKey ? _legacy : module).ToList();
        public IStorageProviderModule? Get(string typeKey) => typeKey == _legacy.TypeKey ? _legacy : _inner.Get(typeKey);
        public IStorageProviderModule Require(string typeKey) => Get(typeKey) ?? _inner.Require(typeKey);
    }

    private sealed class CapabilityMaskCatalog : IStorageProviderModuleCatalog
    {
        private readonly IStorageProviderModuleCatalog _inner;
        private readonly CapabilityMaskModule _legacy;

        public CapabilityMaskCatalog(IStorageProviderModuleCatalog inner, StorageProviderCapabilities removed)
        {
            _inner = inner;
            _legacy = new CapabilityMaskModule(inner.Require(LocalLegacyArtifactStorageDriverFactory.TypeKey), removed);
        }

        public IReadOnlyList<IStorageProviderModule> Modules => _inner.Modules
            .Select(module => module.TypeKey == _legacy.TypeKey ? _legacy : module).ToList();
        public IStorageProviderModule? Get(string typeKey) => typeKey == _legacy.TypeKey ? _legacy : _inner.Get(typeKey);
        public IStorageProviderModule Require(string typeKey) => Get(typeKey) ?? _inner.Require(typeKey);
    }

    private sealed class CapabilityMaskModule : IStorageProviderModule, IStorageProviderLegacyLayout
    {
        private readonly IStorageProviderModule _inner;
        private readonly IStorageProviderLegacyLayout _layout;
        private readonly StorageProviderCapabilities _removed;

        public CapabilityMaskModule(IStorageProviderModule inner, StorageProviderCapabilities removed)
        {
            _inner = inner;
            _layout = inner.ShouldBeAssignableTo<IStorageProviderLegacyLayout>();
            _removed = removed;
        }

        public string TypeKey => _inner.TypeKey;
        public string DisplayName => _inner.DisplayName;
        public JsonElement ConfigSchema => _inner.ConfigSchema;
        public JsonElement SecretSchema => _inner.SecretSchema;
        public StorageProviderCapabilities Capabilities => _inner.Capabilities & ~_removed;
        public Type FactoryType => _inner.FactoryType;
        public JsonElement GetNamespaceConfiguration(JsonElement nonSecretConfiguration) => _inner.GetNamespaceConfiguration(nonSecretConfiguration);
        public void EnsureConfigurationReadable(JsonElement nonSecretConfiguration) => _inner.EnsureConfigurationReadable(nonSecretConfiguration);
        public string? ResolveLegacyObjectKey(JsonElement nonSecretConfiguration, string sha256, string recordedLocator) =>
            _layout.ResolveLegacyObjectKey(nonSecretConfiguration, sha256, recordedLocator);
    }

    private sealed class ThrowingLayoutModule : IStorageProviderModule, IStorageProviderLegacyLayout
    {
        private readonly IStorageProviderModule _inner;
        private readonly IStorageProviderLegacyLayout _layout;
        private readonly string _malformedLocator;

        public ThrowingLayoutModule(IStorageProviderModule inner, string malformedLocator)
        {
            _inner = inner;
            _layout = inner.ShouldBeAssignableTo<IStorageProviderLegacyLayout>();
            _malformedLocator = malformedLocator;
        }

        public string TypeKey => _inner.TypeKey;
        public string DisplayName => _inner.DisplayName;
        public JsonElement ConfigSchema => _inner.ConfigSchema;
        public JsonElement SecretSchema => _inner.SecretSchema;
        public StorageProviderCapabilities Capabilities => _inner.Capabilities;
        public Type FactoryType => _inner.FactoryType;
        public JsonElement GetNamespaceConfiguration(JsonElement nonSecretConfiguration) => _inner.GetNamespaceConfiguration(nonSecretConfiguration);
        public void EnsureConfigurationReadable(JsonElement nonSecretConfiguration) => _inner.EnsureConfigurationReadable(nonSecretConfiguration);

        public string? ResolveLegacyObjectKey(JsonElement nonSecretConfiguration, string sha256, string recordedLocator)
        {
            if (string.Equals(recordedLocator, _malformedLocator, StringComparison.Ordinal))
                throw new FormatException("This immutable locator can never be parsed by the configured legacy layout.");
            return _layout.ResolveLegacyObjectKey(nonSecretConfiguration, sha256, recordedLocator);
        }
    }

    private sealed class EndlessAfterPrefix : Stream
    {
        private readonly byte[] _prefix;
        private long _position;

        public EndlessAfterPrefix(byte[] prefix) => _prefix = prefix;

        public long BytesRead => Interlocked.Read(ref _position);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            Fill(buffer);
            return buffer.Length;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Fill(buffer.Span);
            return ValueTask.FromResult(buffer.Length);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void Fill(Span<byte> destination)
        {
            var start = Interlocked.Read(ref _position);
            for (var index = 0; index < destination.Length; index++)
            {
                var position = start + index;
                destination[index] = position < _prefix.Length ? _prefix[(int)position] : (byte)0x5a;
            }
            Interlocked.Add(ref _position, destination.Length);
        }
    }

    private sealed class AsyncGate
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class SqlAttemptGate : DbCommandInterceptor
    {
        private readonly string[] _fragments;

        public SqlAttemptGate(params string[] fragments) => _fragments = fragments;

        public TaskCompletionSource<int> Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<Guid> ArtifactIds { get; private set; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (_fragments.All(fragment => command.CommandText.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            {
                ArtifactIds = command.Parameters.Cast<DbParameter>().SelectMany(parameter => parameter.Value is Guid[] values ? values : []).ToArray();
                Attempted.TrySetResult(((NpgsqlConnection)command.Connection!).ProcessID);
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class CapturedLegacyPage : DbCommandInterceptor
    {
        public string? CommandText { get; private set; }
        public List<(string Name, object? Value)> Parameters { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Capture(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Capture(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Capture(DbCommand command)
        {
            if (CommandText != null || !command.CommandText.Contains("FROM legacy_placement_adoption_member", StringComparison.OrdinalIgnoreCase)
                || !command.CommandText.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)
                || !command.CommandText.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)) return;

            CommandText = command.CommandText;
            foreach (DbParameter parameter in command.Parameters) Parameters.Add((parameter.ParameterName, parameter.Value));
        }
    }

    private static CandidateSeed Candidate(string content, LegacyShape shape = LegacyShape.Available) => new(content, shape);
    private sealed record CandidateSeed(string Content, LegacyShape Shape);
    private sealed record LegacyArtifact(Guid Id, byte[] ExpectedBytes, LegacyShape Shape, string StorageUrl);
    private sealed record RetainedArtifact(Guid ArtifactId, string StorageUrl, string RootPath);
    private sealed record SidecarPlacement(Guid ArtifactObjectId, Guid LocationId);
    private sealed class RevisionSeed
    {
        public required Guid RevisionId { get; init; }
        public required int Revision { get; init; }
        public required string Config { get; init; }
        public required JsonElement Configuration { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
    }

    private sealed class World
    {
        public required Guid TeamId { get; init; }
        public required Guid ActorId { get; init; }
        public required Guid ProfileId { get; init; }
        public required Guid RevisionId { get; init; }
        public required string RootPath { get; init; }
        public List<LegacyArtifact> Artifacts { get; } = [];
    }
    private enum LegacyShape { Available, Missing, SameLengthCorrupt }
}
