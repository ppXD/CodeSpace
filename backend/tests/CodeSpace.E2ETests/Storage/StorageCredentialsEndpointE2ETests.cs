using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Storage;

/// <summary>The write-only storage-credential control plane through real HTTP/auth/EF/Postgres/Data Protection.</summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class StorageCredentialsEndpointE2ETests : IClassFixture<TaskLaunchApiFactory>, IDisposable
{
    private const string ProviderTypeKey = "credential-test/v1";
    private readonly WebApplicationFactory<CodeSpace.Api.Program> _application;
    private readonly HttpClient _client;

    public StorageCredentialsEndpointE2ETests(TaskLaunchApiFactory factory)
    {
        _application = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStorageProviderModule, CredentialTestStorageProviderModule>();
            services.AddSingleton<IArtifactStorageDriverFactory, CredentialTestStorageDriverFactory>();
        }));
        _client = _application.CreateClient();
    }

    [Fact]
    public async Task Admin_creates_lists_gets_rotates_and_revokes_without_exposing_secret_material()
    {
        var world = await SeedWorldAsync(TeamRole.Admin);
        const string firstSecret = "AK-ABCD";
        var create = await SendAsync(world.UserId, world.TeamId, HttpMethod.Post, "/api/storage/credentials", new
        {
            stableName = " Primary-Store ", providerTypeKey = ProviderTypeKey,
            secret = new { mode = "write", accessKey = firstSecret }, safeHint = "  ending-ABCD  ",
        });

        create.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(create));
        var createdWire = await create.Content.ReadAsStringAsync();
        var created = Json(createdWire);
        var credentialId = created.GetProperty("id").GetGuid();
        created.GetProperty("stableName").GetString().ShouldBe("primary-store");
        created.GetProperty("state").GetString().ShouldBe("Active");
        created.GetProperty("currentRevision").GetInt32().ShouldBe(1);
        created.GetProperty("providerTypeKey").GetString().ShouldBe(ProviderTypeKey);
        created.GetProperty("safeHint").GetString().ShouldBe("ending-ABCD");
        created.GetProperty("credentialRef").GetString().ShouldBe($"db:{credentialId:D}:1");
        created.GetProperty("xmin").GetUInt32().ShouldNotBe(0u);
        AssertSafeWire(createdWire, firstSecret);

        var list = await SendAsync(world.UserId, world.TeamId, HttpMethod.Get, "/api/storage/credentials");
        list.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(list));
        var listed = Json(await list.Content.ReadAsStringAsync());
        listed.GetArrayLength().ShouldBe(1);
        listed[0].GetProperty("id").GetGuid().ShouldBe(credentialId);

        var get = await SendAsync(world.UserId, world.TeamId, HttpMethod.Get, $"/api/storage/credentials/{credentialId}");
        get.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(get));
        AssertSafeWire(await get.Content.ReadAsStringAsync(), firstSecret);

        const string rotatedSecret = "AK-WXYZ";
        var rotate = await SendAsync(world.UserId, world.TeamId, HttpMethod.Post, $"/api/storage/credentials/{credentialId}/revisions", new
        {
            expectedXmin = created.GetProperty("xmin").GetUInt32(), expectedCurrentRevision = 1,
            providerTypeKey = ProviderTypeKey, secret = new { accessKey = rotatedSecret, mode = "read" }, safeHint = "ending-WXYZ",
        });
        rotate.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(rotate));
        var rotatedWire = await rotate.Content.ReadAsStringAsync();
        var rotated = Json(rotatedWire);
        rotated.GetProperty("currentRevision").GetInt32().ShouldBe(2);
        rotated.GetProperty("credentialRef").GetString().ShouldBe($"db:{credentialId:D}:2");
        rotated.GetProperty("xmin").GetUInt32().ShouldNotBe(created.GetProperty("xmin").GetUInt32());
        AssertSafeWire(rotatedWire, rotatedSecret);

        var revoke = await SendAsync(world.UserId, world.TeamId, HttpMethod.Post, $"/api/storage/credentials/{credentialId}/revoke", new
        {
            expectedXmin = rotated.GetProperty("xmin").GetUInt32(), expectedCurrentRevision = 2,
        });
        revoke.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(revoke));
        var revoked = Json(await revoke.Content.ReadAsStringAsync());
        revoked.GetProperty("state").GetString().ShouldBe("Revoked");
        revoked.GetProperty("revokedDate").ValueKind.ShouldBe(JsonValueKind.String);

        using var scope = _application.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var revisions = await db.StorageCredentialRevision.AsNoTracking().Where(value => value.StorageCredentialId == credentialId).OrderBy(value => value.Revision).ToListAsync();
        revisions.Count.ShouldBe(2);
        var encryptor = scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>();
        encryptor.Decrypt(revisions[0].EncryptedPayload).ShouldBe("""{"accessKey":"AK-ABCD","mode":"write"}""");
        encryptor.Decrypt(revisions[1].EncryptedPayload).ShouldBe("""{"accessKey":"AK-WXYZ","mode":"read"}""");
        revisions[0].EncryptedPayload.ShouldNotContain(firstSecret);
        revisions[1].EncryptedPayload.ShouldNotContain(rotatedSecret);
        revisions[0].EnvelopeFingerprint.ShouldBe(Fingerprint(revisions[0].EncryptedPayload));
        revisions[1].EnvelopeFingerprint.ShouldBe(Fingerprint(revisions[1].EncryptedPayload));
        createdWire.ShouldNotContain(revisions[0].EncryptedPayload);
        rotatedWire.ShouldNotContain(revisions[1].EncryptedPayload);
    }

    [Fact]
    public async Task Authentication_admin_permission_and_team_membership_fail_closed()
    {
        var member = await SeedWorldAsync(TeamRole.Member);
        var anonymous = await SendAsync(member.UserId, member.TeamId, HttpMethod.Post, "/api/storage/credentials", ValidCreate("anonymous"), authenticated: false);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await DescribeAsync(anonymous));

        var forbiddenWrite = await SendAsync(member.UserId, member.TeamId, HttpMethod.Post, "/api/storage/credentials", ValidCreate("member-write"));
        forbiddenWrite.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await DescribeAsync(forbiddenWrite));

        var forbiddenRead = await SendAsync(member.UserId, member.TeamId, HttpMethod.Get, "/api/storage/credentials");
        forbiddenRead.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await DescribeAsync(forbiddenRead));

        var foreignTeam = await SendAsync(member.UserId, member.ForeignTeamId, HttpMethod.Get, "/api/storage/credentials");
        foreignTeam.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await DescribeAsync(foreignTeam));
    }

    [Fact]
    public async Task Foreign_and_unknown_credentials_are_indistinguishable_and_lists_are_team_scoped()
    {
        var world = await SeedWorldAsync(TeamRole.Admin, includeForeignMembership: true);
        var create = await SendAsync(world.UserId, world.TeamId, HttpMethod.Post, "/api/storage/credentials", ValidCreate("tenant-a"));
        var created = Json(await create.Content.ReadAsStringAsync());
        var credentialId = created.GetProperty("id").GetGuid();
        var expectedXmin = created.GetProperty("xmin").GetUInt32();

        var foreignGet = await SendAsync(world.UserId, world.ForeignTeamId, HttpMethod.Get, $"/api/storage/credentials/{credentialId}");
        var unknownGet = await SendAsync(world.UserId, world.ForeignTeamId, HttpMethod.Get, $"/api/storage/credentials/{Guid.NewGuid()}");
        foreignGet.StatusCode.ShouldBe(HttpStatusCode.NotFound, await DescribeAsync(foreignGet));
        unknownGet.StatusCode.ShouldBe(HttpStatusCode.NotFound, await DescribeAsync(unknownGet));
        var foreignFailure = Json(await foreignGet.Content.ReadAsStringAsync());
        var unknownFailure = Json(await unknownGet.Content.ReadAsStringAsync());
        foreignFailure.GetProperty("status").GetInt32().ShouldBe(unknownFailure.GetProperty("status").GetInt32());
        foreignFailure.GetProperty("title").GetString().ShouldBe(unknownFailure.GetProperty("title").GetString());

        var foreignRotate = await SendAsync(world.UserId, world.ForeignTeamId, HttpMethod.Post, $"/api/storage/credentials/{credentialId}/revisions", ValidRotation(expectedXmin, 1, "AK-WXYZ"));
        foreignRotate.StatusCode.ShouldBe(HttpStatusCode.NotFound, await DescribeAsync(foreignRotate));
        var foreignRevoke = await SendAsync(world.UserId, world.ForeignTeamId, HttpMethod.Post, $"/api/storage/credentials/{credentialId}/revoke", new { expectedXmin, expectedCurrentRevision = 1 });
        foreignRevoke.StatusCode.ShouldBe(HttpStatusCode.NotFound, await DescribeAsync(foreignRevoke));

        var foreignList = await SendAsync(world.UserId, world.ForeignTeamId, HttpMethod.Get, "/api/storage/credentials");
        Json(await foreignList.Content.ReadAsStringAsync()).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Secret_schema_required_type_additional_enum_and_pattern_failures_are_bad_requests_without_echoing_values()
    {
        var world = await SeedWorldAsync(TeamRole.Admin);
        var invalid = new[]
        {
            (Name: "missing", Secret: Json("""{ "mode": "write" }""")),
            (Name: "wrong-type", Secret: Json("""{ "accessKey": 7, "mode": "write" }""")),
            (Name: "additional", Secret: Json("""{ "accessKey": "AK-ABCD", "mode": "write", "rawSecret": "DO-NOT-ECHO" }""")),
            (Name: "enum", Secret: Json("""{ "accessKey": "AK-ABCD", "mode": "admin" }""")),
            (Name: "pattern", Secret: Json("""{ "accessKey": "RAW-SECRET-DO-NOT-ECHO", "mode": "write" }""")),
        };

        foreach (var item in invalid)
        {
            var response = await SendAsync(world.UserId, world.TeamId, HttpMethod.Post, "/api/storage/credentials", new
            {
                stableName = item.Name, providerTypeKey = ProviderTypeKey, secret = item.Secret,
            });
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"{item.Name}: {await DescribeAsync(response)}");
            var wire = await response.Content.ReadAsStringAsync();
            wire.ShouldNotContain("DO-NOT-ECHO");
            wire.ShouldNotContain("RAW-SECRET");
        }

        using var scope = _application.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().StorageCredential.CountAsync(value => value.TeamId == world.TeamId)).ShouldBe(0);
    }

    [Fact]
    public async Task Unknown_provider_is_rejected_before_plaintext_can_cross_the_persistence_boundary()
    {
        var world = await SeedWorldAsync(TeamRole.Admin);
        const string secret = "AK-ABCD";

        var response = await SendAsync(world.UserId, world.TeamId, HttpMethod.Post, "/api/storage/credentials", new
        {
            stableName = "unknown-provider", providerTypeKey = "not-installed/v1", secret = new { accessKey = secret, mode = "write" },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await DescribeAsync(response));
        (await response.Content.ReadAsStringAsync()).ShouldNotContain(secret);
        using var scope = _application.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().StorageCredentialRevision.CountAsync(value => value.TeamId == world.TeamId)).ShouldBe(0);
    }

    [Fact]
    public async Task Simultaneous_rotations_with_the_same_xmin_have_one_winner_and_one_conflict()
    {
        var world = await SeedWorldAsync(TeamRole.Admin);
        var create = await SendAsync(world.UserId, world.TeamId, HttpMethod.Post, "/api/storage/credentials", ValidCreate("contention"));
        var created = Json(await create.Content.ReadAsStringAsync());
        var credentialId = created.GetProperty("id").GetGuid();
        var xmin = created.GetProperty("xmin").GetUInt32();

        var responses = await Task.WhenAll(
            SendAsync(world.UserId, world.TeamId, HttpMethod.Post, $"/api/storage/credentials/{credentialId}/revisions", ValidRotation(xmin, 1, "AK-ABCD")),
            SendAsync(world.UserId, world.TeamId, HttpMethod.Post, $"/api/storage/credentials/{credentialId}/revisions", ValidRotation(xmin, 1, "AK-WXYZ")));

        responses.Select(value => value.StatusCode).OrderBy(value => value).ShouldBe([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        foreach (var response in responses)
        {
            var wire = await response.Content.ReadAsStringAsync();
            wire.ShouldNotContain("AK-ABCD");
            wire.ShouldNotContain("AK-WXYZ");
        }

        using var scope = _application.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        (await db.StorageCredentialRevision.CountAsync(value => value.StorageCredentialId == credentialId)).ShouldBe(2);
        (await db.StorageCredential.AsNoTracking().SingleAsync(value => value.Id == credentialId)).CurrentRevision.ShouldBe(2);
    }

    [Fact]
    public async Task Revoke_is_terminal_and_a_revoked_credential_cannot_rotate_again()
    {
        var world = await SeedWorldAsync(TeamRole.Admin);
        var create = await SendAsync(world.UserId, world.TeamId, HttpMethod.Post, "/api/storage/credentials", ValidCreate("terminal"));
        var created = Json(await create.Content.ReadAsStringAsync());
        var credentialId = created.GetProperty("id").GetGuid();
        var revoke = await SendAsync(world.UserId, world.TeamId, HttpMethod.Post, $"/api/storage/credentials/{credentialId}/revoke", new
        {
            expectedXmin = created.GetProperty("xmin").GetUInt32(), expectedCurrentRevision = 1,
        });
        revoke.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(revoke));
        var revoked = Json(await revoke.Content.ReadAsStringAsync());

        var rotate = await SendAsync(world.UserId, world.TeamId, HttpMethod.Post, $"/api/storage/credentials/{credentialId}/revisions", ValidRotation(revoked.GetProperty("xmin").GetUInt32(), 1, "AK-WXYZ"));
        rotate.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await DescribeAsync(rotate));

        var revokeAgain = await SendAsync(world.UserId, world.TeamId, HttpMethod.Post, $"/api/storage/credentials/{credentialId}/revoke", new
        {
            expectedXmin = revoked.GetProperty("xmin").GetUInt32(), expectedCurrentRevision = 1,
        });
        revokeAgain.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await DescribeAsync(revokeAgain));

        using var scope = _application.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        (await db.StorageCredentialRevision.CountAsync(value => value.StorageCredentialId == credentialId)).ShouldBe(1);
        (await db.StorageCredential.AsNoTracking().SingleAsync(value => value.Id == credentialId)).State.ShouldBe(StorageCredentialState.Revoked);
    }

    public void Dispose()
    {
        _client.Dispose();
        _application.Dispose();
    }

    private async Task<World> SeedWorldAsync(TeamRole role, bool includeForeignMembership = false)
    {
        using var scope = _application.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var foreignTeamId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, SecurityStamp = TestToken.SeedStamp, Email = $"credential-{suffix}@test.local", Name = "Storage Credential", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.AddRange(
            new Team { Id = teamId, Slug = $"credential-{suffix}", Name = "Credentials", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId },
            new Team { Id = foreignTeamId, Slug = $"credential-foreign-{suffix}", Name = "Foreign", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = role, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        if (includeForeignMembership)
            db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = foreignTeamId, UserId = userId, Role = role, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        await db.SaveChangesAsync();
        return new World(userId, teamId, foreignTeamId);
    }

    private async Task<HttpResponseMessage> SendAsync(Guid userId, Guid teamId, HttpMethod method, string path, object? body = null, bool authenticated = true)
    {
        var request = new HttpRequestMessage(method, path);
        if (authenticated) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken.Mint(userId, TestToken.SeedStamp));
        request.Headers.Add("X-Team-Id", teamId.ToString());
        if (body != null) request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }

    private static object ValidCreate(string stableName) => new
    {
        stableName, providerTypeKey = ProviderTypeKey, secret = new { mode = "write", accessKey = "AK-ABCD" }, safeHint = "ending-ABCD",
    };

    private static object ValidRotation(uint expectedXmin, int expectedCurrentRevision, string accessKey) => new
    {
        expectedXmin, expectedCurrentRevision, providerTypeKey = ProviderTypeKey, secret = new { accessKey, mode = "write" }, safeHint = "rotated",
    };

    private static void AssertSafeWire(string wire, string plaintext)
    {
        wire.ShouldNotContain(plaintext);
        wire.ShouldNotContain("encryptedPayload", Case.Insensitive);
        wire.ShouldNotContain("envelopeFingerprint", Case.Insensitive);
        wire.ShouldNotContain("secret\"", Case.Insensitive);
    }

    private static string Fingerprint(string ciphertext) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ciphertext))).ToLowerInvariant();
    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
    private static async Task<string> DescribeAsync(HttpResponseMessage response) => $"got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}";

    private sealed record World(Guid UserId, Guid TeamId, Guid ForeignTeamId);

    private sealed class CredentialTestStorageProviderModule : IStorageProviderModule
    {
        public string TypeKey => ProviderTypeKey;
        public string DisplayName => "Credential schema test";
        public JsonElement ConfigSchema => Json("""{ "type": "object", "properties": {}, "additionalProperties": false }""");
        public JsonElement SecretSchema => Json("""
            {
              "type": "object",
              "properties": {
                "accessKey": { "type": "string", "pattern": "^AK-[A-Z]{4}$" },
                "mode": { "type": "string", "enum": ["read", "write"] }
              },
              "required": ["accessKey", "mode"],
              "additionalProperties": false
            }
            """);
        public StorageProviderCapabilities Capabilities => StorageProviderCapabilities.None;
        public Type FactoryType => typeof(CredentialTestStorageDriverFactory);
    }

    private sealed class CredentialTestStorageDriverFactory : IArtifactStorageDriverFactory
    {
        public string ProviderTypeKey => StorageCredentialsEndpointE2ETests.ProviderTypeKey;
        public ValueTask<IArtifactStorageDriver> CreateAsync(ArtifactStorageDriverCreateRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
