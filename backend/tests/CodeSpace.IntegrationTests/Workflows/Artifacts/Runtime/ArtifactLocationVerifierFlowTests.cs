using System.Security.Cryptography;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Storage;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts.Runtime;

/// <summary>
/// The verifier that makes Missing and Corrupt real states rather than schema documentation.
///
/// <para>Demotion is the dangerous direction — a location moved off Available stops being readable — so most of what
/// these tests pin is what the verifier REFUSES to do.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ArtifactLocationVerifierFlowTests : IAsyncLifetime
{
    /// <summary>Older than every verifier fixture in this suite, so a pass reaches this test's row by construction rather than luck.</summary>
    private static readonly TimeSpan Ancient = TimeSpan.FromDays(8000);

    private readonly PostgresFixture _fixture;
    private readonly List<Guid> _ancientLocations = [];
    private readonly List<string> _roots = [];

    public ArtifactLocationVerifierFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_object_that_is_still_there_is_confirmed_and_its_verified_at_moves_forward()
    {
        // verified_at was frozen at the write instant with an ORDER BY as its only consumer. Moving it is what makes
        // "when was this last known good" a real answer instead of "when was it written".
        var world = await SeedStoredArtifactAsync();
        var before = (await LocationAsync(world)).VerifiedAt.ShouldNotBeNull();

        var summary = await VerifyAsync();

        summary.Checked.ShouldBeGreaterThanOrEqualTo(1);
        var location = await LocationAsync(world);
        location.State.ShouldBe(ArtifactLocationState.Available);
        location.VerifiedAt.ShouldNotBeNull().ShouldBeGreaterThan(before);
    }

    [Fact]
    public async Task An_object_the_destination_no_longer_holds_is_marked_missing_and_stops_being_served()
    {
        // Before this, those bytes stayed Available forever and the loss surfaced only when a person opened the
        // artifact. The read must now fail the same way — but knowably, ahead of the person.
        var world = await SeedStoredArtifactAsync();
        DeleteStoredObject(world);

        await VerifyAsync();

        var location = await LocationAsync(world);
        location.State.ShouldBe(ArtifactLocationState.Missing);
        location.LastErrorCode.ShouldBe("location-object-missing");
        location.LastErrorMessage.ShouldNotBeNullOrWhiteSpace("a demoted location must say what the destination answered");

        using var scope = _fixture.BeginScope();
        var failure = await Should.ThrowAsync<ArtifactContentUnavailableException>(
            () => scope.Resolve<IArtifactStore>().GetBytesAsync(world.TeamId, world.ArtifactId, CancellationToken.None));
        failure.Kind.ShouldBe(ArtifactContentUnavailableKind.PhysicalObjectMissing, "a Missing location must leave the read path, not silently keep serving");
    }

    [Fact]
    public async Task An_object_replaced_by_something_else_is_marked_corrupt_rather_than_served()
    {
        // The key still resolves, so a reachability check would call this healthy. Whatever is there is not the
        // artifact, and serving it would hand a caller bytes that do not match what was recorded.
        var world = await SeedStoredArtifactAsync();
        OverwriteStoredObject(world, "a different object entirely");

        await VerifyAsync();

        var location = await LocationAsync(world);
        location.State.ShouldBe(ArtifactLocationState.Corrupt);
        location.LastErrorCode.ShouldBe("location-object-mismatch");
    }

    [Fact]
    public async Task A_destination_that_cannot_answer_is_asked_about_the_object_and_itself()
    {
        var world = await SeedDuePlacementAsync();
        var observed = new DestinationObservation(world);

        BreakDestination(world.Root);

        await VerifyAsync(observed);

        observed.Heads.ShouldBe(1, "the verifier must ask this exact object, or an unchanged row below would prove only that the sweep never selected it");
        observed.Probes.ShouldBe(1, "a failed object answer must be corroborated against this exact destination, not treated as a conclusion about the object");
    }

    [Fact]
    public async Task A_destination_that_cannot_answer_leaves_the_location_exactly_as_it_was()
    {
        // The property that makes this safe to run unattended. An outage, a throttle or a revoked key says something
        // about the REQUEST, not about the object — demoting on any of them would turn a transient blip into readable
        // bytes becoming unreadable, which is worse than the silence this replaces.
        var world = await SeedDuePlacementAsync();
        var before = await LocationAsync(world);

        BreakDestination(world.Root);

        await VerifyAsync();

        var after = await LocationAsync(world);
        after.State.ShouldBe(ArtifactLocationState.Available, "an unreachable destination is not evidence the object is gone");
        after.VerifiedAt.ShouldBe(before.VerifiedAt, "and its verified_at must NOT move, so the row stays visibly stale rather than looking freshly checked");
        after.Revision.ShouldBe(before.Revision);
    }

    [Fact]
    public async Task A_location_wrongly_demoted_by_a_destination_fault_comes_back_when_the_destination_answers_again()
    {
        // The safety valve that makes automated demotion acceptable at all. A verifier that only ever moves rows OUT of
        // Available is a one-way door: one bad pass over a flapping destination would permanently mark bytes that are
        // sitting right there as unreadable, with nothing that could ever re-examine them.
        var world = await SeedStoredArtifactAsync();
        var path = ObjectPath(world);
        var content = await File.ReadAllBytesAsync(path);
        var stashed = path + ".stashed";

        // Moved rather than rewritten, so the object comes back byte-identical AND metadata-identical — the shape of a
        // destination that was briefly serving an incomplete view of itself, not of someone editing the bytes.
        File.Move(path, stashed);
        await VerifyAsync();
        (await LocationAsync(world)).State.ShouldBe(ArtifactLocationState.Missing);

        File.Move(stashed, path);

        var location = await VerifyUntilAsync(world, ArtifactLocationState.Available);

        location.State.ShouldBe(ArtifactLocationState.Available, "the recorded object is present and matches, which is the same evidence its placement was accepted on");
        location.LastErrorCode.ShouldBeNull("a restored location must not keep advertising the error it no longer has");

        using var scope = _fixture.BeginScope();
        var bytes = await scope.Resolve<IArtifactStore>().GetBytesAsync(world.TeamId, world.ArtifactId, CancellationToken.None);
        bytes.Bytes.ShouldBe(content, "and it must be readable again, not merely marked healthy");
    }

    [Fact]
    public async Task A_corrupt_location_is_not_swept_back_into_circulation()
    {
        // Corrupt takes a positive disagreement to reach — an outage cannot fabricate a wrong-sized object — so unlike
        // Missing it carries no risk of being a false demotion, and re-reading it would only invite flapping.
        var world = await SeedStoredArtifactAsync();
        OverwriteStoredObject(world, "a different object entirely");
        await VerifyAsync();
        (await LocationAsync(world)).State.ShouldBe(ArtifactLocationState.Corrupt);
        var demoted = await LocationAsync(world);

        await VerifyAsync();

        var after = await LocationAsync(world);
        after.State.ShouldBe(ArtifactLocationState.Corrupt);
        after.Revision.ShouldBe(demoted.Revision, "a Corrupt row must not even be re-examined, let alone rewritten");
    }

    [Fact]
    public async Task A_destination_whose_whole_root_has_vanished_is_asked_about_the_object_and_itself()
    {
        var world = await SeedDuePlacementAsync();
        var observed = new DestinationObservation(world);

        Directory.Delete(world.Root, recursive: true);

        await VerifyAsync(observed);

        observed.Heads.ShouldBe(1, "the verifier must ask this exact object beneath the vanished root, or the safety assertion has no exercised fault behind it");
        observed.Probes.ShouldBe(1, "the verifier must ask this exact vanished destination for corroboration before deciding what the missing object answer means");
    }

    [Fact]
    public async Task A_destination_whose_whole_root_has_vanished_demotes_nothing()
    {
        // The scenario the liveness corroboration exists for, and the one it did not actually cover: an unmounted
        // volume, a detached disk, a deleted directory. Every object under it reads as absent, so without a truthful
        // answer about the DESTINATION every placement a team owns is demoted in a single pass — and a demotion of
        // bytes that are merely unreachable is far worse than the silence it replaced.
        //
        // The existing sibling test replaces the root with a FILE, which throws and is therefore easy to answer
        // honestly. A root that is simply GONE is the case a probe can silently paper over by creating it.
        var world = await SeedDuePlacementAsync();
        var before = await LocationAsync(world);

        Directory.Delete(world.Root, recursive: true);

        await VerifyAsync();

        var after = await LocationAsync(world);
        after.State.ShouldBe(ArtifactLocationState.Available, "a destination that is not there cannot testify that an object was deleted");
        after.VerifiedAt.ShouldBe(before.VerifiedAt, "and the row must stay visibly unchecked rather than looking freshly confirmed");
        Directory.Exists(world.Root).ShouldBeFalse("checking a destination must never be what creates it");
    }

    [Fact]
    public async Task A_write_probe_does_not_provision_the_destination_it_is_checking()
    {
        // The chain that made the two sweeps feed each other: the health sweep probes write-verified every fifteen
        // minutes and the write arm created the root, so a vanished mount was recreated empty, reported healthy on
        // the card, and then satisfied the verifier's destination-liveness check — which demoted every placement
        // underneath it, a hundred an hour, forever. A probe must never make the thing it is checking.
        var world = await SeedDuePlacementAsync();

        Directory.Delete(world.Root, recursive: true);
        var probe = await WriteProbeAsync(world);

        Directory.Exists(world.Root).ShouldBeFalse("a write-verified probe must not provision a destination the operator never provisioned");
        probe.Status.ShouldBe(StorageProfileProbeStatusValue.Unavailable, "a vanished destination is unavailable; recreating it would invent a healthy answer");
    }

    [Fact]
    public async Task A_provisioning_probe_can_create_the_destination_it_is_adopting()
    {
        var world = await SeedDuePlacementAsync();

        Directory.Delete(world.Root, recursive: true);

        var probe = await ProvisioningProbeAsync(world);

        probe.Status.ShouldBe(StorageProfileProbeStatusValue.Available, "an explicit provisioning action still has to make a writable destination ready");
        Directory.Exists(world.Root).ShouldBeTrue("provisioning is an operator request to create the destination, not a health observation");
    }

    [Fact]
    public async Task A_write_probe_scenario_is_asked_about_the_object_and_destination_by_the_verifier()
    {
        var world = await SeedDuePlacementAsync();
        var observed = new DestinationObservation(world);

        Directory.Delete(world.Root, recursive: true);
        await WriteProbeAsync(world);

        await VerifyAsync(observed);

        observed.Heads.ShouldBe(1, "the verifier must ask this exact placement after the health probe, or the unchanged state would prove nothing about their interaction");
        observed.Probes.ShouldBe(1, "the verifier must obtain its own destination answer rather than inheriting the earlier health probe's result");
    }

    [Fact]
    public async Task A_write_probe_does_not_hand_the_verifier_the_corroboration_it_needs_to_demote()
    {
        var world = await SeedDuePlacementAsync();
        var before = await LocationAsync(world);

        Directory.Delete(world.Root, recursive: true);
        await WriteProbeAsync(world);

        await VerifyAsync();

        var after = await LocationAsync(world);
        after.State.ShouldBe(ArtifactLocationState.Available, "with the destination honestly unavailable, an absent object is not evidence the object was deleted");
        after.VerifiedAt.ShouldBe(before.VerifiedAt, "and the row stays visibly unchecked rather than freshly confirmed");
    }

    [Fact]
    public async Task An_operators_read_probe_of_a_destination_that_admits_no_write_does_not_provision_it()
    {
        // The same door, from the other side. A profile that admits no write is now probed for a READ, so that the
        // destination its stored objects still live on is actually contacted rather than answered from
        // storage_profile.state — and the operator's Test action asks to provision. Honouring that on a read is
        // provisioning-by-probe again: it recreates the vanished root, which is the corroboration the verifier
        // demotes every placement underneath on.
        var world = await SeedDuePlacementAsync();
        await StopAdmittingWritesAsync(world);

        Directory.Delete(world.Root, recursive: true);

        var probe = await OperatorReadProbeAsync(world);

        Directory.Exists(world.Root).ShouldBeFalse("only a probe about to prove it can WRITE may create the destination it is checking");
        probe.Status.ShouldBe(StorageProfileProbeStatusValue.Unavailable, "a destination that is not there is unavailable, and saying so is the whole job of a probe");
    }

    [Fact]
    public async Task An_operator_read_probe_scenario_is_asked_about_the_object_and_destination_by_the_verifier()
    {
        var world = await SeedDuePlacementAsync();
        await StopAdmittingWritesAsync(world);
        var observed = new DestinationObservation(world);

        Directory.Delete(world.Root, recursive: true);
        await OperatorReadProbeAsync(world);

        await VerifyAsync(observed);

        observed.Heads.ShouldBe(1, "the verifier must ask this exact placement after the operator's read probe, or an unchanged row could be an unselected one");
        observed.Probes.ShouldBe(1, "the verifier must obtain its own destination answer and never reuse the operator probe as corroboration");
    }

    [Fact]
    public async Task An_operators_read_probe_does_not_hand_the_verifier_the_corroboration_it_needs_to_demote()
    {
        var world = await SeedDuePlacementAsync();
        await StopAdmittingWritesAsync(world);
        var before = await LocationAsync(world);

        Directory.Delete(world.Root, recursive: true);
        await OperatorReadProbeAsync(world);

        await VerifyAsync();

        var after = await LocationAsync(world);
        after.State.ShouldBe(ArtifactLocationState.Available, "with the destination honestly unavailable, an absent object is not evidence the object was deleted");
        after.VerifiedAt.ShouldBe(before.VerifiedAt, "and the row stays visibly unchecked rather than freshly confirmed");
    }

    /// <summary>Runs the provisioning probe an operator's adopt / activate / test action runs.</summary>
    private Task<StorageProfileProbeResult> ProvisioningProbeAsync(DuePlacement world) => ProbeAsync(world, initialize: true, verifyWriteAccess: true);

    /// <summary>Runs the write-verified probe the health sweep runs, against this test's own destination.</summary>
    private Task<StorageProfileProbeResult> WriteProbeAsync(DuePlacement world) => ProbeAsync(world, initialize: false, verifyWriteAccess: true);

    /// <summary>Runs the operator's Test action with write verification switched off — it still asks to provision.</summary>
    private Task<StorageProfileProbeResult> OperatorReadProbeAsync(DuePlacement world) => ProbeAsync(world, initialize: true, verifyWriteAccess: false);

    private async Task<StorageProfileProbeResult> ProbeAsync(DuePlacement world, bool initialize, bool verifyWriteAccess)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<IStorageProfileProbeService>().ProbeAsync(
            new StorageProfileProbeRequest(world.TeamId, world.ProfileId, ProfileRevision: null, verifyWriteAccess, initialize), CancellationToken.None);
    }

    /// <summary>Disables the profile. That unbinds no route and removes no bytes — it only stops admitting new writes.</summary>
    private async Task StopAdmittingWritesAsync(DuePlacement world)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE storage_profile SET state = {StorageProfileState.Disabled.ToString()} WHERE id = {world.ProfileId}");
    }

    // ─── World + helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Sweeps until THIS test's location reaches <paramref name="state"/>.
    ///
    /// <para>The sweep is deployment-wide and bounded, and the recovery half of its batch is deliberately the smaller
    /// half, so a Missing row left behind by any earlier test competes for the same slots. Asserting after one pass
    /// would be asserting that this row won a race it has no reason to win — and would get harder to satisfy as the
    /// suite grows.</para>
    /// </summary>
    private async Task<ArtifactLocation> VerifyUntilAsync(StoredArtifact world, ArtifactLocationState state, TimeSpan? within = null)
    {
        var deadline = DateTimeOffset.UtcNow + (within ?? TimeSpan.FromSeconds(10));
        ArtifactLocation? seen = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await VerifyAsync();
            seen = await LocationAsync(world);

            if (seen.State == state) return seen;
        }

        throw new Xunit.Sdk.XunitException(
            $"The location for artifact {world.ArtifactId} never reached {state} (last seen {seen?.State.ToString() ?? "absent"}, "
            + $"last error {seen?.LastErrorCode ?? "none"}). The sweep is deployment-wide and its recovery share is bounded, "
            + "so check whether earlier tests left more Missing rows than that share holds.");
    }

    private async Task<ArtifactLocationVerificationSummary> VerifyAsync()
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<IArtifactLocationVerifier>().VerifyStaleAsync(100, CancellationToken.None);
    }

    /// <summary>Runs the real verifier with an observer around only this test's exact destination.</summary>
    private async Task<ArtifactLocationVerificationSummary> VerifyAsync(DestinationObservation observation)
    {
        using var scope = _fixture.BeginScope(builder => builder
            .Register<IStorageRuntimeDriverBroker>(context => new ObservingBroker(context.Resolve<StorageRuntimeDriverBroker>(), observation))
            .InstancePerLifetimeScope());

        return await scope.Resolve<IArtifactLocationVerifier>().VerifyStaleAsync(100, CancellationToken.None);
    }

    private async Task<ArtifactLocation> LocationAsync(StoredArtifact world)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking()
            .SingleAsync(location => location.TeamId == world.TeamId);
    }

    private async Task<ArtifactLocation> LocationAsync(DuePlacement world)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking()
            .SingleAsync(location => location.Id == world.LocationId);
    }

    private static string ObjectPath(StoredArtifact world) => Directory.GetFiles(world.Root, "*", SearchOption.AllDirectories).Single();

    private static void DeleteStoredObject(StoredArtifact world) => File.Delete(ObjectPath(world));

    private static void OverwriteStoredObject(StoredArtifact world, string content) => File.WriteAllText(ObjectPath(world), content);

    /// <summary>Replaces the destination root with a FILE, so the driver cannot open it at all — a transport fault, not an answer about the object.</summary>
    private static void BreakDestination(string root)
    {
        Directory.Delete(root, recursive: true);
        File.WriteAllText(root, "not a directory");
    }

    private async Task<StoredArtifact> SeedStoredArtifactAsync()
    {
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var destination = await RoutedArtifactSeed.RouteTeamAsync(_fixture, teamId, actorId);
        var artifactId = await RoutedArtifactSeed.WriteRoutedAsync(_fixture, teamId, "verifiable bytes", "application/octet-stream");

        _roots.Add(destination.Root);
        destination.ObjectCount.ShouldBe(1, "the object must physically exist for a verification test to mean anything");

        return new StoredArtifact(teamId, artifactId, destination.Root);
    }

    /// <summary>
    /// Creates an ordinary Available placement at its first revision, with the object physically present at the exact
    /// key the row names. Its initial observation is ancient at INSERT time: changing it afterwards is both less honest
    /// and rejected by the durable-identity schema, while a freshly written row sorts behind every location already owed
    /// an answer and makes a negative assertion pass without ever being selected.
    /// </summary>
    private async Task<DuePlacement> SeedDuePlacementAsync()
    {
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var destination = await RoutedArtifactSeed.RouteTeamAsync(_fixture, teamId, actorId);
        var observed = DateTimeOffset.UtcNow - Ancient;
        var bytes = System.Text.Encoding.UTF8.GetBytes("verifier due bytes");
        var digest = SHA256.HashData(bytes);
        var objectId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        var objectKey = $"verifier-controls/{locationId:N}";
        var path = Path.Combine([destination.Root, .. objectKey.Split('/', StringSplitOptions.RemoveEmptyEntries)]);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var revision = await db.StorageProfileRevision.AsNoTracking()
            .Where(value => value.StorageProfileId == destination.ProfileId)
            .Select(value => new { value.Id, value.Revision })
            .SingleAsync();
        var location = new ArtifactLocation
        {
            Id = locationId, TeamId = teamId, ArtifactObjectId = objectId, StorageProfileRevisionId = revision.Id,
            Locator = objectKey, ObjectKey = objectKey, ProviderChecksumAlgorithm = "Sha256", ProviderChecksum = digest,
            ObservedSizeBytes = bytes.LongLength, State = ArtifactLocationState.Available, Revision = 1, VerifiedAt = observed,
            CreatedDate = observed, CreatedBy = actorId, LastModifiedDate = observed, LastModifiedBy = actorId,
        };

        db.ArtifactObject.Add(new ArtifactObject
        {
            Id = objectId, TeamId = teamId, DigestAlgorithm = ArtifactDigestAlgorithm.Sha256, Digest = digest,
            SizeBytes = bytes.LongLength, CreatedDate = observed, CreatedBy = actorId,
        });
        db.ArtifactLocation.Add(location);
        db.ArtifactLocationEvent.Add(Snapshot(location, ArtifactLocationEventType.Verified, observed, actorId));
        await db.SaveChangesAsync();

        _ancientLocations.Add(locationId);
        _roots.Add(destination.Root);

        return new DuePlacement(teamId, locationId, destination.ProfileId, revision.Revision, objectKey, destination.Root);
    }

    private static ArtifactLocationEvent Snapshot(ArtifactLocation location, ArtifactLocationEventType eventType, DateTimeOffset observedAt, Guid actorId) => new()
    {
        Id = Guid.NewGuid(), TeamId = location.TeamId, ArtifactLocationId = location.Id, Revision = location.Revision,
        EventType = eventType, State = location.State, ObservedAt = observedAt,
        ProviderObjectVersion = location.ProviderObjectVersion, ProviderETag = location.ProviderETag,
        ProviderChecksumAlgorithm = location.ProviderChecksumAlgorithm, ProviderChecksum = location.ProviderChecksum,
        ObservedSizeBytes = location.ObservedSizeBytes, VerifiedAt = location.VerifiedAt,
        ContentEncoding = location.ContentEncoding, EncryptionKeyVersion = location.EncryptionKeyVersion,
        ErrorCode = location.LastErrorCode, ErrorMessage = location.LastErrorMessage, DetailsJson = "{}", CreatedBy = actorId,
    };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var rows = await db.ArtifactLocation.Where(location => _ancientLocations.Contains(location.Id)).ToListAsync();
            var now = DateTimeOffset.UtcNow;

            foreach (var row in rows)
            {
                row.State = ArtifactLocationState.Deleted;
                row.Revision++;
                row.LastModifiedDate = now;
                row.LastModifiedBy = SystemUsers.SeederId;
                db.ArtifactLocationEvent.Add(Snapshot(row, ArtifactLocationEventType.StateChanged, now, SystemUsers.SeederId));
            }

            await db.SaveChangesAsync();
        }

        foreach (var root in _roots)
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                else if (File.Exists(root)) File.Delete(root);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record StoredArtifact(Guid TeamId, Guid ArtifactId, string Root);

    private sealed record DuePlacement(Guid TeamId, Guid LocationId, Guid ProfileId, int ProfileRevision, string ObjectKey, string Root);

    /// <summary>What the verifier asked of this test's exact destination, never a deployment-wide tally.</summary>
    private sealed class DestinationObservation
    {
        private readonly DuePlacement _placement;
        private int _heads;
        private int _probes;

        public DestinationObservation(DuePlacement placement) => _placement = placement;

        public int Heads => Volatile.Read(ref _heads);

        public int Probes => Volatile.Read(ref _probes);

        public bool Matches(StorageRuntimeDriverRequest request) => request.TeamId == _placement.TeamId
            && request.ProfileId == _placement.ProfileId && request.ProfileRevision == _placement.ProfileRevision;

        public void Headed(string objectKey)
        {
            if (string.Equals(objectKey, _placement.ObjectKey, StringComparison.Ordinal)) Interlocked.Increment(ref _heads);
        }

        public void Probed() => Interlocked.Increment(ref _probes);
    }

    /// <summary>Hands out the real driver, wrapped only for the exact destination this test owns.</summary>
    private sealed class ObservingBroker : IStorageRuntimeDriverBroker
    {
        private readonly IStorageRuntimeDriverBroker _inner;
        private readonly DestinationObservation _observation;

        public ObservingBroker(IStorageRuntimeDriverBroker inner, DestinationObservation observation)
        {
            _inner = inner;
            _observation = observation;
        }

        public async ValueTask<StorageRuntimeDriverResolution> OpenAsync(StorageRuntimeDriverRequest request, CancellationToken cancellationToken)
        {
            var resolution = await _inner.OpenAsync(request, cancellationToken);

            return _observation.Matches(request) && resolution is StorageRuntimeDriverResolution.Ready ready
                ? new StorageRuntimeDriverResolution.Ready(new StorageRuntimeDriverLease(new ObservingDriver(ready.Lease, _observation)))
                : resolution;
        }
    }

    /// <summary>Records HEAD and Probe, forwards every operation, and releases the original lease exactly once.</summary>
    private sealed class ObservingDriver : IArtifactStorageDriver
    {
        private readonly StorageRuntimeDriverLease _lease;
        private readonly DestinationObservation _observation;

        public ObservingDriver(StorageRuntimeDriverLease lease, DestinationObservation observation)
        {
            _lease = lease;
            _observation = observation;
        }

        public StorageProviderCapabilities Capabilities => _lease.Driver.Capabilities;

        public ValueTask<ArtifactStorageHeadResult> HeadAsync(ArtifactStorageHeadRequest request, CancellationToken cancellationToken)
        {
            _observation.Headed(request.ObjectKey);

            return _lease.Driver.HeadAsync(request, cancellationToken);
        }

        public ValueTask<ArtifactStorageProbeResult> ProbeAsync(ArtifactStorageProbeRequest request, CancellationToken cancellationToken)
        {
            _observation.Probed();

            return _lease.Driver.ProbeAsync(request, cancellationToken);
        }

        public ValueTask<ArtifactStoragePutResult> PutAsync(ArtifactStoragePutRequest request, CancellationToken cancellationToken) => _lease.Driver.PutAsync(request, cancellationToken);

        public ValueTask<ArtifactStorageReadResult> OpenReadAsync(ArtifactStorageReadRequest request, CancellationToken cancellationToken) => _lease.Driver.OpenReadAsync(request, cancellationToken);

        public ValueTask<ArtifactStorageDeleteResult> DeleteAsync(ArtifactStorageDeleteRequest request, CancellationToken cancellationToken) => _lease.Driver.DeleteAsync(request, cancellationToken);

        public ValueTask DisposeAsync() => _lease.DisposeAsync();
    }
}
