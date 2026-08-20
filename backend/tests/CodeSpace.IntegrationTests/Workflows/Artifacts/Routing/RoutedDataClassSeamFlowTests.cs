using System.Security.Cryptography;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing.Exceptions;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts.Routing;

/// <summary>
/// 🟢 High fidelity: a THIRD routed data class that exists only in this file, driving the real
/// <see cref="IRoutedDestinationResolver"/>, the real <c>ArtifactCasRuntimeCoordinator</c> and the real local-rwx
/// driver against real Postgres and two real temp roots. It has no resolver, no destination union, no problem enum and
/// no read query of its own — a declaration and a call site are the whole of it, which is the enforceable form of the
/// claim that a new data class is a declaration rather than a second implementation of the same policy.
///
/// <para>Two profiles of the SAME provider kind with DIFFERENT roots, so nothing here depends on a second provider's
/// availability and the file counts pin WHICH root the bytes physically left for.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RoutedDataClassSeamFlowTests : IDisposable
{
    private const string ProviderTypeKey = "local-rwx/v1";
    private const string SyntheticKey = "synthetic-probe/v1";

    private readonly PostgresFixture _fixture;
    private readonly string _rootA = Path.Combine(Path.GetTempPath(), "codespace-seam-a", Guid.NewGuid().ToString("N"));
    private readonly string _rootB = Path.Combine(Path.GetTempPath(), "codespace-seam-b", Guid.NewGuid().ToString("N"));

    public RoutedDataClassSeamFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_declared_class_resolves_its_own_route_and_reads_back_through_the_revision_it_recorded()
    {
        var world = await SeedAsync(routeToProfileA: true);
        var payload = "declared-and-nothing-else\n"u8.ToArray();

        var destination = await ResolveAsync(new SyntheticDataClass(), world.TeamId);
        destination.ShouldBe(new RoutedDestination.Routed(world.ProfileAId, 1), "an Active route for the declared key resolves to its frozen coordinates");

        var objectId = await PutAsync(world, (RoutedDestination.Routed)destination, payload);

        FileCountUnder(_rootA).ShouldBeGreaterThan(0, "the write must have gone through the routed profile");
        FileCountUnder(_rootB).ShouldBe(0, "profile B holds nothing yet — it is not even routed");

        await RepointRouteAsync(world, world.ProfileBId);

        (await ResolveAsync(new SyntheticDataClass(), world.TeamId))
            .ShouldBe(new RoutedDestination.Routed(world.ProfileBId, 1), "the next write follows the switched route");

        var recorded = await RecordedAsync(world.TeamId, objectId);

        recorded.Select(location => (location.StorageProfileId, location.StorageProfileRevision))
            .ShouldBe([(world.ProfileAId, 1)], "the recorded ledger, not current routing policy, is what a read resolves through");
        (await OpenAsync(world.TeamId, objectId, recorded[0])).ShouldBe(payload);
    }

    /// <summary>
    /// The one axis a declaration can change, against real routing tables rather than a stub. A team with no route is
    /// the shipped state of every team that never configured one, so it is the state both answers have to be right for.
    /// </summary>
    [Fact]
    public async Task A_team_that_never_routed_the_class_gets_the_answer_its_declaration_asked_for()
    {
        var world = await SeedAsync(routeToProfileA: false);

        (await ResolveAsync(new SyntheticDataClass(), world.TeamId))
            .ShouldBe(new RoutedDestination.Unusable(RoutedDestinationDisposition.NoRoute), "no local home was declared, so there is nowhere to degrade to");
        (await ResolveAsync(new SyntheticDataClassWithLocalHome(), world.TeamId))
            .ShouldBe(new RoutedDestination.Local(RoutedDestinationDisposition.NoRoute), "the declared local home takes the write instead");
    }

    /// <summary>
    /// A route an operator created and never activated. Pressing "Create data route" must not start refusing writes for
    /// a class that has a local home, and must not start accepting them for a class that has none.
    /// </summary>
    [Fact]
    public async Task A_route_created_and_never_activated_is_pre_cutover_for_both_declarations()
    {
        var world = await SeedAsync(routeToProfileA: true, activate: false);

        (await ResolveAsync(new SyntheticDataClass(), world.TeamId))
            .ShouldBe(new RoutedDestination.Unusable(RoutedDestinationDisposition.RouteNotActivated));
        (await ResolveAsync(new SyntheticDataClassWithLocalHome(), world.TeamId))
            .ShouldBe(new RoutedDestination.Local(RoutedDestinationDisposition.RouteNotActivated));
    }

    /// <summary>
    /// Why the routes above are seeded as rows instead of created through the service: route CREATION is gated on the
    /// catalog of data classes declared in the Core assembly, and this file's declaration is not one of them. That gate
    /// is the feature — a route for a key no consumer reads would list as configured storage and never move a byte — so
    /// the honest cost of a real third data class is one declaration file inside Core, which this test states rather
    /// than works around silently.
    /// </summary>
    [Fact]
    public async Task Settings_refuses_to_create_a_route_for_a_key_no_consumer_in_this_build_reads()
    {
        var world = await SeedAsync(routeToProfileA: false);

        using var scope = _fixture.BeginScope();
        var create = () => scope.Resolve<IStorageRouteService>().CreateAsync(world.TeamId, world.ActorId, new CreateStorageRouteCommand
        {
            DataClassTypeKey = SyntheticKey, StorageProfileId = world.ProfileAId,
        }, CancellationToken.None);

        (await create.ShouldThrowAsync<StorageRouteInvalidException>()).Message
            .ShouldContain("workflow-artifact/v1", Case.Sensitive, "the refusal names the classes this build does read");
    }

    private async Task<RoutedDestination> ResolveAsync(IRoutedDataClass dataClass, Guid teamId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<IRoutedDestinationResolver>().ResolveAsync(dataClass, teamId, CancellationToken.None);
    }

    private async Task<Guid> PutAsync(World world, RoutedDestination.Routed routed, byte[] payload)
    {
        using var scope = _fixture.BeginScope();
        using var content = new MemoryStream(payload, writable: false);
        var sha = Convert.ToHexStringLower(SHA256.HashData(payload));

        var result = await scope.Resolve<IArtifactCasRuntimeCoordinator>().PutAsync(new ArtifactCasTransferRequest
        {
            TeamId = world.TeamId, StorageProfileId = routed.StorageProfileId, StorageProfileRevision = routed.StorageProfileRevision,
            IdempotencyScope = $"{SyntheticKey}/{sha}", TargetObjectKey = $"synthetic-probe/{sha[..2]}/{sha}",
            Content = content, ExpectedSizeBytes = payload.Length, ExpectedSha256 = sha,
            ContentType = "application/octet-stream", ActorId = world.ActorId,
        }, CancellationToken.None);

        return result.ShouldBeOfType<ArtifactCasTransferResult.Committed>().ArtifactObjectId;
    }

    /// <summary>The shared read seam, used exactly as a data class with no code of its own would use it.</summary>
    private async Task<IReadOnlyList<RecordedArtifactLocation>> RecordedAsync(Guid teamId, Guid artifactObjectId)
    {
        using var scope = _fixture.BeginScope();

        return await RecordedArtifactLocations.AvailableFor(scope.Resolve<CodeSpaceDbContext>(), teamId)
            .Where(location => location.ArtifactObjectId == artifactObjectId)
            .OrderByDescending(location => location.VerifiedAt).ThenBy(location => location.LocationId)
            .ToListAsync();
    }

    private async Task<byte[]> OpenAsync(Guid teamId, Guid artifactObjectId, RecordedArtifactLocation recorded)
    {
        using var scope = _fixture.BeginScope();
        var result = await scope.Resolve<IArtifactCasRuntimeCoordinator>().OpenReadAsync(new ArtifactCasReadRequest
        {
            TeamId = teamId, ArtifactObjectId = artifactObjectId,
            StorageProfileId = recorded.StorageProfileId, StorageProfileRevision = recorded.StorageProfileRevision,
        }, CancellationToken.None);

        await using var content = result.ShouldBeOfType<ArtifactCasReadResult.Opened>().Content;
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer);

        return buffer.ToArray();
    }

    /// <summary>Point the route at another profile through the SERVICE an operator's request runs — only creation is catalog-gated, so an existing route revises normally.</summary>
    private async Task RepointRouteAsync(World world, Guid profileId)
    {
        using var scope = _fixture.BeginScope();
        var routes = scope.Resolve<IStorageRouteService>();
        var current = (await routes.GetAsync(world.TeamId, world.RouteId!.Value, null, 10, CancellationToken.None)).ShouldNotBeNull();

        var appended = await routes.AppendRevisionAsync(world.TeamId, world.ActorId, new AppendStorageRouteRevisionCommand
        {
            RouteId = world.RouteId.Value, ExpectedXmin = current.Xmin, ExpectedCurrentRevision = current.CurrentRevision,
            StorageProfileId = profileId,
        }, CancellationToken.None);

        appended.ShouldNotBeNull().CurrentTarget.StorageProfileId.ShouldBe(profileId);
    }

    private static int FileCountUnder(string root) =>
        Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count() : 0;

    private async Task<World> SeedAsync(bool routeToProfileA, bool activate = true)
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"seam-{actorId:N}@test.local", Name = $"seam-{actorId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"seam-{teamId:N}", Name = "Routed Seam Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });

        var profileA = AddProfile(db, teamId, actorId, now, _rootA, "seam-a");
        var profileB = AddProfile(db, teamId, actorId, now, _rootB, "seam-b");
        var routeId = routeToProfileA ? AddRoute(db, teamId, actorId, now, profileA) : (Guid?)null;

        await db.SaveChangesAsync();

        var world = new World(teamId, actorId, profileA, profileB, routeId);
        if (routeId != null && activate) await ActivateRouteAsync(world);

        return world;
    }

    /// <summary>
    /// Activation goes through the SERVICE, which is where the route lifecycle rules live. Only the seeded row itself
    /// steps around the catalog gate; a route is still born Draft at revision 1 — the database's own
    /// <c>storage_route</c> identity trigger refuses anything else — and reaches Active as a second, guarded step.
    /// </summary>
    private async Task ActivateRouteAsync(World world)
    {
        using var scope = _fixture.BeginScope();
        var routes = scope.Resolve<IStorageRouteService>();
        var current = (await routes.GetAsync(world.TeamId, world.RouteId!.Value, null, 10, CancellationToken.None)).ShouldNotBeNull();

        var activated = await routes.SetStateAsync(world.TeamId, world.ActorId, new SetStorageRouteStateCommand
        {
            RouteId = world.RouteId.Value, ExpectedXmin = current.Xmin, ExpectedCurrentRevision = current.CurrentRevision,
            State = StorageRouteStateValue.Active,
        }, CancellationToken.None);

        activated.ShouldNotBeNull().State.ShouldBe(StorageRouteStateValue.Active);
    }

    private static Guid AddRoute(CodeSpaceDbContext db, Guid teamId, Guid actorId, DateTimeOffset now, Guid profileId)
    {
        var route = new StorageRoute
        {
            Id = Guid.NewGuid(), TeamId = teamId, DataClassTypeKey = SyntheticKey, CurrentRevision = 1,
            State = StorageRouteState.Draft,
            CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        };
        route.Revisions.Add(new StorageRouteRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageRouteId = route.Id, Revision = 1, StorageProfileId = profileId,
            ProfileRevisionMode = StorageProfileRevisionMode.CurrentAtWrite, CreatedDate = now, CreatedBy = actorId,
        });
        db.StorageRoute.Add(route);

        return route.Id;
    }

    private static Guid AddProfile(CodeSpaceDbContext db, Guid teamId, Guid actorId, DateTimeOffset now, string rootPath, string namePrefix)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { rootPath = Path.GetFullPath(rootPath) }));
        var canonicalConfig = StorageProfileRules.CanonicalJson(document.RootElement);
        using var canonical = JsonDocument.Parse(canonicalConfig);
        var profile = new StorageProfile
        {
            Id = Guid.NewGuid(), TeamId = teamId, StableName = $"{namePrefix}-{Guid.NewGuid():N}",
            State = StorageProfileState.Active, CurrentRevision = 1,
            CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        };
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageProfileId = profile.Id, Revision = 1,
            ProviderTypeKey = ProviderTypeKey, NonSecretConfigJson = canonicalConfig, CredentialRef = null,
            NamespaceFingerprint = StorageProfileRules.NamespaceFingerprint(ProviderTypeKey, canonical.RootElement),
            CreatedDate = now, CreatedBy = actorId,
        });
        db.StorageProfile.Add(profile);

        return profile.Id;
    }

    public void Dispose()
    {
        foreach (var root in new[] { _rootA, _rootB })
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A third routed data class, declared and nothing more. No local home, so it refuses until it is cut over.</summary>
    private sealed record SyntheticDataClass : IRoutedDataClass
    {
        public string TypeKey => SyntheticKey;

        public string DisplayName => "Synthetic probe";
    }

    /// <summary>The same declaration plus the one optional capability, which is the only thing that changes its policy.</summary>
    private sealed record SyntheticDataClassWithLocalHome : IRoutedDataClass, IRoutedDataClassLocalFallback
    {
        public string TypeKey => SyntheticKey;

        public string DisplayName => "Synthetic probe with a local home";
    }

    private sealed record World(Guid TeamId, Guid ActorId, Guid ProfileAId, Guid ProfileBId, Guid? RouteId);
}
