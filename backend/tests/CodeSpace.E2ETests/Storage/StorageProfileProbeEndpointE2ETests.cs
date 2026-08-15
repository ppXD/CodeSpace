using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Storage;

/// <summary>Storage runtime qualification through the real HTTP/auth/EF/Postgres/driver stack.</summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class StorageProfileProbeEndpointE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;

    public StorageProfileProbeEndpointE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task Admin_probes_current_or_requested_exact_local_rwx_revision_without_changing_profile_state()
    {
        var world = await SeedWorldAsync(TeamRole.Admin);
        var root = Path.Combine(Path.GetTempPath(), "codespace-storage-probe", Guid.NewGuid().ToString("N"));
        var oldRoot = Path.Combine(root, "old");
        var currentRoot = Path.Combine(root, "current");
        try
        {
            var profileId = await SeedProfileAsync(world.TeamId, world.UserId, StorageProfileState.Active, (1, oldRoot), (2, currentRoot));

            var current = await SendAsync(world.UserId, world.TeamId, HttpMethod.Post, $"/api/storage/profiles/{profileId}/probe", new { verifyWriteAccess = true });
            current.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(current));
            var currentJson = await JsonAsync(current);
            currentJson.GetProperty("profileRevision").GetInt32().ShouldBe(2);
            currentJson.GetProperty("providerTypeKey").GetString().ShouldBe("local-rwx/v1");
            currentJson.GetProperty("status").GetString().ShouldBe("Available");
            currentJson.GetProperty("writeAccessRequested").GetBoolean().ShouldBeTrue();
            currentJson.GetProperty("latencyMilliseconds").GetInt64().ShouldBeGreaterThanOrEqualTo(0);
            Directory.Exists(currentRoot).ShouldBeTrue();
            Directory.Exists(oldRoot).ShouldBeFalse("omitting a revision must open only the persisted current revision");

            var exact = await SendAsync(world.UserId, world.TeamId, HttpMethod.Post, $"/api/storage/profiles/{profileId}/probe", new { profileRevision = 1, verifyWriteAccess = true });
            exact.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(exact));
            (await JsonAsync(exact)).GetProperty("profileRevision").GetInt32().ShouldBe(1);
            Directory.Exists(oldRoot).ShouldBeTrue();
            Directory.EnumerateFiles(root, ".codespace-probe-*", SearchOption.AllDirectories).ShouldBeEmpty("qualification must clean up its write canary");

            using var scope = _factory.Services.CreateScope();
            (await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().StorageProfile.FindAsync(profileId))!.State.ShouldBe(StorageProfileState.Active);
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public async Task Authentication_admin_permission_and_foreign_team_scope_fail_closed()
    {
        var admin = await SeedWorldAsync(TeamRole.Admin, includeSecondMembership: true);
        var member = await SeedWorldAsync(TeamRole.Member);
        var root = Path.Combine(Path.GetTempPath(), "codespace-storage-probe", Guid.NewGuid().ToString("N"));
        try
        {
            var profileId = await SeedProfileAsync(admin.TeamId, admin.UserId, StorageProfileState.Active, (1, root));

            var anonymous = await SendAsync(admin.UserId, admin.TeamId, HttpMethod.Post, $"/api/storage/profiles/{profileId}/probe", new { }, authenticated: false);
            anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await DescribeAsync(anonymous));

            var forbidden = await SendAsync(member.UserId, member.TeamId, HttpMethod.Post, $"/api/storage/profiles/{profileId}/probe", new { });
            forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await DescribeAsync(forbidden));

            var foreign = await SendAsync(admin.UserId, admin.ForeignTeamId, HttpMethod.Post, $"/api/storage/profiles/{profileId}/probe", new { });
            foreign.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(foreign));
            var foreignJson = await JsonAsync(foreign);
            foreignJson.GetProperty("status").GetString().ShouldBe("Unavailable");
            foreignJson.GetProperty("failure").GetProperty("code").GetString().ShouldBe("ProfileMissing");
            foreignJson.GetRawText().ShouldNotContain(root);
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public async Task Broker_failure_is_typed_and_secret_free_over_http()
    {
        var world = await SeedWorldAsync(TeamRole.Admin);
        var root = Path.Combine(Path.GetTempPath(), "codespace-storage-probe", Guid.NewGuid().ToString("N"));
        var profileId = await SeedProfileAsync(world.TeamId, world.UserId, StorageProfileState.Draft, (1, root));

        var response = await SendAsync(world.UserId, world.TeamId, HttpMethod.Post, $"/api/storage/profiles/{profileId}/probe", new { profileRevision = 1 });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(response));
        var json = await JsonAsync(response);
        json.GetProperty("status").GetString().ShouldBe("Unavailable");
        json.GetProperty("failure").GetProperty("stage").GetString().ShouldBe("Profile");
        json.GetProperty("failure").GetProperty("code").GetString().ShouldBe("ProfileNotActive");
        json.GetRawText().ShouldNotContain(root);
        Directory.Exists(root).ShouldBeFalse("a profile activation failure must not touch provider storage");
    }

    private async Task<World> SeedWorldAsync(TeamRole role, bool includeSecondMembership = false)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var foreignTeamId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, SecurityStamp = TestToken.SeedStamp, Email = $"probe-{suffix}@test.local", Name = "Storage Probe", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.AddRange(
            new Team { Id = teamId, Slug = $"probe-{suffix}", Name = "Probe", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId },
            new Team { Id = foreignTeamId, Slug = $"probe-foreign-{suffix}", Name = "Foreign", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = role, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        if (includeSecondMembership)
            db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = foreignTeamId, UserId = userId, Role = role, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        await db.SaveChangesAsync();
        return new World(userId, teamId, foreignTeamId);
    }

    private async Task<Guid> SeedProfileAsync(Guid teamId, Guid actorId, StorageProfileState state, params (int Revision, string Root)[] revisions)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var profile = new StorageProfile
        {
            Id = Guid.NewGuid(), TeamId = teamId, StableName = $"probe-{Guid.NewGuid():N}", CurrentRevision = revisions.Max(value => value.Revision),
            State = state, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        };
        foreach (var (revision, root) in revisions)
        {
            profile.Revisions.Add(new StorageProfileRevision
            {
                Id = Guid.NewGuid(), TeamId = teamId, StorageProfileId = profile.Id, Revision = revision,
                ProviderTypeKey = "local-rwx/v1", NonSecretConfigJson = JsonSerializer.Serialize(new { rootPath = root }),
                NamespaceFingerprint = "sha256:" + new string((char)('a' + revision), 64), CreatedDate = now, CreatedBy = actorId,
            });
        }
        db.StorageProfile.Add(profile);
        await db.SaveChangesAsync();
        return profile.Id;
    }

    private async Task<HttpResponseMessage> SendAsync(Guid userId, Guid teamId, HttpMethod method, string path, object body, bool authenticated = true)
    {
        var request = new HttpRequestMessage(method, path);
        if (authenticated) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken.Mint(userId, TestToken.SeedStamp));
        request.Headers.Add("X-Team-Id", teamId.ToString());
        request.Content = JsonContent.Create(body);
        return await _factory.CreateClient().SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    private static async Task<string> DescribeAsync(HttpResponseMessage response) => $"got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}";
    private static void DeleteTree(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException) { }
    }

    private sealed record World(Guid UserId, Guid TeamId, Guid ForeignTeamId);
}
