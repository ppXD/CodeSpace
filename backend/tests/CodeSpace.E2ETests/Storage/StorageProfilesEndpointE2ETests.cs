using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Storage;

/// <summary>The storage-profile Settings control plane through the real HTTP/auth/EF/Postgres stack.</summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class StorageProfilesEndpointE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;

    public StorageProfilesEndpointE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task Admin_creates_lists_gets_appends_and_retires_an_immutable_profile_ledger()
    {
        var world = await SeedWorldAsync(TeamRole.Admin, includeSecondMembership: true);
        var credentialRef = await SeedStorageCredentialAsync(world.TeamId, world.UserId);

        var create = await SendAsync(world.UserId, world.TeamId, HttpMethod.Post, "/api/storage/profiles", new
        {
            stableName = "Primary-Artifacts",
            providerTypeKey = "local-rwx/v1",
            nonSecretConfig = new { rootPath = "/srv/codespace/artifacts" },
            credentialRef,
            namespaceFingerprint = "sha256:" + new string('f', 64),
        });
        create.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(create));
        var created = await JsonAsync(create);
        var profileId = created.GetProperty("id").GetGuid();
        created.GetProperty("stableName").GetString().ShouldBe("primary-artifacts");
        created.GetProperty("state").GetString().ShouldBe("Draft");
        created.GetProperty("currentRevision").GetInt32().ShouldBe(1);
        created.GetProperty("revisions").GetArrayLength().ShouldBe(1);
        created.GetProperty("revisions")[0].GetProperty("namespaceFingerprint").GetString().ShouldNotBe("sha256:" + new string('f', 64));
        created.GetProperty("revisions")[0].GetProperty("credentialRef").GetString().ShouldBe(credentialRef);

        var list = await SendAsync(world.UserId, world.TeamId, HttpMethod.Get, "/api/storage/profiles");
        list.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(list));
        (await JsonAsync(list)).GetArrayLength().ShouldBe(1);

        var append = await SendAsync(world.UserId, world.TeamId, HttpMethod.Post, $"/api/storage/profiles/{profileId}/revisions", new
        {
            expectedXmin = created.GetProperty("xmin").GetUInt32(),
            expectedCurrentRevision = 1,
            providerTypeKey = "local-rwx/v1",
            nonSecretConfig = new { rootPath = "/srv/codespace/artifacts-v2" },
            credentialRef,
        });
        append.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(append));
        var revised = await JsonAsync(append);
        revised.GetProperty("currentRevision").GetInt32().ShouldBe(2);
        revised.GetProperty("revisions").EnumerateArray().Select(value => value.GetProperty("revision").GetInt32()).ShouldBe([2, 1]);

        var activate = await SendAsync(world.UserId, world.TeamId, HttpMethod.Put, $"/api/storage/profiles/{profileId}/state", new
        {
            expectedXmin = revised.GetProperty("xmin").GetUInt32(), expectedCurrentRevision = 2, state = "Active",
        });
        activate.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(activate));
        var active = await JsonAsync(activate);

        var retire = await SendAsync(world.UserId, world.TeamId, HttpMethod.Put, $"/api/storage/profiles/{profileId}/state", new
        {
            expectedXmin = active.GetProperty("xmin").GetUInt32(), expectedCurrentRevision = 2, state = "Retired",
        });
        retire.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(retire));
        (await JsonAsync(retire)).GetProperty("state").GetString().ShouldBe("Retired");

        var get = await SendAsync(world.UserId, world.TeamId, HttpMethod.Get, $"/api/storage/profiles/{profileId}");
        get.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(get));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var revisions = await db.StorageProfileRevision.AsNoTracking().Where(value => value.StorageProfileId == profileId).OrderBy(value => value.Revision).ToListAsync();
        revisions.Select(value => JsonDocument.Parse(value.NonSecretConfigJson).RootElement.GetProperty("rootPath").GetString()).ShouldBe([
            "/srv/codespace/artifacts", "/srv/codespace/artifacts-v2",
        ]);
    }

    [Fact]
    public async Task Authentication_admin_role_and_team_scope_fail_closed()
    {
        var member = await SeedWorldAsync(TeamRole.Member);
        var body = ValidCreate("member-denied");

        var anonymous = await SendAsync(member.UserId, member.TeamId, HttpMethod.Post, "/api/storage/profiles", body, authenticated: false);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var forbidden = await SendAsync(member.UserId, member.TeamId, HttpMethod.Post, "/api/storage/profiles", body);
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await DescribeAsync(forbidden));

        var foreign = await SendAsync(member.UserId, member.ForeignTeamId, HttpMethod.Get, "/api/storage/profiles");
        foreign.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await DescribeAsync(foreign));
    }

    [Fact]
    public async Task Profile_reads_are_tenant_scoped_even_for_an_admin_of_both_teams()
    {
        var world = await SeedWorldAsync(TeamRole.Admin, includeSecondMembership: true);
        var create = await SendAsync(world.UserId, world.TeamId, HttpMethod.Post, "/api/storage/profiles", ValidCreate("tenant-a"));
        var profileId = (await JsonAsync(create)).GetProperty("id").GetGuid();

        var foreignGet = await SendAsync(world.UserId, world.ForeignTeamId, HttpMethod.Get, $"/api/storage/profiles/{profileId}");

        foreignGet.StatusCode.ShouldBe(HttpStatusCode.NotFound, await DescribeAsync(foreignGet));
    }

    [Fact]
    public async Task Unknown_provider_secret_injection_and_unstructured_credential_are_invalid()
    {
        var world = await SeedWorldAsync(TeamRole.Admin);

        var unknown = await SendAsync(world.UserId, world.TeamId, HttpMethod.Post, "/api/storage/profiles", new
        {
            stableName = "unknown-provider", providerTypeKey = "not-installed/v1", nonSecretConfig = new { rootPath = "/data" },
        });
        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await DescribeAsync(unknown));

        var secret = await SendAsync(world.UserId, world.TeamId, HttpMethod.Post, "/api/storage/profiles", new
        {
            stableName = "secret-injection", providerTypeKey = "local-rwx/v1",
            nonSecretConfig = new { rootPath = "/data", accessKeySecret = "must-never-persist" },
        });
        secret.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await DescribeAsync(secret));
        (await secret.Content.ReadAsStringAsync()).ShouldNotContain("must-never-persist");

        var rawSecret = await SendAsync(world.UserId, world.TeamId, HttpMethod.Post, "/api/storage/profiles", new
        {
            stableName = "raw-credential", providerTypeKey = "local-rwx/v1", nonSecretConfig = new { rootPath = "/data" }, credentialRef = "actual-secret-value",
        });
        rawSecret.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await DescribeAsync(rawSecret));
    }

    [Fact]
    public async Task Credential_refs_must_resolve_to_an_active_exact_revision_in_the_same_team_and_provider()
    {
        var world = await SeedWorldAsync(TeamRole.Admin);
        var foreign = await SeedStorageCredentialAsync(world.ForeignTeamId, world.UserId);
        var revoked = await SeedStorageCredentialAsync(world.TeamId, world.UserId, StorageCredentialState.Revoked);
        var wrongProvider = await SeedStorageCredentialAsync(world.TeamId, world.UserId, providerTypeKey: "other-store/v1");
        var exact = await SeedStorageCredentialAsync(world.TeamId, world.UserId);

        foreach (var (stableName, credentialRef) in new[]
        {
            ("foreign-credential", foreign), ("revoked-credential", revoked), ("wrong-provider", wrongProvider),
            ("missing-revision", exact[..exact.LastIndexOf(':')] + ":2"),
        })
        {
            var response = await SendAsync(world.UserId, world.TeamId, HttpMethod.Post, "/api/storage/profiles", new
            {
                stableName, providerTypeKey = "local-rwx/v1", nonSecretConfig = new { rootPath = "/data" }, credentialRef,
            });
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await DescribeAsync(response));
        }
    }

    [Fact]
    public async Task Stale_xmin_or_revision_is_a_conflict_and_does_not_append_history()
    {
        var world = await SeedWorldAsync(TeamRole.Admin);
        var create = await SendAsync(world.UserId, world.TeamId, HttpMethod.Post, "/api/storage/profiles", ValidCreate("concurrent"));
        var original = await JsonAsync(create);
        var profileId = original.GetProperty("id").GetGuid();

        var activate = await SendAsync(world.UserId, world.TeamId, HttpMethod.Put, $"/api/storage/profiles/{profileId}/state", new
        {
            expectedXmin = original.GetProperty("xmin").GetUInt32(), expectedCurrentRevision = 1, state = "Active",
        });
        activate.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(activate));

        var stale = await SendAsync(world.UserId, world.TeamId, HttpMethod.Post, $"/api/storage/profiles/{profileId}/revisions", new
        {
            expectedXmin = original.GetProperty("xmin").GetUInt32(), expectedCurrentRevision = 1,
            providerTypeKey = "local-rwx/v1", nonSecretConfig = new { rootPath = "/stale" },
        });
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict, await DescribeAsync(stale));

        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().StorageProfileRevision.CountAsync(value => value.StorageProfileId == profileId)).ShouldBe(1);
    }

    [Fact]
    public async Task Retired_is_terminal_for_revision_and_state_changes()
    {
        var world = await SeedWorldAsync(TeamRole.Admin);
        var create = await SendAsync(world.UserId, world.TeamId, HttpMethod.Post, "/api/storage/profiles", ValidCreate("retired"));
        var original = await JsonAsync(create);
        var profileId = original.GetProperty("id").GetGuid();
        var retire = await SendAsync(world.UserId, world.TeamId, HttpMethod.Put, $"/api/storage/profiles/{profileId}/state", new
        {
            expectedXmin = original.GetProperty("xmin").GetUInt32(), expectedCurrentRevision = 1, state = "Retired",
        });
        var retired = await JsonAsync(retire);

        var append = await SendAsync(world.UserId, world.TeamId, HttpMethod.Post, $"/api/storage/profiles/{profileId}/revisions", new
        {
            expectedXmin = retired.GetProperty("xmin").GetUInt32(), expectedCurrentRevision = 1,
            providerTypeKey = "local-rwx/v1", nonSecretConfig = new { rootPath = "/resurrected" },
        });
        append.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await DescribeAsync(append));

        var reactivate = await SendAsync(world.UserId, world.TeamId, HttpMethod.Put, $"/api/storage/profiles/{profileId}/state", new
        {
            expectedXmin = retired.GetProperty("xmin").GetUInt32(), expectedCurrentRevision = 1, state = "Active",
        });
        reactivate.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await DescribeAsync(reactivate));
    }

    private async Task<World> SeedWorldAsync(TeamRole role, bool includeSecondMembership = false)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var foreignTeamId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, SecurityStamp = TestToken.SeedStamp, Email = $"profile-{suffix}@test.local", Name = "Storage Profile", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.AddRange(
            new Team { Id = teamId, Slug = $"profile-{suffix}", Name = "Profiles", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId },
            new Team { Id = foreignTeamId, Slug = $"profile-foreign-{suffix}", Name = "Foreign", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = role, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        if (includeSecondMembership)
            db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = foreignTeamId, UserId = userId, Role = role, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        await db.SaveChangesAsync();
        return new World(userId, teamId, foreignTeamId);
    }

    private async Task<string> SeedStorageCredentialAsync(Guid teamId, Guid actorId, StorageCredentialState state = StorageCredentialState.Active, string providerTypeKey = "local-rwx/v1")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var credential = new StorageCredential
        {
            Id = Guid.NewGuid(), TeamId = teamId, StableName = $"profile-{Guid.NewGuid():N}", CurrentRevision = 1,
            State = StorageCredentialState.Active, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actorId,
        };
        credential.Revisions.Add(new StorageCredentialRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageCredentialId = credential.Id, Revision = 1,
            ProviderTypeKey = providerTypeKey, EncryptedPayload = "protected-test-envelope", SafeHint = "test-ref",
            EnvelopeFingerprint = "sha256:" + new string('a', 64), CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actorId,
        });
        db.StorageCredential.Add(credential);
        await db.SaveChangesAsync();
        if (state == StorageCredentialState.Revoked)
        {
            credential.State = StorageCredentialState.Revoked;
            credential.RevokedDate = DateTimeOffset.UtcNow;
            credential.RevokedBy = actorId;
            await db.SaveChangesAsync();
        }
        return $"db:{credential.Id:D}:1";
    }

    private async Task<HttpResponseMessage> SendAsync(Guid userId, Guid teamId, HttpMethod method, string path, object? body = null, bool authenticated = true)
    {
        var request = new HttpRequestMessage(method, path);
        if (authenticated) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken.Mint(userId, TestToken.SeedStamp));
        request.Headers.Add("X-Team-Id", teamId.ToString());
        if (body != null) request.Content = JsonContent.Create(body);
        return await _factory.CreateClient().SendAsync(request);
    }

    private static object ValidCreate(string stableName) => new
    {
        stableName, providerTypeKey = "local-rwx/v1", nonSecretConfig = new { rootPath = "/srv/codespace/artifacts" },
    };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    private static async Task<string> DescribeAsync(HttpResponseMessage response) => $"got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}";

    private sealed record World(Guid UserId, Guid TeamId, Guid ForeignTeamId);
}
