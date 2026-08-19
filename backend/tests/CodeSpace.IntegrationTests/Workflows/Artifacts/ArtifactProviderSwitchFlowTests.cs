using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts;

/// <summary>
/// 🟢 High fidelity: the real <see cref="IArtifactStore"/> against real Postgres and the real local-rwx driver writing
/// to real temp directories. Two profiles of the SAME provider kind with DIFFERENT roots, so nothing here depends on a
/// second provider's availability and any failure is about revision resolution alone.
///
/// The property under test is the one an operator actually asks before adopting routing: <b>if I point the artifact class
/// at a different provider, can I still open everything written before the switch?</b> The answer is supposed to be yes,
/// because a read resolves through the profile revision the object's own location ledger recorded and never through
/// current routing policy. That is a load-bearing claim about durability, and until this file it was asserted only by a
/// doc-comment on <c>ArtifactStore.Routing.OpenRoutedAsync</c> — the read path had no test crossing a switch at all.
///
/// Every payload here EXCEEDS <c>ArtifactStoreConfig.InlineThresholdBytes</c> deliberately. At or below it the store keeps
/// the bytes inline in the DB row and no provider is touched at all, so a smaller payload makes every assertion below
/// pass while testing nothing about routing — the first draft of this file did exactly that. <c>AssertOffloadedAsync</c>
/// pins the write actually left the database, and the file counts pin WHICH provider root it left for; a read that fell
/// through to the legacy backend would otherwise return the right bytes for the wrong reason.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ArtifactProviderSwitchFlowTests : IDisposable
{
    private const string ProviderTypeKey = "local-rwx/v1";

    /// <summary>Comfortably over the inline threshold, so every write is a real provider write.</summary>
    private const int OffloadedSize = 32 * 1024;

    private readonly PostgresFixture _fixture;
    private readonly string _rootA = Path.Combine(Path.GetTempPath(), "codespace-switch-a", Guid.NewGuid().ToString("N"));
    private readonly string _rootB = Path.Combine(Path.GetTempPath(), "codespace-switch-b", Guid.NewGuid().ToString("N"));

    public ArtifactProviderSwitchFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_artifact_written_before_the_switch_still_opens_from_its_own_provider_after_it()
    {
        var world = await SeedAsync();
        var before = Payload("written-under-profile-a");

        var beforeId = await PutAsync(world.TeamId, before);

        await AssertOffloadedAsync(world.TeamId, beforeId);
        FileCountUnder(_rootA).ShouldBeGreaterThan(0, "the write must have gone through the routed profile, not the legacy backend");
        FileCountUnder(_rootB).ShouldBe(0, "profile B holds nothing yet — it is not even routed");

        await RepointRouteAsync(world, world.ProfileBId);

        var after = Payload("written-under-profile-b");
        var afterId = await PutAsync(world.TeamId, after);

        (await GetBytesAsync(world.TeamId, afterId)).ShouldBe(after, "a new write follows the switched route");
        FileCountUnder(_rootB).ShouldBeGreaterThan(0, "and lands physically under profile B's root");

        (await GetBytesAsync(world.TeamId, beforeId)).ShouldBe(before, "the pre-switch artifact still opens — its location ledger, not current routing, chose the profile");
    }

    [Fact]
    public async Task A_range_read_of_a_pre_switch_artifact_also_resolves_through_the_recorded_revision()
    {
        // The range path builds its own request from the recorded stamp (ArtifactStore.Routing.RangeRequestFor), so it can
        // regress independently of the whole-object read. A UI opening a large artifact's preview uses this path.
        var world = await SeedAsync();
        var bytes = Payload("range-source").Concat("TAIL"u8.ToArray()).ToArray();

        var artifactId = await PutAsync(world.TeamId, bytes);
        await RepointRouteAsync(world, world.ProfileBId);

        using var scope = _fixture.BeginScope();
        var read = await scope.Resolve<IArtifactRangeReader>().ReadRangeAsync(world.TeamId, artifactId, bytes.Length - 4, 4, CancellationToken.None);

        read.State.ShouldBe(ArtifactRangeReadState.Available, "a range read after the switch still reaches profile A");
        read.Bytes.ShouldBe("TAIL"u8.ToArray());
    }

    [Fact]
    public async Task A_pre_switch_artifact_survives_its_provider_being_retired_after_the_switch()
    {
        // The realistic decommission order: switch the route away, then wind the old profile down. Reads of everything it
        // still holds must keep working, or "switch provider" silently means "lose the history".
        var world = await SeedAsync();
        var bytes = Payload("outlives-decommission");

        var artifactId = await PutAsync(world.TeamId, bytes);
        await RepointRouteAsync(world, world.ProfileBId);

        foreach (var state in new[] { StorageProfileState.Disabled, StorageProfileState.Retired })
        {
            await SetProfileStateAsync(world.TeamId, world.ProfileAId, state);

            (await GetBytesAsync(world.TeamId, artifactId)).ShouldBe(bytes, $"a {state} profile must still serve the bytes its own revision stamped");
        }
    }

    [Fact]
    public async Task Switching_back_does_not_strand_what_the_other_profile_holds()
    {
        // Two switches, one artifact each. Neither read may depend on which profile the route happens to name now — the
        // failure this catches is a read path that resolves the CURRENT route and gets away with it while nothing moved.
        var world = await SeedAsync();
        var underA = Payload("under-a");
        var underB = Payload("under-b");

        var idA = await PutAsync(world.TeamId, underA);
        await RepointRouteAsync(world, world.ProfileBId);
        var idB = await PutAsync(world.TeamId, underB);
        await RepointRouteAsync(world, world.ProfileAId);

        (await GetBytesAsync(world.TeamId, idA)).ShouldBe(underA, "written under A, routed to A");
        (await GetBytesAsync(world.TeamId, idB)).ShouldBe(underB, "written under B, routed to A — the read must ignore the route entirely");
    }

    /// <summary>A payload over the inline threshold, unique per call so two writes never collide on content-addressed identity.</summary>
    private static byte[] Payload(string tag)
    {
        var seed = Encoding.UTF8.GetBytes($"{tag}-{Guid.NewGuid():N}-");

        return Enumerable.Range(0, OffloadedSize / seed.Length + 1).SelectMany(_ => seed).Take(OffloadedSize).ToArray();
    }

    /// <summary>Fail loudly if the write never left the database — an inline row would make every other assertion here vacuous.</summary>
    private async Task AssertOffloadedAsync(Guid teamId, Guid artifactId)
    {
        using var scope = _fixture.BeginScope();
        var row = await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifact.AsNoTracking()
            .SingleAsync(value => value.TeamId == teamId && value.Id == artifactId);

        row.InlineBytes.ShouldBeNull("the payload must exceed the inline threshold, or no provider is exercised at all");
        row.CasArtifactObjectId.ShouldNotBeNull("an offloaded write through an active route records a CAS object");
    }

    private async Task<Guid> PutAsync(Guid teamId, byte[] bytes)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<IArtifactStore>().PutAsync(teamId, bytes, "application/octet-stream", CancellationToken.None);
    }

    private async Task<byte[]> GetBytesAsync(Guid teamId, Guid artifactId)
    {
        using var scope = _fixture.BeginScope();
        var read = await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, artifactId, CancellationToken.None);

        return read.ShouldNotBeNull("the artifact must still be readable").Bytes.ToArray();
    }

    /// <summary>Point the class's route at another profile through the SERVICE an operator's request runs — a new route revision on the live route, not a hand-written row.</summary>
    private async Task RepointRouteAsync(World world, Guid profileId)
    {
        using var scope = _fixture.BeginScope();
        var routes = scope.Resolve<IStorageRouteService>();
        var current = (await routes.GetAsync(world.TeamId, world.RouteId, null, 10, CancellationToken.None)).ShouldNotBeNull();

        var appended = await routes.AppendRevisionAsync(world.TeamId, world.ActorId, new AppendStorageRouteRevisionCommand
        {
            RouteId = world.RouteId, ExpectedXmin = current.Xmin, ExpectedCurrentRevision = current.CurrentRevision,
            StorageProfileId = profileId,
        }, CancellationToken.None);

        appended.ShouldNotBeNull().CurrentTarget.StorageProfileId.ShouldBe(profileId, "the route's live revision now names the new profile");
    }

    private async Task SetProfileStateAsync(Guid teamId, Guid profileId, StorageProfileState state)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var profile = await db.StorageProfile.SingleAsync(row => row.TeamId == teamId && row.Id == profileId);

        profile.State = state;
        profile.LastModifiedDate = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync();
    }

    private static int FileCountUnder(string root) =>
        Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count() : 0;

    private async Task<World> SeedAsync()
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"switch-{actorId:N}@test.local", Name = $"switch-{actorId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"switch-{teamId:N}", Name = "Provider Switch Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });

        var profileA = AddProfile(db, teamId, actorId, now, _rootA, "switch-a");
        var profileB = AddProfile(db, teamId, actorId, now, _rootB, "switch-b");

        await db.SaveChangesAsync();

        // Create + activate through the service, which is where the route's lifecycle rules live (a route is born Draft at
        // revision 1 and activated as a second step). Seeding the rows by hand would step around the guards that make the
        // rest of this file meaningful.
        var routes = scope.Resolve<IStorageRouteService>();
        var created = await routes.CreateAsync(teamId, actorId, new CreateStorageRouteCommand
        {
            DataClassTypeKey = WorkflowArtifactDestinationResolver.DataClassTypeKey, StorageProfileId = profileA,
        }, CancellationToken.None);

        var activated = await routes.SetStateAsync(teamId, actorId, new SetStorageRouteStateCommand
        {
            RouteId = created.Id, ExpectedXmin = created.Xmin, ExpectedCurrentRevision = created.CurrentRevision,
            State = StorageRouteStateValue.Active,
        }, CancellationToken.None);

        activated.ShouldNotBeNull().State.ShouldBe(StorageRouteStateValue.Active);

        return new World(teamId, actorId, profileA, profileB, created.Id);
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

    private sealed record World(Guid TeamId, Guid ActorId, Guid ProfileAId, Guid ProfileBId, Guid RouteId);
}
