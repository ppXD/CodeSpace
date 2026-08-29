using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Failures;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Storage;

/// <summary>
/// The byte-serving surface over real HTTP.
///
/// <para>No suite anywhere hit a route that returns bytes: the E2E project's routes are auth, teams, storage admin,
/// repositories, sessions and workflow runs. So controller status mapping, header emission and <c>File()</c> streaming
/// of provider-sourced bytes were unproven end to end — and the failure path most of all, which is the one an operator
/// meets when a destination stops serving.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class ArtifactDownloadEndpointE2ETests : IClassFixture<TaskLaunchApiFactory>, IDisposable
{
    private readonly TaskLaunchApiFactory _factory;
    private readonly List<string> _roots = [];

    public ArtifactDownloadEndpointE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task An_artifact_whose_bytes_live_at_a_provider_downloads_over_http()
    {
        var world = await SeedRoutedTeamAsync();
        var payload = Payload("bytes that only exist at the destination");
        var artifactId = await PutAsync(world.TeamId, payload);

        var response = await SendAsync(world, artifactId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(payload, "every byte must survive the driver, the controller and the wire");
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/octet-stream");
    }

    [Fact]
    public async Task An_artifact_whose_destination_stopped_serving_answers_a_typed_failure_not_a_masked_500()
    {
        // The response an operator actually meets after a bucket is emptied or a key is revoked. A masked 500 would
        // tell them nothing, and a 200 with zero bytes would render as an empty file.
        var world = await SeedRoutedTeamAsync();
        var artifactId = await PutAsync(world.TeamId, Payload("about to vanish"));

        Directory.Delete(world.Root, recursive: true);

        var response = await SendAsync(world, artifactId);

        response.StatusCode.ShouldNotBe(HttpStatusCode.OK, "serving nothing as success is the one outcome that must never happen");
        response.StatusCode.ShouldNotBe(HttpStatusCode.InternalServerError, "content that is unavailable is a known state, not an unhandled fault");

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("code").GetString().ShouldBe(FailureCodes.ArtifactContentUnavailable,
            "the wire code is what lets a client tell 'the destination is not serving' from 'you may not have this'");
    }

    [Fact]
    public async Task A_foreign_team_cannot_download_another_teams_artifact()
    {
        // Team scope on a byte route is enforced by the handler, never the URL. Proven over HTTP because that is where
        // the ambient X-Team-Id header actually arrives.
        var owner = await SeedRoutedTeamAsync();
        var stranger = await SeedRoutedTeamAsync();
        var artifactId = await PutAsync(owner.TeamId, Payload("not yours"));

        var response = await SendAsync(stranger, artifactId);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a foreign id and an absent one must be indistinguishable, or the response leaks existence");
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused_before_any_byte_is_read()
    {
        var world = await SeedRoutedTeamAsync();
        var artifactId = await PutAsync(world.TeamId, Payload("private"));

        var response = await SendAsync(world, artifactId, authenticated: false);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ─── World + helpers ─────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> SendAsync(RoutedTeam world, Guid artifactId, bool authenticated = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/artifacts/{artifactId}");
        if (authenticated) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken.Mint(world.UserId, TestToken.SeedStamp));
        request.Headers.Add("X-Team-Id", world.TeamId.ToString());

        return await _factory.CreateClient().SendAsync(request);
    }

    private async Task<Guid> PutAsync(Guid teamId, byte[] payload)
    {
        using var scope = _factory.Services.CreateScope();
        var artifactId = await scope.ServiceProvider.GetRequiredService<IArtifactStore>()
            .PutAsync(teamId, payload, "application/octet-stream", CancellationToken.None);

        var row = await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().WorkflowArtifact.AsNoTracking()
            .SingleAsync(value => value.Id == artifactId);
        row.CasArtifactObjectId.ShouldNotBeNull("a fixture that stayed inline or fell back to local disk proves nothing about a provider");

        return artifactId;
    }

    /// <summary>Past the inline threshold, so the bytes must leave the row.</summary>
    private static byte[] Payload(string content)
    {
        var head = System.Text.Encoding.UTF8.GetBytes(content + "\n");
        var bytes = new byte[32 * 1024];
        head.CopyTo(bytes, 0);
        for (var i = head.Length; i < bytes.Length; i++) bytes[i] = (byte)('a' + (i % 26));

        return bytes;
    }

    private async Task<RoutedTeam> SeedRoutedTeamAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "codespace-http-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.User.Add(new User { Id = userId, SecurityStamp = TestToken.SeedStamp, Email = $"download-{suffix}@test.local", Name = "Download", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.Add(new Team { Id = teamId, Slug = $"download-{suffix}", Name = "Download", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        var profile = new StorageProfile
        {
            Id = profileId, TeamId = teamId, StableName = $"download-{suffix}", CurrentRevision = 1,
            State = StorageProfileState.Active, CreatedDate = now, CreatedBy = userId, LastModifiedDate = now, LastModifiedBy = userId,
        };
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageProfileId = profileId, Revision = 1,
            ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey,
            NonSecretConfigJson = JsonSerializer.Serialize(new { rootPath = root }), CredentialRef = null,
            NamespaceFingerprint = $"sha256:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(profileId.ToByteArray())).ToLowerInvariant()}",
            CreatedDate = now, CreatedBy = userId,
        });
        db.StorageProfile.Add(profile);
        db.StorageRoute.Add(new StorageRoute
        {
            Id = routeId, TeamId = teamId, DataClassTypeKey = "workflow-artifact/v1", CurrentRevision = 1,
            State = StorageRouteState.Draft, CreatedDate = now, CreatedBy = userId, LastModifiedDate = now, LastModifiedBy = userId,
            Revisions =
            {
                new StorageRouteRevision
                {
                    Id = Guid.NewGuid(), TeamId = teamId, StorageRouteId = routeId, Revision = 1, StorageProfileId = profileId,
                    ProfileRevisionMode = StorageProfileRevisionMode.CurrentAtWrite, PinnedProfileRevision = null,
                    CreatedDate = now, CreatedBy = userId,
                },
            },
        });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE storage_route SET state = 'Active' WHERE id = {routeId}");

        return new RoutedTeam(userId, teamId, root);
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

    private sealed record RoutedTeam(Guid UserId, Guid TeamId, string Root);
}
