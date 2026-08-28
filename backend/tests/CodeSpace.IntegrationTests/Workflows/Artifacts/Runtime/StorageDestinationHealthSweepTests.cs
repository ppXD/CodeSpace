using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
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

    private async Task AgeHealthAsync(World world, TimeSpan by)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE storage_profile_health SET observed_at = observed_at - {by} WHERE storage_profile_id = {world.ProfileId}");
    }

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "codespace-sweep", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
    }

    private async Task<World> SeedAsync(string rootPath, StorageRouteState routeState)
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
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
            State = StorageProfileState.Active, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        };
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageProfileId = profileId, Revision = 1,
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

        return new World(teamId, actorId, profileId);
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

    private sealed record World(Guid TeamId, Guid ActorId, Guid ProfileId);
}
