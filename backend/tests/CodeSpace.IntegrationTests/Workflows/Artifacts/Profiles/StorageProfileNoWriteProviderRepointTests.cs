using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles.Exceptions;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local.Legacy;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing.Exceptions;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts.Profiles;

/// <summary>
/// The revision ledger is the second door onto the fact the route plane guards. A profile revision may change provider
/// type freely, so an operator could bind a data class to an ordinary profile and then repoint the PROFILE at a
/// provider that takes no bytes — reaching the same permanent write failure the route-side refusal exists to prevent,
/// at the first artifact write, with no operator standing. The rule is decided here, where the provider is chosen.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class StorageProfileNoWriteProviderRepointTests : IDisposable
{
    /// <summary>A path no test in this class ever writes to: only the activation race below activates a route, and only activation probes a destination.</summary>
    private const string UnusedRoot = "/unused/repoint";

    private readonly PostgresFixture _fixture;
    private readonly List<string> _roots = [];

    public StorageProfileNoWriteProviderRepointTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Repointing_a_profile_an_active_route_writes_through_at_a_no_write_provider_is_refused()
    {
        var world = await SeedProfileAsync(UnusedRoot);
        await SeedRouteAsync(world, world.ProfileId, StorageRouteState.Active, StorageProfileRevisionMode.CurrentAtWrite);

        var refused = await Should.ThrowAsync<StorageProfileConflictException>(() => AppendLegacyRevisionAsync(world));

        refused.Message.ShouldContain(LocalLegacyArtifactStorageDriverFactory.TypeKey, Case.Sensitive, "an operator is told which provider they chose");
        refused.Message.ShouldContain("accepts no new bytes", Case.Sensitive, "and told it is the capability, not a destination having a bad day");
        (await HeadProviderAsync(world)).ShouldBe(LocalRwxArtifactStorageDriverFactory.TypeKey, "the refused revision left the head where it was");
    }

    /// <summary>
    /// The three shapes that cannot reach the runtime failure, so the rule must not refuse them. A Draft route writes
    /// nothing and its own activation door re-reads this same rule; a Pinned route keeps resolving the revision it
    /// named, so a head it never reads changes nothing about where its bytes land.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData(StorageRouteState.Draft, StorageProfileRevisionMode.CurrentAtWrite)]
    [InlineData(StorageRouteState.Active, StorageProfileRevisionMode.Pinned)]
    public async Task Repointing_a_profile_no_active_writer_follows_is_allowed(StorageRouteState? routeState, StorageProfileRevisionMode? mode)
    {
        var world = await SeedProfileAsync(UnusedRoot);
        if (routeState is { } state && mode is { } selection) await SeedRouteAsync(world, world.ProfileId, state, selection);

        (await AppendLegacyRevisionAsync(world)).ShouldNotBeNull().CurrentRevision.ShouldBe(2);

        (await HeadProviderAsync(world)).ShouldBe(LocalLegacyArtifactStorageDriverFactory.TypeKey);
    }

    /// <summary>
    /// The re-reading the Draft allow-case above rests on. Letting a Draft route be repointed under is only honest if
    /// the door it must still pass asks the same question again — so that door is exercised here, on a profile that
    /// has ALREADY moved onto a provider taking no bytes, which is the exact state the allow-case creates.
    /// </summary>
    [Fact]
    public async Task Activating_a_route_onto_a_profile_repointed_to_a_no_write_provider_is_refused()
    {
        var world = await SeedActiveProfileAsync(UnusedRoot);
        var routeId = await BindRouteAsync(world);

        (await AppendLegacyRevisionAsync(world)).ShouldNotBeNull("the Draft allow-case, reached through the real door rather than seeded");

        var refused = await Should.ThrowAsync<StorageRouteInvalidException>(() => ActivateRouteAsync(world, routeId));

        refused.Message.ShouldContain(LocalLegacyArtifactStorageDriverFactory.TypeKey, Case.Sensitive, "an operator is told which provider they were about to bind");
        refused.Message.ShouldContain("accepts no new bytes", Case.Sensitive, "and told it is the capability, so no retry and no repair of the destination can make this activation succeed");
        (await RouteStateAsync(routeId)).ShouldBe(StorageRouteState.Draft, "a refused activation leaves the route exactly where it was, and activation is a one-way door");
    }

    /// <summary>
    /// The two readings, run at the same time on one profile. Each guard is a SNAPSHOT read of the other end's rows —
    /// the route side reads which provider the head names, the profile side counts which routes follow that head — so
    /// unserialized they both pass and both commit, landing an Active route resolving a head that accepts no new
    /// bytes. Every artifact write it binds would then fail at the destination, permanently, and activation cannot be
    /// undone.
    ///
    /// <para>A third session holds <see cref="StorageProfileHeadLock"/> so the race is a fact rather than a timing
    /// accident: while it is held NEITHER writer may reach its write, which is the claim that each of them takes the
    /// lock. Removing it from either side makes that side's wait assertion fail here.</para>
    /// </summary>
    [Fact]
    public async Task An_activation_racing_a_repoint_on_one_profile_serializes_and_the_loser_is_refused()
    {
        var world = await SeedActiveProfileAsync(NewRoot());
        var routeId = await BindRouteAsync(world);

        using var holder = _fixture.BeginScope();
        var held = await StorageProfileHeadLock.TakeAsync(holder.Resolve<CodeSpaceDbContext>().Database, world.ProfileId, CancellationToken.None);

        var activation = Task.Run(() => ActivateRouteAsync(world, routeId));
        var repoint = Task.Run(() => AppendLegacyRevisionAsync(world));
        var settled = await Task.WhenAny(activation, repoint, Task.Delay(TimeSpan.FromSeconds(3)));

        settled.ShouldNotBe(activation, "route activation reached its write while another session held this profile's head lock, so it bound a writer to a head it had only read in a snapshot. Diagnose with: psql -c \"SELECT granted, objid FROM pg_locks WHERE locktype = 'advisory'\".");
        settled.ShouldNotBe(repoint, "the profile revision reached its write while another session held this profile's head lock, so it counted the routes following this head without excluding the activation that adds one.");

        await held.ShouldNotBeNull().DisposeAsync();

        var activationFailure = await SettleAsync(activation);
        var repointFailure = await SettleAsync(repoint);

        new[] { activationFailure, repointFailure }.Count(failure => failure == null)
            .ShouldBe(1, "exactly one writer may be accepted — both accepted means each passed a guard the other was about to invalidate");
        (activationFailure ?? repointFailure).ShouldNotBeNull().Message
            .ShouldContain("accepts no new bytes", Case.Sensitive, "the loser is refused BY THE RULE, naming the capability, not by a lock timeout or a version mismatch");

        var head = await HeadProviderAsync(world);
        var routeState = await RouteStateAsync(routeId);

        (head == LocalLegacyArtifactStorageDriverFactory.TypeKey && routeState == StorageRouteState.Active)
            .ShouldBeFalse("an Active route now resolves a head that accepts no new bytes — the state both guards exist to forbid, committed because neither saw the other");
    }

    /// <summary>
    /// The THIRD door onto the same fact, raced. Activation is not the only way a route becomes a writer of a
    /// profile's head: appending a route revision that names another profile binds an already-Active route to THAT
    /// profile's head the moment it commits, without ever touching the route's state. So the profile side's count of
    /// following writers, taken a moment earlier, sees nothing — while the route side reads a head the profile side is
    /// about to move. Unserialized both pass their own guard, both commit, and the route resolves a provider that
    /// accepts no new bytes: the identical forbidden state, reached through a door neither guard was watching.
    ///
    /// <para>Held from a third session, as in the activation race above, so this is a fact rather than a timing
    /// accident: while the lock is held NEITHER writer may reach its write, which is the claim that each of them takes
    /// it. Remove <see cref="StorageProfileHeadLock"/> from <c>StorageRouteService.AppendRevisionAsync</c> and the move
    /// sails past the holder, failing its wait assertion — that assertion is the mutation signal, and it is the whole
    /// of it. The committed forbidden state is NOT reachable from a test: staging it needs the route side paused
    /// between its snapshot read and its commit, and the only thing that pauses it there is the very lock being
    /// mutated away. So the assertions below state the invariant end to end, but what discriminates this lock site is
    /// the wait.</para>
    ///
    /// <para>A STRONGER discriminator exists and is deliberately not taken here, so the next reader does not have to
    /// re-derive that: the committed forbidden state IS stageable, by pausing the route side between its snapshot read
    /// and its commit with an ordinary <c>SELECT ... FOR UPDATE</c> on the profile row from a third session — no
    /// production seam, and nothing that the mutation removes. A test built that way would assert the end state itself
    /// rather than the wait, which is the better signal. It is not built because this suite already refuses the
    /// forbidden state at both doors; if either of those is ever relaxed, this is the test to write.</para>
    /// </summary>
    [Fact]
    public async Task A_route_moved_onto_a_profile_racing_that_profiles_repoint_serializes_and_the_loser_is_refused()
    {
        var world = await SeedActiveProfileAsync(UnusedRoot);
        var origin = await SeedSiblingActiveProfileAsync(world);
        var routeId = await SeedRouteAsync(world, origin, StorageRouteState.Active, StorageProfileRevisionMode.CurrentAtWrite);

        using var holder = _fixture.BeginScope();
        var held = await StorageProfileHeadLock.TakeAsync(holder.Resolve<CodeSpaceDbContext>().Database, world.ProfileId, CancellationToken.None);

        var move = Task.Run(() => MoveRouteAsync(world, routeId));
        var repoint = Task.Run(() => AppendLegacyRevisionAsync(world));
        var settled = await Task.WhenAny(move, repoint, Task.Delay(TimeSpan.FromSeconds(3)));

        settled.ShouldNotBe(move, "the route revision reached its write while another session held the TARGET profile's head lock, so it bound an active writer to a head it had only read in a snapshot. Diagnose with: psql -c \"SELECT granted, objid FROM pg_locks WHERE locktype = 'advisory'\".");
        settled.ShouldNotBe(repoint, "the profile revision reached its write while another session held this profile's head lock, so it counted the routes following this head without excluding the move that adds one.");

        await held.ShouldNotBeNull().DisposeAsync();

        var moveFailure = await SettleAsync(move);
        var repointFailure = await SettleAsync(repoint);

        new[] { moveFailure, repointFailure }.Count(failure => failure == null)
            .ShouldBe(1, "exactly one writer may be accepted — both accepted means each passed a guard the other was about to invalidate");
        (moveFailure ?? repointFailure).ShouldNotBeNull().Message
            .ShouldContain("accepts no new bytes", Case.Sensitive, "the loser is refused BY THE RULE, naming the capability, not by a lock timeout or a version mismatch");

        var head = await HeadProviderAsync(world);
        var follows = await FollowsHeadAsync(routeId, world.ProfileId);

        (head == LocalLegacyArtifactStorageDriverFactory.TypeKey && follows)
            .ShouldBeFalse("an Active route now follows a head that accepts no new bytes — the state both guards exist to forbid, committed because neither saw the other");
    }

    // ─── World + helpers ─────────────────────────────────────────────────────

    /// <summary>Awaits one racer and returns what refused it, or null when it was accepted.</summary>
    private static async Task<Exception?> SettleAsync(Task racer)
    {
        try
        {
            await racer.WaitAsync(TimeSpan.FromSeconds(30));
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private async Task<StorageProfileDetail?> AppendLegacyRevisionAsync(World world)
    {
        using var scope = _fixture.BeginScope();
        var profile = await scope.Resolve<CodeSpaceDbContext>().StorageProfile.AsNoTracking()
            .SingleAsync(value => value.TeamId == world.TeamId && value.Id == world.ProfileId);

        return await scope.Resolve<IStorageProfileService>().AppendRevisionAsync(world.TeamId, world.ActorId, new AppendStorageProfileRevisionCommand
        {
            ProfileId = world.ProfileId, ExpectedXmin = profile.Xmin, ExpectedCurrentRevision = profile.CurrentRevision,
            ProviderTypeKey = LocalLegacyArtifactStorageDriverFactory.TypeKey,
            NonSecretConfig = JsonDocument.Parse("""{"rootPath":"/unused/legacy"}""").RootElement,
        }, CancellationToken.None);
    }

    private async Task<string> HeadProviderAsync(World world)
    {
        using var scope = _fixture.BeginScope();
        var detail = (await scope.Resolve<IStorageProfileService>().GetAsync(world.TeamId, world.ProfileId, CancellationToken.None))!;

        return detail.Revisions.Single(revision => revision.Revision == detail.CurrentRevision).ProviderTypeKey;
    }

    /// <summary>
    /// Whether this route is an ACTIVE follower of that profile's head — route state alone is not the forbidden
    /// state here, because the route under test is already Active before the race and stays Active either way. What
    /// the race decides is whether its current revision came to name this profile at all.
    /// </summary>
    private async Task<bool> FollowsHeadAsync(Guid routeId, Guid profileId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var route = await db.StorageRoute.AsNoTracking().SingleAsync(value => value.Id == routeId);
        if (route.State != StorageRouteState.Active) return false;

        return await db.StorageRouteRevision.AsNoTracking().AnyAsync(value => value.StorageRouteId == routeId
            && value.Revision == route.CurrentRevision && value.StorageProfileId == profileId
            && value.ProfileRevisionMode == StorageProfileRevisionMode.CurrentAtWrite);
    }

    private async Task<StorageRouteState> RouteStateAsync(Guid routeId)
    {
        using var scope = _fixture.BeginScope();

        return (await scope.Resolve<CodeSpaceDbContext>().StorageRoute.AsNoTracking().SingleAsync(row => row.Id == routeId)).State;
    }

    private async Task<World> SeedProfileAsync(string rootPath)
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"repoint-{actorId:N}@test.local", Name = $"repoint-{actorId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"repoint-{teamId:N}", Name = "Storage Repoint Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        await db.SaveChangesAsync();

        var created = await scope.Resolve<IStorageProfileService>().CreateAsync(teamId, actorId, new CreateStorageProfileCommand
        {
            StableName = $"repoint-{Guid.NewGuid():N}", ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey,
            NonSecretConfig = JsonDocument.Parse(JsonSerializer.Serialize(new { rootPath })).RootElement,
        }, CancellationToken.None);

        return new World(teamId, actorId, created.Id);
    }

    /// <summary>A profile a route may actually name: binding one refuses any profile that is not Active.</summary>
    private async Task<World> SeedActiveProfileAsync(string rootPath)
    {
        var world = await SeedProfileAsync(rootPath);

        using var scope = _fixture.BeginScope();
        var profiles = scope.Resolve<IStorageProfileService>();
        var profile = (await profiles.GetAsync(world.TeamId, world.ProfileId, CancellationToken.None)).ShouldNotBeNull();

        (await profiles.SetStateAsync(world.TeamId, world.ActorId, new SetStorageProfileStateCommand
        {
            ProfileId = world.ProfileId, ExpectedXmin = profile.Xmin, ExpectedCurrentRevision = profile.CurrentRevision,
            State = StorageProfileStateValue.Active,
        }, CancellationToken.None)).ShouldNotBeNull();

        return world;
    }

    /// <summary>
    /// A SECOND Active profile in the same team — where a route lives before it is moved onto the contended one, so
    /// the move genuinely adds a follower to a head that had none rather than re-stating one it already had.
    /// </summary>
    private async Task<Guid> SeedSiblingActiveProfileAsync(World world)
    {
        using var scope = _fixture.BeginScope();
        var profiles = scope.Resolve<IStorageProfileService>();
        var created = await profiles.CreateAsync(world.TeamId, world.ActorId, new CreateStorageProfileCommand
        {
            StableName = $"repoint-origin-{Guid.NewGuid():N}", ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey,
            NonSecretConfig = JsonDocument.Parse(JsonSerializer.Serialize(new { rootPath = UnusedRoot })).RootElement,
        }, CancellationToken.None);

        (await profiles.SetStateAsync(world.TeamId, world.ActorId, new SetStorageProfileStateCommand
        {
            ProfileId = created.Id, ExpectedXmin = created.Xmin, ExpectedCurrentRevision = created.CurrentRevision,
            State = StorageProfileStateValue.Active,
        }, CancellationToken.None)).ShouldNotBeNull();

        return created.Id;
    }

    /// <summary>Appends a route revision naming this world's profile — the move that makes an already-Active route a writer of that profile's head.</summary>
    private async Task<StorageRouteDetail?> MoveRouteAsync(World world, Guid routeId)
    {
        using var scope = _fixture.BeginScope();
        var routes = scope.Resolve<IStorageRouteService>();
        var route = (await routes.GetAsync(world.TeamId, routeId, null, 10, CancellationToken.None)).ShouldNotBeNull();

        return await routes.AppendRevisionAsync(world.TeamId, world.ActorId, new AppendStorageRouteRevisionCommand
        {
            RouteId = routeId, ExpectedXmin = route.Xmin, ExpectedCurrentRevision = route.CurrentRevision,
            StorageProfileId = world.ProfileId, ProfileRevisionMode = StorageProfileRevisionModeValue.CurrentAtWrite,
        }, CancellationToken.None);
    }

    /// <summary>Binds a data class to this world's profile through the routing service. The route is born Draft.</summary>
    private async Task<Guid> BindRouteAsync(World world)
    {
        using var scope = _fixture.BeginScope();

        return (await scope.Resolve<IStorageRouteService>().CreateAsync(world.TeamId, world.ActorId, new CreateStorageRouteCommand
        {
            DataClassTypeKey = "agent-run-log/v1", StorageProfileId = world.ProfileId,
            ProfileRevisionMode = StorageProfileRevisionModeValue.CurrentAtWrite,
        }, CancellationToken.None)).Id;
    }

    private async Task<StorageRouteDetail?> ActivateRouteAsync(World world, Guid routeId)
    {
        using var scope = _fixture.BeginScope();
        var routes = scope.Resolve<IStorageRouteService>();
        var route = (await routes.GetAsync(world.TeamId, routeId, null, 10, CancellationToken.None)).ShouldNotBeNull();

        return await routes.SetStateAsync(world.TeamId, world.ActorId, new SetStorageRouteStateCommand
        {
            RouteId = routeId, ExpectedXmin = route.Xmin, ExpectedCurrentRevision = route.CurrentRevision,
            State = StorageRouteStateValue.Active,
        }, CancellationToken.None);
    }

    /// <summary>A storage_route row is forced to start Draft at revision 1 by a database trigger, so the target state is a follow-up update.</summary>
    private async Task<Guid> SeedRouteAsync(World world, Guid profileId, StorageRouteState state, StorageProfileRevisionMode mode)
    {
        var now = DateTimeOffset.UtcNow;
        var route = new StorageRoute
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, DataClassTypeKey = $"repoint-{Guid.NewGuid():N}/v1", CurrentRevision = 1,
            State = StorageRouteState.Draft, CreatedDate = now, CreatedBy = world.ActorId, LastModifiedDate = now, LastModifiedBy = world.ActorId,
        };
        route.Revisions.Add(new StorageRouteRevision
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, StorageRouteId = route.Id, Revision = 1,
            StorageProfileId = profileId, ProfileRevisionMode = mode,
            PinnedProfileRevision = mode == StorageProfileRevisionMode.Pinned ? 1 : null,
            CreatedDate = now, CreatedBy = world.ActorId,
        });

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.StorageRoute.Add(route);
        await db.SaveChangesAsync();
        await db.StorageRoute.Where(value => value.Id == route.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.State, state));

        return route.Id;
    }

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "codespace-repoint", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed record World(Guid TeamId, Guid ActorId, Guid ProfileId);
}
