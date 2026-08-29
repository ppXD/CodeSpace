using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Infrastructure;

/// <summary>
/// Gives a team a working cloud-shaped destination and writes bytes to it through the REAL store.
///
/// <para>Every consumer of artifact bytes — the diff drawer, the agent launch path, a raw download — has tests that
/// seed an inline row or a local <c>storage_url</c>, and those exercise neither the location ledger nor a driver. A
/// consumer proven against them is proven against the one shape a configured deployment never has.</para>
///
/// <para>The write goes through <see cref="IArtifactStore"/> rather than hand-inserted rows on purpose: a fabricated
/// <c>artifact_location</c> is exactly the over-generous fake that makes a consumer test pass over state production
/// cannot produce. What this returns is what a real offloaded write leaves behind.</para>
/// </summary>
public static class RoutedArtifactSeed
{
    /// <summary>Comfortably past <c>ArtifactStoreConfig.InlineThresholdBytes</c>, so the bytes MUST leave the row.</summary>
    public const int OffloadedSize = 32 * 1024;

    /// <summary>
    /// Points this team's workflow-artifact writes at a routed profile, and returns the root the bytes will land under
    /// so a caller can assert they physically arrived.
    /// </summary>
    public static async Task<RoutedDestination> RouteTeamAsync(PostgresFixture fixture, Guid teamId, Guid actorId, string dataClassTypeKey = "workflow-artifact/v1")
    {
        var root = Path.Combine(Path.GetTempPath(), "codespace-routed-seed", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var profileId = Guid.NewGuid();
        var routeId = Guid.NewGuid();

        var profile = new StorageProfile
        {
            Id = profileId, TeamId = teamId, StableName = $"routed-seed-{profileId:N}", CurrentRevision = 1,
            State = StorageProfileState.Active, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        };
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageProfileId = profileId, Revision = 1,
            ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey,
            NonSecretConfigJson = JsonSerializer.Serialize(new { rootPath = root }), CredentialRef = null,
            NamespaceFingerprint = Fingerprint(profileId), CreatedDate = now, CreatedBy = actorId,
        });
        db.StorageProfile.Add(profile);
        db.StorageRoute.Add(new StorageRoute
        {
            Id = routeId, TeamId = teamId, DataClassTypeKey = dataClassTypeKey, CurrentRevision = 1,
            State = StorageRouteState.Draft, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
            Revisions =
            {
                new StorageRouteRevision
                {
                    Id = Guid.NewGuid(), TeamId = teamId, StorageRouteId = routeId, Revision = 1, StorageProfileId = profileId,
                    ProfileRevisionMode = StorageProfileRevisionMode.CurrentAtWrite, PinnedProfileRevision = null,
                    CreatedDate = now, CreatedBy = actorId,
                },
            },
        });
        await db.SaveChangesAsync();

        // Activated by SQL rather than through the route service: 0134's trigger requires a route to be born Draft,
        // and going through SetStateAsync would make every consumer's fixture depend on the activation probe as well.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE storage_route SET state = 'Active' WHERE id = {routeId}");

        return new RoutedDestination(profileId, routeId, root);
    }

    /// <summary>
    /// Writes bytes through the real store and asserts they actually left the database. The assertion is the point:
    /// without it a threshold change, or a team the route did not reach, would silently turn every consumer test back
    /// into an inline test that proves nothing about a provider.
    /// </summary>
    public static async Task<Guid> WriteRoutedAsync(PostgresFixture fixture, Guid teamId, string content, string contentType = "text/x-diff")
    {
        var bytes = Payload(content);

        using var scope = fixture.BeginScope();
        var artifactId = await scope.Resolve<IArtifactStore>().PutAsync(teamId, bytes, contentType, CancellationToken.None);

        var row = await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifact.AsNoTracking().SingleAsync(value => value.Id == artifactId);
        row.InlineBytes.ShouldBeNull("a routed fixture that stayed inline exercises no driver and no location ledger");
        row.StorageUrl.ShouldBeNull("a routed fixture that fell back to the local backend proves nothing about a provider");
        row.CasArtifactObjectId.ShouldNotBeNull("the bytes must be reachable only through the location ledger");

        return artifactId;
    }

    /// <summary>Deterministic filler past the inline threshold, with the caller's content at the head so an assertion can still recognise it.</summary>
    public static byte[] Payload(string content)
    {
        var head = System.Text.Encoding.UTF8.GetBytes(content + "\n");
        var bytes = new byte[Math.Max(OffloadedSize, head.Length + 1)];
        head.CopyTo(bytes, 0);
        for (var i = head.Length; i < bytes.Length; i++) bytes[i] = (byte)('a' + (i % 26));

        return bytes;
    }

    private static string Fingerprint(Guid profileId) => $"sha256:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(profileId.ToByteArray())).ToLowerInvariant()}";

    /// <summary>Where a routed fixture's bytes physically land, so a test can count files under it.</summary>
    public sealed record RoutedDestination(Guid ProfileId, Guid RouteId, string Root)
    {
        public int ObjectCount => Directory.Exists(Root) ? Directory.GetFiles(Root, "*", SearchOption.AllDirectories).Length : 0;
    }
}
