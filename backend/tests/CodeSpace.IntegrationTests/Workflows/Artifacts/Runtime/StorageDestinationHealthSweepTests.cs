using System.Security.Cryptography;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts.Runtime;

/// <summary>
/// Which destinations the scheduled sweep re-asks about. The selection IS the design: probing everything spends a real
/// provider round trip on destinations no run can reach, and probing too little leaves the ones that lose data unchecked.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class StorageDestinationHealthSweepTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly List<string> _roots = [];

    public StorageDestinationHealthSweepTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_destination_an_active_route_binds_writes_to_is_probed_even_though_nobody_asked()
    {
        // The whole point. Before this, the only probe that ever ran was one an operator clicked, so a credential
        // revoked at the provider stayed invisible until someone opened an artifact.
        var world = await SeedAsync(NewRoot(), routeState: StorageRouteState.Active);

        (await SweepAsync()).ShouldBeGreaterThanOrEqualTo(1);

        var health = (await HealthAsync(world)).ShouldNotBeNull();
        health.Status.ShouldBe(StorageProfileProbeStatusValue.Available);
        health.WriteVerified.ShouldBeTrue("a scheduled reachability ping would not prove a run's bytes will land");
    }

    [Fact]
    public async Task A_broken_destination_an_active_route_binds_writes_to_is_recorded_as_broken()
    {
        var world = await SeedAsync("/dev/null/codespace-cannot-write-here", routeState: StorageRouteState.Active);

        await SweepAsync();

        var health = (await HealthAsync(world)).ShouldNotBeNull();
        health.Status.ShouldNotBe(StorageProfileProbeStatusValue.Available);
        health.FailureCode.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_profile_no_active_route_points_at_is_left_alone()
    {
        // A Draft route's destination is nobody's problem yet, and a real provider round trip per pass is not free.
        var world = await SeedAsync(NewRoot(), routeState: StorageRouteState.Draft);

        await SweepAsync();

        (await HealthAsync(world)).ShouldBeNull();
    }

    [Fact]
    public async Task A_disabled_profile_an_active_route_still_binds_writes_to_is_really_contacted()
    {
        // Disabling a profile unbinds no route, so this destination is still in the population. What it must NOT
        // produce is ProfileNotActive: that answer never opens a driver, so the row would restate the lifecycle
        // state the settings page already shows and no round trip would ever have happened.
        var world = await SeedAsync(NewRoot(), routeState: StorageRouteState.Active, profileState: StorageProfileState.Disabled);
        (await HealthAsync(world)).ShouldBeNull("the sweep must be what writes this row; the fixture leaves it unprobed");

        await SweepAsync();

        var health = (await HealthAsync(world)).ShouldNotBeNull();
        health.Status.ShouldBe(StorageProfileProbeStatusValue.Available, "a destination that answers is reachable whatever its profile's lifecycle state says");
        health.FailureCode.ShouldBeNull();
        health.WriteVerified.ShouldBeFalse("no write was attempted, and a read-qualified pass must never be recorded as one that proved bytes land");
    }

    [Fact]
    public async Task A_disabled_profiles_vanished_destination_is_recorded_as_unreachable_not_as_disabled()
    {
        // The pair to the case above, and the one the widened population exists for: the lifecycle gate would have
        // answered ProfileNotActive for a directory that no longer exists, which reads on the settings page exactly
        // like the healthy Disabled profile next to it.
        var world = await SeedAsync("/dev/null/codespace-cannot-read-here", routeState: StorageRouteState.Active, profileState: StorageProfileState.Disabled);

        await SweepAsync();

        var health = (await HealthAsync(world)).ShouldNotBeNull();
        health.Status.ShouldBe(StorageProfileProbeStatusValue.Unavailable);
        health.FailureStage.ShouldBe(StorageProfileProbeFailureStageValue.Probe, "the answer must come from the destination, not from storage_profile.state");
    }

    [Fact]
    public async Task A_retired_profile_that_still_holds_a_lost_placement_is_probed_and_shows_on_the_storage_page()
    {
        // Nothing writes here any more, but every object ever written is still recorded here — so "is this
        // destination still answering" is the one question the operator most needs an answer to, and Retired admits
        // exactly the read that answers it.
        var world = await SeedAsync(NewRoot(), routeState: StorageRouteState.Draft, profileState: StorageProfileState.Retired);
        await PlaceAsync(world, ArtifactLocationState.Missing);
        (await SummaryAsync(world)).Health.ShouldBeNull("nothing has probed this destination yet");

        await SweepAsync();

        var health = (await SummaryAsync(world)).Health.ShouldNotBeNull();
        health.Status.ShouldBe(StorageProfileProbeStatusValue.Available);
        health.WriteVerified.ShouldBeFalse("a terminal profile admits no write, so no pass may claim one landed");
    }

    [Fact]
    public async Task Postgres_puts_a_never_probed_destination_at_the_head_of_a_pass_not_at_its_back()
    {
        // No unit test can catch this. LINQ-to-Objects sorts nulls FIRST on an ascending key and PostgreSQL sorts
        // them LAST, so an in-memory population passes whether or not the ordering names where nulls go — only the
        // real provider can say. It became load-bearing the moment a pass got a ceiling: sorted to the back, a
        // destination nothing has ever contacted is not probed later, it is never probed at all.
        var neverProbed = await SeedAsync(NewRoot(), routeState: StorageRouteState.Active);
        var probedLongAgo = await SeedAsync(NewRoot(), routeState: StorageRouteState.Active);
        await ObserveAsync(probedLongAgo, DateTimeOffset.UtcNow - TimeSpan.FromDays(3650));

        var order = await DueOrderAsync(neverProbed, probedLongAgo);

        order.ShouldBe([neverProbed.ProfileId, probedLongAgo.ProfileId],
            "a destination with no observation at all outranks one observed ten years ago; if this reversed, PostgreSQL is sorting NULLs last and the never-probed are starved behind every destination that has a row");
    }

    [Fact]
    public async Task A_freshly_observed_destination_is_not_probed_again_on_the_next_pass()
    {
        var world = await SeedAsync(NewRoot(), routeState: StorageRouteState.Active);

        await SweepAsync();
        var first = (await HealthAsync(world)).ShouldNotBeNull().ObservedAt;

        await SweepAsync();

        (await HealthAsync(world)).ShouldNotBeNull().ObservedAt.ShouldBe(first, "the staleness window exists so a tick does not re-probe what it just observed");
    }

    [Fact]
    public async Task A_stale_observation_is_re_taken()
    {
        // The failure this whole lane exists to notice: a destination that WAS working and stopped.
        var world = await SeedAsync(NewRoot(), routeState: StorageRouteState.Active);
        await SweepAsync();
        await AgeHealthAsync(world, StorageDestinationHealthSweep.StaleAfter + TimeSpan.FromMinutes(1));
        var stale = (await HealthAsync(world)).ShouldNotBeNull().ObservedAt;

        await SweepAsync();

        (await HealthAsync(world)).ShouldNotBeNull().ObservedAt.ShouldBeGreaterThan(stale);
    }

    // ─── World + helpers ─────────────────────────────────────────────────────

    private async Task<int> SweepAsync()
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<IStorageDestinationHealthSweep>().ProbeStaleAsync(CancellationToken.None);
    }

    private async Task<StorageProfileHealth?> HealthAsync(World world)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().StorageProfileHealth.AsNoTracking()
            .SingleOrDefaultAsync(row => row.StorageProfileId == world.ProfileId);
    }

    /// <summary>What the storage settings page shows for this one profile — the surface an operator actually reads.</summary>
    private async Task<StorageProfileSummary> SummaryAsync(World world)
    {
        using var scope = _fixture.BeginScope();
        var profiles = await scope.Resolve<IStorageProfileService>().ListAsync(world.TeamId, CancellationToken.None);

        return profiles.Single(profile => profile.Id == world.ProfileId);
    }

    /// <summary>Record that something observed this destination at <paramref name="at"/>, without running a pass.</summary>
    private async Task ObserveAsync(World world, DateTimeOffset at)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.StorageProfileHealth.Add(new StorageProfileHealth
        {
            TeamId = world.TeamId, StorageProfileId = world.ProfileId, ProfileRevision = 1,
            Status = StorageProfileProbeStatusValue.Available, WriteVerified = true, LatencyMs = 1, ObservedAt = at,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The order one pass would take these worlds' destinations in, asked of PostgreSQL itself.
    ///
    /// <para>The population is scoped to the teams this test seeded, which is what keeps the assertion off a
    /// deployment-wide tally: every other suite's destinations are due at the same time and would otherwise decide
    /// both the contents and the length of the answer. The ORDER BY still executes on the real provider over real
    /// rows — that is the whole point, since null placement is exactly what differs from LINQ-to-Objects.</para>
    /// </summary>
    private async Task<IReadOnlyList<Guid>> DueOrderAsync(params World[] worlds)
    {
        var teamIds = worlds.Select(world => world.TeamId).ToArray();
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var mine = new StorageDestinationHealthSweep.PopulationTables
        {
            Profiles = db.StorageProfile.Where(row => teamIds.Contains(row.TeamId)),
            Routes = db.StorageRoute.Where(row => teamIds.Contains(row.TeamId)),
            RouteRevisions = db.StorageRouteRevision.Where(row => teamIds.Contains(row.TeamId)),
            ProfileRevisions = db.StorageProfileRevision.Where(row => teamIds.Contains(row.TeamId)),
            Locations = db.ArtifactLocation.Where(row => teamIds.Contains(row.TeamId)),
            Health = db.StorageProfileHealth.Where(row => teamIds.Contains(row.TeamId)),
        };

        return await StorageDestinationHealthSweep.StaleDestinations(mine, DateTimeOffset.UtcNow)
            .Select(destination => destination.StorageProfileId).ToListAsync();
    }

    private async Task AgeHealthAsync(World world, TimeSpan by)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE storage_profile_health SET observed_at = observed_at - {by} WHERE storage_profile_id = {world.ProfileId}");
    }

    /// <summary>
    /// A destination an operator has PROVISIONED — the directory exists before anything watches it.
    ///
    /// <para>It used to be a path nobody had created, and the sweep's own write probe made it on first contact. That
    /// is exactly the behaviour a monitoring sweep must not have: it is what let a vanished mount go green and then
    /// supply the integrity verifier with corroboration to demote everything underneath. Provisioning belongs to the
    /// operator's adopt / activate / test actions, so the fixture does it here.</para>
    /// </summary>
    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "codespace-sweep", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    private async Task<World> SeedAsync(string rootPath, StorageRouteState routeState, StorageProfileState profileState = StorageProfileState.Active)
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var profileRevisionId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"sweep-{actorId:N}@test.local", Name = "Sweep" });
        db.Team.Add(new Team { Id = teamId, Slug = $"sweep-{teamId:N}", Name = "Sweep", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });

        var profile = new StorageProfile
        {
            Id = profileId, TeamId = teamId, StableName = $"sweep-{profileId:N}", CurrentRevision = 1,
            State = profileState, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        };
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = profileRevisionId, TeamId = teamId, StorageProfileId = profileId, Revision = 1,
            ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey,
            NonSecretConfigJson = JsonSerializer.Serialize(new { rootPath }), CredentialRef = null,
            NamespaceFingerprint = $"sha256:{new string('f', 64)}", CreatedDate = now, CreatedBy = actorId,
        });
        db.StorageProfile.Add(profile);
        await db.SaveChangesAsync();

        // The route is seeded directly: this suite is about WHICH destinations the sweep selects, and driving the
        // route service would make every case depend on its activation rules instead.
        db.StorageRoute.Add(new StorageRoute
        {
            Id = routeId, TeamId = teamId, DataClassTypeKey = "agent-run-log/v1", CurrentRevision = 1,
            State = StorageRouteState.Draft, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
            Revisions =
            {
                new StorageRouteRevision
                {
                    Id = Guid.NewGuid(), TeamId = teamId, StorageRouteId = routeId, Revision = 1,
                    StorageProfileId = profileId,
                    ProfileRevisionMode = StorageProfileRevisionMode.CurrentAtWrite, PinnedProfileRevision = null,
                    CreatedDate = now, CreatedBy = actorId,
                },
            },
        });
        await db.SaveChangesAsync();

        if (routeState != StorageRouteState.Draft)
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE storage_route SET state = {routeState.ToString()} WHERE id = {routeId}");

        return new World(teamId, actorId, profileId, profileRevisionId);
    }

    /// <summary>One placement recorded under the profile's revision, in a state nothing has settled.</summary>
    private async Task PlaceAsync(World world, ArtifactLocationState state)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var objectId = Guid.NewGuid();
        var objectKey = $"objects/{objectId:N}";

        db.ArtifactObject.Add(new ArtifactObject { Id = objectId, TeamId = world.TeamId, Digest = SHA256.HashData(objectId.ToByteArray()), SizeBytes = 12, CreatedDate = now });
        db.ArtifactLocation.Add(new ArtifactLocation
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, ArtifactObjectId = objectId, StorageProfileRevisionId = world.ProfileRevisionId,
            Locator = objectKey, ObjectKey = objectKey, State = state, Revision = 1, VerifiedAt = now,
            CreatedDate = now, CreatedBy = world.ActorId, LastModifiedDate = now, LastModifiedBy = world.ActorId,
            Events =
            {
                new ArtifactLocationEvent
                {
                    Id = Guid.NewGuid(), TeamId = world.TeamId, Revision = 1, EventType = ArtifactLocationEventType.Created,
                    State = state, ObservedAt = now, VerifiedAt = now, DetailsJson = "{}", CreatedBy = world.ActorId,
                },
            },
        });

        await db.SaveChangesAsync();
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

    private sealed record World(Guid TeamId, Guid ActorId, Guid ProfileId, Guid ProfileRevisionId);
}
