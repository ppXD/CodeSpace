using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles.Exceptions;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts;

/// <summary>
/// The operator's way out of a destination that is gone, end to end.
///
/// <para>The coordinator could already settle one placement on proof; without this nobody could reach it. A
/// capability with no caller is not a capability — the records stayed unreachable and the profile stayed
/// un-retirable, which is the dead end this whole arc exists to close.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ProfileAbandonmentFlowTests : IDisposable
{
    /// <summary>The service's own cap, so cleanup can close everything any one test seeded in a single pass.</summary>
    private const int MaxDrainableBatch = 200;

    private readonly PostgresFixture _fixture;
    private readonly List<string> _roots = [];
    private readonly List<RoutedWorld> _worlds = [];

    public ProfileAbandonmentFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_unmounted_volume_closes_no_record_and_stops_the_pass_rather_than_asking_on()
    {
        // The one answer that must never be believed per-object. The mount is gone, so the destination reports every
        // key Missing while the bytes sit untouched one directory over. Closing those records nulls the checksum, the
        // size and the ETag, and nothing in the system could afterwards say what the bytes were or where they are.
        var world = await SeedRoutedProfileAsync(placements: 50);
        var hidden = Unmount(world);

        var summary = await AbandonAsync(world, batchSize: 50);

        summary.Abandoned.ShouldBe(0, "a destination that cannot answer for itself may not close a single record");
        summary.Unanswered.ShouldBe(summary.Examined);
        summary.Examined.ShouldBeLessThan(50, "one answer repeated across unrelated objects is about the destination; the pass must stop asking");
        summary.StoppedBy.ShouldBe(nameof(ArtifactCasProblemCode.ProviderUnavailableTransient),
            "a pass that stopped early has to name why, or a short Examined is indistinguishable from a small profile");
        (await PlacementStatesAsync(world)).ShouldAllBe(state => state == ArtifactLocationState.Available);
        Directory.GetFiles(Path.Combine(hidden, "objects"), "*", SearchOption.AllDirectories).Length
            .ShouldBe(50, "the bytes never went anywhere — only the mount did");
    }

    [Theory]
    [InlineData(5, 20, null)]                                                        // twenty placements failing for five unrelated reasons are twenty objects with their own problems
    [InlineData(1, 5, nameof(ArtifactCasProblemCode.ProviderTimeout))]               // one answer for a quarter of the batch is the destination talking, not the objects
    public async Task A_pass_stops_only_when_one_answer_comes_back_for_much_of_the_batch(int distinctAnswers, int expectedExamined, string? expectedStop)
    {
        // The breaker generalizes from UNIFORMITY, never from failure. Stopping on a genuinely mixed batch would
        // report a destination fault nothing observed and leave placements unasked that nobody asked it to skip.
        var world = await SeedRoutedProfileAsync(placements: 20);
        var destination = new ScriptedDestination(MixedAnswers.Take(distinctAnswers).ToArray());

        var summary = await AbandonAsync(world, batchSize: 20, destination);

        summary.StoppedBy.ShouldBe(expectedStop);
        summary.Examined.ShouldBe(expectedExamined);
        summary.Unanswered.ShouldBe(expectedExamined, "a refusal is never a closed record, however many of them arrive");
        summary.Abandoned.ShouldBe(0);
        destination.Asked.ShouldBe(expectedExamined, "the pass must stop ASKING, not merely stop counting");
    }

    [Fact]
    public async Task An_operator_drains_an_emptied_destination_and_can_then_retire_the_profile()
    {
        // The journey the two 409s used to describe and no code implemented: the objects are gone, the destination
        // says so about itself as well as about them, the records are closed on that answer, and only then does the
        // irreversible step become available. The corroboration must not break the operation it guards.
        var world = await SeedRoutedProfileAsync(placements: 3);
        DeleteEveryObject(world);

        var summary = await AbandonAsync(world, batchSize: 50);

        summary.Examined.ShouldBe(3);
        summary.Abandoned.ShouldBe(3);
        summary.StillServed.ShouldBe(0);
        summary.Remaining.ShouldBe(0, "draining to zero must mean draining to what actually unblocks retirement");

        await RetireAsync(world);
        (await StateAsync(world)).ShouldBe(StorageProfileState.Retired);
    }

    [Fact]
    public async Task A_destination_whose_own_probe_answers_it_is_gone_for_good_still_drains_to_zero()
    {
        // The deleted bucket, which is the destination this whole operation exists for. It cannot serve the object
        // and it cannot serve ITSELF, and both refusals are durable. Demanding a HEALTHY probe as corroboration made
        // this exit unreachable: the one operator who genuinely cannot get the bytes back could never drain the
        // profile, and therefore could never retire it.
        var world = await SeedRoutedProfileAsync(placements: 3);

        var summary = await AbandonAsync(world, batchSize: 50, ScriptedProvider.GoneForGood());

        summary.Abandoned.ShouldBe(3);
        summary.StoppedBy.ShouldBeNull("nothing stopped the pass — the destination answered, and its answer was conclusive");
        summary.Remaining.ShouldBe(0, "a destination that says it is gone for good is exactly what abandonment is for");
        (await PlacementStatesAsync(world)).ShouldAllBe(state => state == ArtifactLocationState.Purged);
    }

    [Theory]
    [InlineData(ArtifactStorageErrorCode.Unauthorized)] // the access key was rotated out from under the profile
    [InlineData(ArtifactStorageErrorCode.Forbidden)]    // the policy that granted the key its access was withdrawn
    public async Task A_credential_that_lost_its_permission_corroborates_nothing_and_closes_no_record(ArtifactStorageErrorCode refusal)
    {
        // The guard's own case, re-entering through the widening that admits a durably-gone bucket. A refused
        // credential is durable, and about the CREDENTIAL: it says nothing whatever about whether the objects are
        // there, which is precisely what a namespace you can no longer see also says. Every per-object HEAD agrees
        // that the object is gone for the very same reason — one refused key answering both questions — so the
        // per-object answer cannot be its own corroboration.
        var world = await SeedRoutedProfileAsync(placements: 3);

        var summary = await AbandonAsync(world, batchSize: 50, ScriptedProvider.WithARefusedKey(refusal));

        summary.Abandoned.ShouldBe(0, "the bytes are intact behind a permission somebody can grant back");
        summary.Examined.ShouldBe(3);
        summary.Unanswered.ShouldBe(3, "an answer nothing corroborated is no answer, and the pass has to report it as one");
        summary.Remaining.ShouldBe(3, "nothing was released, so the profile is exactly as un-retirable as before the pass");
        (await PlacementStatesAsync(world)).ShouldAllBe(state => state == ArtifactLocationState.Available,
            "every claim goes back where it was found; closing these rows would null the checksum, the size and the ETag of readable bytes");
    }

    [Fact]
    public async Task A_destination_this_worker_could_not_open_closes_no_record_however_gone_the_objects_look()
    {
        // The same harm one stage earlier. The provider module is absent from THIS worker's image, which the catalog
        // answers from its own registry — the destination is never contacted, so it never said anything. Every
        // object really is gone from disk here, so had the module been present each HEAD would have answered Missing
        // against a probe that answers for itself, and every record would close on THAT. The module's absence is not
        // that answer, and one worker deployed without it must not close records placed through the profile.
        var world = await SeedRoutedProfileAsync(placements: 20);
        DeleteEveryObject(world);

        var summary = await AbandonAsync(world, batchSize: 20, new MissingProviderModule());

        summary.Abandoned.ShouldBe(0, "a destination that never opened never spoke, and only the destination may close a record");
        summary.Unanswered.ShouldBe(summary.Examined, "the honest report is that the pass got no answer at all");
        summary.Remaining.ShouldBe(20, "nothing was released, so the profile is exactly as un-retirable as before the pass");
        summary.StoppedBy.ShouldBe(nameof(ArtifactCasProblemCode.ProviderUnavailable),
            "the refusal is kept as it came so an operator is told WHY the pass got nowhere, and not merely that it did");
        (await PlacementStatesAsync(world)).ShouldAllBe(state => state == ArtifactLocationState.Available,
            "closing these rows nulls the checksum, the size and the ETag on nothing but a deployment mistake");
    }

    [Fact]
    public async Task A_pass_reaches_the_placements_ordered_behind_ones_that_always_refuse()
    {
        // Head-of-line starvation. The breaker stops the pass, and a batch that always starts at the same place
        // stops at the same placements every time — so everything ordered behind a handful of persistent refusers
        // is never examined again and Remaining never falls. A refusal must cost that placement its turn, not the
        // whole rest of the profile.
        var world = await SeedRoutedProfileAsync(placements: 20);
        var refused = await FirstKeysAsync(world, count: 5);

        var first = await AbandonAsync(world, batchSize: 20, ScriptedProvider.LosingEverythingExcept(refused));
        var second = await AbandonAsync(world, batchSize: 20, ScriptedProvider.LosingEverythingExcept(refused));

        first.StoppedBy.ShouldBe(nameof(ArtifactCasProblemCode.Throttled), "one answer for a quarter of the batch still stops the pass — that part is the point");
        (first.Abandoned + second.Abandoned).ShouldBe(15, "every placement that was not itself refusing has to be reachable across successive passes");
        second.Remaining.ShouldBe(refused.Count, "only the placements that actually refuse may still be held");
    }

    [Fact]
    public async Task A_batch_of_claims_that_all_went_stale_is_not_a_destination_talking()
    {
        // Every one of these refusals was decided before the destination was asked anything: the claim was taken and
        // then lost to another worker. Two drains racing each other agree on that answer for every row they race
        // over, and reading the agreement as a broken destination stops a pass on evidence no destination produced.
        var world = await SeedRoutedProfileAsync(placements: 20);
        var destination = new ScriptedDestination(ArtifactCasProblemCode.StaleWorker);

        var summary = await AbandonAsync(world, batchSize: 20, destination);

        summary.StoppedBy.ShouldBeNull("a uniform answer is evidence about a destination only if the destination produced it");
        summary.Examined.ShouldBe(20);
        destination.Asked.ShouldBe(20, "the pass had no reason to stop asking");
    }

    [Fact]
    public async Task A_destination_that_still_serves_its_objects_is_not_drained_and_the_profile_stays_blocked()
    {
        // The refusal that makes the operation safe to expose at all. An operator who runs this against a healthy
        // destination gets nothing closed and nothing lost — the answer comes from the destination, not from them.
        var world = await SeedRoutedProfileAsync(placements: 2);

        var summary = await AbandonAsync(world, batchSize: 50);

        summary.StillServed.ShouldBe(2);
        summary.Abandoned.ShouldBe(0);
        summary.Remaining.ShouldBe(2);
        await Should.ThrowAsync<StorageProfileConflictException>(() => RetireAsync(world));
    }

    [Fact]
    public async Task A_pass_is_bounded_and_says_how_much_is_left_so_it_can_be_resumed()
    {
        // Bounded and repeatable rather than one long job: a call that dies halfway leaves everything it did not
        // reach exactly as it was, so resumption is a property of the ledger rather than of a job row.
        var world = await SeedRoutedProfileAsync(placements: 3);
        DeleteEveryObject(world);

        var first = await AbandonAsync(world, batchSize: 2);

        first.Abandoned.ShouldBe(2);
        first.Remaining.ShouldBe(1);

        var second = await AbandonAsync(world, batchSize: 2);

        second.Abandoned.ShouldBe(1);
        second.Remaining.ShouldBe(0);
    }

    [Fact]
    public async Task An_orphaned_claim_on_a_serving_destination_is_released_to_the_state_it_rests_in()
    {
        // A worker that dies between claiming and deleting leaves the marker on the row. The drain re-claims it, the
        // destination serves the object, and the release has to put the row back where it was BEFORE any claim:
        // writing the marker back records a live object as mid-delete forever, which is the one state it cannot leave.
        var world = await SeedRoutedProfileAsync(placements: 1);
        var orphaned = await OrphanOneClaimAsync(world);

        (await LocationStateAsync(orphaned)).ShouldBe(ArtifactLocationState.Deleting, "a fixture that did not start in the marker would prove nothing");

        var summary = await AbandonAsync(world, batchSize: 50);

        summary.StillServed.ShouldBe(1);
        summary.Abandoned.ShouldBe(0);
        (await LocationStateAsync(orphaned)).ShouldBe(ArtifactLocationState.Available,
            "a serving HEAD is the one thing that may restore Available, and it was taken under this claim");
    }

    [Fact]
    public async Task An_orphaned_claim_on_a_destination_that_lost_the_object_is_closed_rather_than_released()
    {
        // The same orphan, where the destination answers for itself and has lost the object. Abandonment is its exit,
        // and it is the only one: the claim was taken from the marker, so no release can establish anything to put back.
        var world = await SeedRoutedProfileAsync(placements: 1);
        var orphaned = await OrphanOneClaimAsync(world);
        DeleteEveryObject(world);

        (await LocationStateAsync(orphaned)).ShouldBe(ArtifactLocationState.Deleting);

        var summary = await AbandonAsync(world, batchSize: 50);

        summary.Abandoned.ShouldBe(1);
        summary.Remaining.ShouldBe(0);
        (await LocationStateAsync(orphaned)).ShouldBe(ArtifactLocationState.Purged);
    }

    [Fact]
    public async Task A_worker_that_died_mid_purge_does_not_wedge_the_drain_across_repeated_passes()
    {
        // The operator's whole loop: drain, remove what the destination still serves, drain again. The first pass has
        // to hand the orphan back to a state the second pass can move it out of, or the record is stuck at the front
        // of every batch for good and the count it reports never moves.
        var world = await SeedRoutedProfileAsync(placements: 1);
        var orphaned = await OrphanOneClaimAsync(world);

        var first = await AbandonAsync(world, batchSize: 50);

        first.StillServed.ShouldBe(1);
        first.Remaining.ShouldBe(1, "a served placement is still held, and a drain that claimed otherwise would be the unsafe answer");
        (await LocationStateAsync(orphaned)).ShouldBe(ArtifactLocationState.Available);

        DeleteEveryObject(world);

        var second = await AbandonAsync(world, batchSize: 50);

        second.Remaining.ShouldBeLessThan(first.Remaining);
        (await LocationStateAsync(orphaned)).ShouldBe(ArtifactLocationState.Purged);
    }

    [Fact]
    public async Task A_record_closed_by_deleting_the_bytes_reads_differently_from_one_merely_abandoned()
    {
        // Both closures land on Purged, through the same finalize, writing the same StateChanged event. The operator
        // reading the ledger afterwards is asking one question — did the bytes go? — and until the verb is written
        // down the two rows are identical answers to it.
        var world = await SeedRoutedProfileAsync(placements: 2);
        var deleted = await PurgeOneAsync(world);
        DeleteEveryObject(world);

        (await AbandonAsync(world, batchSize: 50)).Abandoned.ShouldBe(1);

        var closures = await ClosuresAsync(world);

        closures.Count.ShouldBe(2, "both placements are closed, so the ledger holds both closures");
        closures[deleted.LocationId].Verb.ShouldBe("deleted", "the destination was asked to remove the bytes and did not refuse");

        var abandoned = closures.Single(entry => entry.Key != deleted.LocationId).Value;

        abandoned.Verb.ShouldBe("abandoned", "nothing was deleted here — the record was closed on what the destination said");
        abandoned.Observed.ShouldNotBeNullOrWhiteSpace("an abandonment that records no observation leaves the operator exactly where they were");
    }

    [Fact]
    public async Task After_a_drain_an_operator_can_name_every_key_that_may_still_hold_bytes()
    {
        // A drained profile is not an emptied destination. Abandonment closes the record WITHOUT deleting anything,
        // so a bucket that answered it is gone for good leaves every one of its objects exactly where it was, under
        // keys nothing points at any more. The row keeps the coordinate; only the ledger can say the bytes were never
        // asked to go.
        var world = await SeedRoutedProfileAsync(placements: 3);
        var deleted = await PurgeOneAsync(world);

        File.Exists(BytesAt(world, deleted.ObjectKey)).ShouldBeFalse("a delete that removed nothing would make the rest of this prove nothing");
        (await KeysClosedWithoutDeletingAsync(world)).ShouldBeEmpty("nothing has been abandoned yet");

        (await AbandonAsync(world, batchSize: 50, ScriptedProvider.GoneForGood())).Abandoned.ShouldBe(2);

        var stranded = await KeysClosedWithoutDeletingAsync(world);

        stranded.Count.ShouldBe(2, "every record closed without a delete has to be nameable, or the leak is silent");
        stranded.ShouldNotContain(deleted.ObjectKey, "the one destination that was asked to delete answered, and its key holds nothing");
        stranded.ShouldAllBe(key => File.Exists(BytesAt(world, key)), "an operator handed these keys must find the bytes still sitting there");
    }

    [Fact]
    public async Task A_record_closed_because_its_key_holds_someone_elses_object_says_so()
    {
        // The other conclusive closure, and the one the ledger has to keep apart from the first. Here the destination
        // is healthy and it IS serving something at the key — just not this object. Both closures land on Purged
        // through the same finalize, so an operator triaging what may still be out there reads the observation or
        // nothing: bytes stranded because a destination went away are not the problem that somebody else's bytes
        // sitting under our coordinate is.
        var world = await SeedRoutedProfileAsync(placements: 1);
        var usurped = await FirstPlacementAsync(world);

        await File.WriteAllTextAsync(BytesAt(world, usurped.ObjectKey), "an entirely different object living at that key");

        (await AbandonAsync(world, batchSize: 50)).Abandoned.ShouldBe(1, "a healthy destination holding something that is not ours is grounds to close the record");

        var closure = (await ClosuresAsync(world))[usurped.LocationId];

        closure.Verb.ShouldBe("abandoned", "nothing was deleted — those bytes were positively identified as not ours");
        closure.Observed.ShouldBe($"the destination holds something other than this object at {usurped.ObjectKey}",
            "the two conclusive closures have to be told apart in the ledger, not by reading the code that wrote them");
    }

    // ─── World ───────────────────────────────────────────────────────────────

    /// <summary>The answers a mixed batch gives, first-listed first — five unrelated problems that no one destination fault could produce together.</summary>
    private static readonly ArtifactCasProblemCode[] MixedAnswers =
    [
        ArtifactCasProblemCode.ProviderTimeout, ArtifactCasProblemCode.Throttled, ArtifactCasProblemCode.ProviderFailure,
        ArtifactCasProblemCode.StaleWorker, ArtifactCasProblemCode.ProviderUnavailableTransient,
    ];

    private async Task<ProfileAbandonmentSummary> AbandonAsync(RoutedWorld world, int batchSize) => await DrainAsync(world, batchSize, null);

    private async Task<ProfileAbandonmentSummary> AbandonAsync(RoutedWorld world, int batchSize, IArtifactCasPurgeCoordinator destination) =>
        await DrainAsync(world, batchSize, builder => builder.RegisterInstance(destination).As<IArtifactCasPurgeCoordinator>().SingleInstance());

    private async Task<ProfileAbandonmentSummary> AbandonAsync(RoutedWorld world, int batchSize, IArtifactStorageDriverFactoryCatalog catalog) =>
        await DrainAsync(world, batchSize, builder => builder.RegisterInstance(catalog).As<IArtifactStorageDriverFactoryCatalog>().SingleInstance());

    private async Task<ProfileAbandonmentSummary> DrainAsync(RoutedWorld world, int batchSize, Action<ContainerBuilder>? overrides)
    {
        using var scope = overrides == null ? _fixture.BeginScope() : _fixture.BeginScope(overrides);

        return await scope.Resolve<IProfileAbandonmentService>().AbandonAsync(world.TeamId, world.ActorId, world.ProfileId, batchSize, CancellationToken.None);
    }

    /// <summary>The object keys of the placements a pass meets first. Any fixed set would prove the same property — these are simply the ones an id-ordered batch hands out at the head.</summary>
    private async Task<IReadOnlyCollection<string>> FirstKeysAsync(RoutedWorld world, int count)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking()
            .Where(location => location.TeamId == world.TeamId).OrderBy(location => location.Id)
            .Select(location => location.ObjectKey).Take(count).ToListAsync();
    }

    private async Task RetireAsync(RoutedWorld world)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var profile = await db.StorageProfile.AsNoTracking().Where(row => row.Id == world.ProfileId)
            .Select(row => new { row.Xmin, row.CurrentRevision }).SingleAsync();

        await scope.Resolve<IStorageProfileService>().SetStateAsync(world.TeamId, world.ActorId, new SetStorageProfileStateCommand
        {
            ProfileId = world.ProfileId, State = StorageProfileStateValue.Retired,
            ExpectedXmin = profile.Xmin, ExpectedCurrentRevision = profile.CurrentRevision,
        }, CancellationToken.None);
    }

    private async Task<StorageProfileState> StateAsync(RoutedWorld world)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().StorageProfile.AsNoTracking()
            .Where(profile => profile.Id == world.ProfileId).Select(profile => profile.State).SingleAsync();
    }

    /// <summary>A worker that claimed a placement and died before it could delete or release it: the marker is all it left.</summary>
    private async Task<Guid> OrphanOneClaimAsync(RoutedWorld world)
    {
        using var scope = _fixture.BeginScope();
        var placement = await scope.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking()
            .Where(location => location.TeamId == world.TeamId).OrderBy(location => location.Id)
            .Select(location => new { location.Id, location.ArtifactObjectId }).FirstAsync();

        (await scope.Resolve<IArtifactCasPurgeCoordinator>().ClaimAsync(new ArtifactCasPurgeRequest
        {
            TeamId = world.TeamId, ArtifactObjectId = placement.ArtifactObjectId, ActorId = world.ActorId, ArtifactLocationId = placement.Id,
        }, CancellationToken.None)).ShouldBeOfType<ArtifactCasPurgeClaimResult.Claimed>();

        return placement.Id;
    }

    /// <summary>
    /// Empties the destination without taking it away: the objects are genuinely gone and the root still answers a
    /// probe. This is a deleted bucket, which is the operation abandonment exists for — and it is NOT what
    /// <see cref="Unmount"/> stages, which the corroboration has to tell apart from it.
    /// </summary>
    private static void DeleteEveryObject(RoutedWorld world) => Directory.Delete(Path.Combine(world.Root, "objects"), recursive: true);

    /// <summary>Takes the destination away without touching a byte — what an unmounted volume looks like from above: the root is not there, and every object under it still is.</summary>
    private string Unmount(RoutedWorld world)
    {
        var hidden = world.Root + "-unmounted";
        Directory.Move(world.Root, hidden);
        _roots.Add(hidden);

        return hidden;
    }

    /// <summary>The states of the placements THIS test seeded. Never a deployment-wide tally: other classes share the database and drain their own profiles.</summary>
    private async Task<List<ArtifactLocationState>> PlacementStatesAsync(RoutedWorld world)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking()
            .Where(location => location.TeamId == world.TeamId).Select(location => location.State).ToListAsync();
    }

    private async Task<ArtifactLocationState> LocationStateAsync(Guid locationId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking()
            .Where(location => location.Id == locationId).Select(location => location.State).SingleAsync();
    }

    /// <summary>Removes one placement's bytes for real, through the delete path — the closure an abandonment has to be distinguishable from.</summary>
    private async Task<SeededPlacement> PurgeOneAsync(RoutedWorld world)
    {
        var placement = await FirstPlacementAsync(world);

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IArtifactCasPurgeCoordinator>().PurgeAsync(new ArtifactCasPurgeRequest
        {
            TeamId = world.TeamId, ArtifactObjectId = placement.ArtifactObjectId, ActorId = world.ActorId, ArtifactLocationId = placement.LocationId,
        }, CancellationToken.None)).ShouldBeOfType<ArtifactCasPurgeResult.Purged>();

        return placement;
    }

    /// <summary>The placement a fixture stages against, by the same ordering the seeding used — never a scan of the table, which sibling classes are writing to.</summary>
    private async Task<SeededPlacement> FirstPlacementAsync(RoutedWorld world)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking()
            .Where(location => location.TeamId == world.TeamId).OrderBy(location => location.Id)
            .Select(location => new SeededPlacement(location.Id, location.ArtifactObjectId, location.ObjectKey)).FirstAsync();
    }

    /// <summary>How each of THIS world's placements was closed, read off the ledger. Never a deployment-wide scan: sibling classes close their own rows in the same table.</summary>
    private async Task<Dictionary<Guid, Closure>> ClosuresAsync(RoutedWorld world)
    {
        using var scope = _fixture.BeginScope();
        var entries = await scope.Resolve<CodeSpaceDbContext>().ArtifactLocationEvent.AsNoTracking()
            .Where(entry => entry.TeamId == world.TeamId && entry.State == ArtifactLocationState.Purged)
            .Select(entry => new { entry.ArtifactLocationId, entry.DetailsJson }).ToListAsync();

        return entries.ToDictionary(entry => entry.ArtifactLocationId, entry => Closure.Read(entry.DetailsJson));
    }

    /// <summary>
    /// The operator's question, asked of the ledger the way an operator asks it: which keys were closed WITHOUT
    /// anything being deleted, and so may still hold bytes. Scoped to this world's team, never deployment-wide.
    /// </summary>
    private async Task<List<string>> KeysClosedWithoutDeletingAsync(RoutedWorld world)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().Database.SqlQuery<string>($"""
            SELECT location.object_key AS "Value"
            FROM artifact_location_event closure
            JOIN artifact_location location ON location.id = closure.artifact_location_id
            WHERE closure.team_id = {world.TeamId} AND closure.details_jsonb ->> 'closure' = 'abandoned'
            ORDER BY location.object_key
            """).ToListAsync();
    }

    /// <summary>Where the local driver actually keeps the bytes for a key — it namespaces every one of them under an "objects" directory of its own.</summary>
    private static string BytesAt(RoutedWorld world, string objectKey) =>
        Path.Combine(world.Root, "objects", objectKey.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>A profile whose objects really exist on disk, so "the destination still serves it" is a fact rather than a fixture flag.</summary>
    private async Task<RoutedWorld> SeedRoutedProfileAsync(int placements)
    {
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var root = Path.Combine(Path.GetTempPath(), $"codespace-abandon-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _roots.Add(root);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var profileId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var config = JsonSerializer.Serialize(new { rootPath = root });
        using var document = JsonDocument.Parse(config);

        db.StorageProfile.Add(new StorageProfile
        {
            Id = profileId, TeamId = teamId, StableName = $"abandon-{profileId:N}", CurrentRevision = 1,
            State = StorageProfileState.Disabled, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
            Revisions =
            {
                new StorageProfileRevision
                {
                    Id = revisionId, TeamId = teamId, StorageProfileId = profileId, Revision = 1,
                    ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, NonSecretConfigJson = config, CredentialRef = null,
                    NamespaceFingerprint = StorageProfileRules.NamespaceFingerprint(LocalRwxArtifactStorageDriverFactory.TypeKey, document.RootElement),
                    CreatedDate = now, CreatedBy = actorId,
                },
            },
        });
        await db.SaveChangesAsync();

        foreach (var index in Enumerable.Range(0, placements)) await PlaceAsync(db, teamId, actorId, revisionId, root, index);

        var world = new RoutedWorld(teamId, actorId, profileId, root);
        _worlds.Add(world);

        return world;
    }

    private static async Task PlaceAsync(CodeSpaceDbContext db, Guid teamId, Guid actorId, Guid revisionId, string root, int index)
    {
        var now = DateTimeOffset.UtcNow;
        var objectId = Guid.NewGuid();
        var payload = System.Text.Encoding.UTF8.GetBytes($"object {index} {objectId:N}");
        var digest = System.Security.Cryptography.SHA256.HashData(payload);
        var objectKey = $"artifacts/{objectId:N}";
        // The local driver namespaces every key under an "objects" directory of its own, so a fixture that wrote
        // where it thought the key pointed would have the destination report Missing and abandon a live object.
        var path = Path.Combine(root, "objects", objectKey.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, payload);

        db.ArtifactObject.Add(new ArtifactObject { Id = objectId, TeamId = teamId, Digest = digest, SizeBytes = payload.Length, CreatedDate = now });

        var location = new ArtifactLocation
        {
            Id = Guid.NewGuid(), TeamId = teamId, ArtifactObjectId = objectId, StorageProfileRevisionId = revisionId,
            Locator = objectKey, ObjectKey = objectKey, State = ArtifactLocationState.Available, Revision = 1, VerifiedAt = now,
            ObservedSizeBytes = payload.Length, ProviderChecksumAlgorithm = "Sha256", ProviderChecksum = digest,
            CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        };
        db.ArtifactLocation.Add(location);
        db.ArtifactLocationEvent.Add(new ArtifactLocationEvent
        {
            Id = Guid.NewGuid(), TeamId = teamId, ArtifactLocationId = location.Id, Revision = 1,
            EventType = ArtifactLocationEventType.Created, State = ArtifactLocationState.Available, ObservedAt = now,
            ProviderChecksumAlgorithm = "Sha256", ProviderChecksum = digest, ObservedSizeBytes = payload.Length,
            VerifiedAt = now, DetailsJson = "{}", CreatedBy = actorId,
        });

        await db.SaveChangesAsync();
    }

    public void Dispose()
    {
        foreach (var world in _worlds) CloseSeededPlacements(world);

        foreach (var root in _roots.Where(Directory.Exists))
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// Drains every placement this class seeded, best effort, before the roots go.
    ///
    /// <para>An <c>artifact_location</c> row can never be deleted — the ledger is durable identity — so a seeded
    /// placement left <c>Available</c> stays in the location verifier's DEPLOYMENT-WIDE sweep for the rest of the
    /// run, and enough of them crowd out the one row a sibling class is waiting on. Draining to <c>Purged</c> is the
    /// only cleanup this table has. Emptying the destination first is what makes the drain able to close them.</para>
    /// </summary>
    private void CloseSeededPlacements(RoutedWorld world)
    {
        try
        {
            if (Directory.Exists(Path.Combine(world.Root, "objects"))) DeleteEveryObject(world);
            Directory.CreateDirectory(world.Root);

            AbandonAsync(world, MaxDrainableBatch).GetAwaiter().GetResult();
        }
        catch (Exception) { }
    }

    /// <summary>
    /// A destination that answers the drain with a scripted cycle of refusals. Medium-mock fidelity, and only here:
    /// one local driver cannot be made to give five different provider errors across one batch, and whether the
    /// answers AGREE is the entire question the breaker asks. The placement selection, the ordering and the summary
    /// all still come from the real service against the real database.
    /// </summary>
    private sealed class ScriptedDestination(params ArtifactCasProblemCode[] codes) : IArtifactCasPurgeCoordinator
    {
        public int Asked { get; private set; }

        public Task<ArtifactCasPurgeClaimResult> ClaimAsync(ArtifactCasPurgeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ArtifactCasPurgeClaimResult>(new ArtifactCasPurgeClaimResult.Claimed(new ArtifactCasPurgeClaim
            {
                TeamId = request.TeamId, ArtifactObjectId = request.ArtifactObjectId, LocationId = request.ArtifactLocationId!.Value,
                LocationRevision = 1, StorageProfileId = Guid.NewGuid(), StorageProfileRevision = 1, ObjectKey = "scripted",
                ProviderETag = null, ProviderObjectVersion = null, ActorId = request.ActorId, OperationTimeout = TimeSpan.FromSeconds(1),
            }));

        public Task<ArtifactCasAbandonResult> AbandonAsync(ArtifactCasPurgeClaim claim, CancellationToken cancellationToken) =>
            Task.FromResult<ArtifactCasAbandonResult>(new ArtifactCasAbandonResult.Rejected(new ArtifactCasProblem(codes[Asked++ % codes.Length], true)));

        public Task<ArtifactCasPurgeResult> DeleteAsync(ArtifactCasPurgeClaim claim, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ArtifactCasReleaseOutcome> ReleaseAsync(ArtifactCasPurgeClaim claim, ArtifactCasReleaseEvidence evidence, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ArtifactCasPurgeResult> PurgeAsync(ArtifactCasPurgeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>
    /// A worker whose image does not carry the profile's provider module — the deployment mistake, not a
    /// destination. The catalog answers it from this process's own registry, having contacted nothing.
    /// </summary>
    private sealed class MissingProviderModule : IArtifactStorageDriverFactoryCatalog
    {
        public IArtifactStorageDriverFactory? Get(string providerTypeKey) => null;
        public IArtifactStorageDriverFactory Require(string providerTypeKey) => throw new NotSupportedException();
    }

    /// <summary>
    /// A destination scripted at the PROVIDER seam, for the two answers a local root cannot give: a DURABLE "I am
    /// gone" about itself — a vanished local root is retryable, because an unmounted volume can be mounted back —
    /// and a refusal aimed at named keys while every other key is answered normally.
    ///
    /// <para>Medium-mock fidelity, and only below the seam: the coordinator, the claims it takes, the ledger they
    /// move and the summary the service reports are all real, which is what makes an ordering property observable.</para>
    /// </summary>
    private sealed class ScriptedProvider : IArtifactStorageDriverFactoryCatalog, IArtifactStorageDriverFactory, IArtifactStorageDriver
    {
        private static readonly ArtifactStorageError BucketDeleted = new(ArtifactStorageErrorCode.Unavailable, "NoSuchBucket", IsRetryable: false);
        private static readonly ArtifactStorageError KeyGone = new(ArtifactStorageErrorCode.Missing, "no such key", IsRetryable: false);
        private static readonly ArtifactStorageError NotNow = new(ArtifactStorageErrorCode.Throttled, "slow down", IsRetryable: true);

        private readonly ArtifactStorageProbeResult _probe;
        private readonly ArtifactStorageError _answer;
        private readonly HashSet<string> _refused;

        private ScriptedProvider(ArtifactStorageProbeResult probe, ArtifactStorageError answer, IEnumerable<string> refused)
        {
            _probe = probe;
            _answer = answer;
            _refused = refused.ToHashSet(StringComparer.Ordinal);
        }

        /// <summary>A bucket the operator deleted: it cannot serve the object and says the same about itself, durably — which is the one thing that separates it from a mount that will come back.</summary>
        public static ScriptedProvider GoneForGood() => new(Probe(ArtifactStorageProbeStatus.Unavailable, BucketDeleted), BucketDeleted, []);

        /// <summary>A destination that has lost every object except the named keys, which refuse for a reason that is never grounds to close anything — however many passes ask them.</summary>
        public static ScriptedProvider LosingEverythingExcept(IEnumerable<string> refused) => new(Probe(ArtifactStorageProbeStatus.Available, null), KeyGone, refused);

        /// <summary>A key that lost its permission: the refusal is durable, like a deleted bucket's, but it is about the CREDENTIAL — every object is still there, and every HEAD the same key makes looks exactly as if none of them were.</summary>
        public static ScriptedProvider WithARefusedKey(ArtifactStorageErrorCode refusal) =>
            new(Probe(ArtifactStorageProbeStatus.Unavailable, new ArtifactStorageError(refusal, "the key no longer has access", IsRetryable: false)), KeyGone, []);

        public string ProviderTypeKey => LocalRwxArtifactStorageDriverFactory.TypeKey;
        public StorageProviderCapabilities Capabilities => StorageProviderCapabilities.StreamingRead;

        public IArtifactStorageDriverFactory? Get(string providerTypeKey) => string.Equals(providerTypeKey, ProviderTypeKey, StringComparison.Ordinal) ? this : null;
        public IArtifactStorageDriverFactory Require(string providerTypeKey) => Get(providerTypeKey) ?? throw new NotSupportedException();
        public ValueTask<IArtifactStorageDriver> CreateAsync(ArtifactStorageDriverCreateRequest request, CancellationToken cancellationToken) => ValueTask.FromResult<IArtifactStorageDriver>(this);

        public ValueTask<ArtifactStorageProbeResult> ProbeAsync(ArtifactStorageProbeRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(_probe);

        public ValueTask<ArtifactStorageHeadResult> HeadAsync(ArtifactStorageHeadRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ArtifactStorageHeadResult.Failed(_refused.Contains(request.ObjectKey) ? NotNow : _answer));

        public ValueTask<ArtifactStoragePutResult> PutAsync(ArtifactStoragePutRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ArtifactStorageReadResult> OpenReadAsync(ArtifactStorageReadRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ArtifactStorageDeleteResult> DeleteAsync(ArtifactStorageDeleteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static ArtifactStorageProbeResult Probe(ArtifactStorageProbeStatus status, ArtifactStorageError? error) =>
            new() { Status = status, Latency = TimeSpan.Zero, Error = error };
    }

    private sealed record RoutedWorld(Guid TeamId, Guid ActorId, Guid ProfileId, string Root);

    private sealed record SeededPlacement(Guid LocationId, Guid ArtifactObjectId, string ObjectKey);

    /// <summary>What one closure's ledger entry says about itself, as an operator reads it back out of the details column.</summary>
    private sealed record Closure(string? Verb, string? Observed)
    {
        public static Closure Read(string detailsJson)
        {
            var details = JsonDocument.Parse(detailsJson).RootElement;

            return new Closure(Text(details, "closure"), Text(details, "observed"));
        }

        private static string? Text(JsonElement details, string name) =>
            details.TryGetProperty(name, out var value) ? value.GetString() : null;
    }
}
