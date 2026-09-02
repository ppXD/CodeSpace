using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Storage;

/// <summary>
/// The additive storage-provider discovery surface through the real ASP.NET pipeline: routing, JWT authentication,
/// X-Team-Id membership authorization, controller, mediator, and the production module catalog. The endpoint is
/// descriptor-only — it never reads a profile or secret row and never activates a storage factory.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class StorageProviderModulesEndpointE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;

    public StorageProviderModulesEndpointE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task Authenticated_team_member_discovers_the_public_module_descriptors()
    {
        var (userId, teamId, _) = await SeedUserAndTeamsAsync();

        var response = await SendAsync(userId, teamId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: await DescribeAsync(response));
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var modules = body.EnumerateArray().ToList();
        modules.Select(module => module.GetProperty("typeKey").GetString()).ShouldBe(
            modules.Select(module => module.GetProperty("typeKey").GetString()).OrderBy(key => key, StringComparer.Ordinal),
            "the HTTP contract is deterministic regardless of DI contribution order");

        var local = modules.Single(module => module.GetProperty("typeKey").GetString() == "local-rwx/v1");
        local.EnumerateObject().Select(property => property.Name).ShouldBe([
            "typeKey", "displayName", "configSchema", "secretSchema", "capabilities", "teamNamespaceProperty", "acceptsNoNewBytes"
        ]);
        local.GetProperty("teamNamespaceProperty").GetString().ShouldBe("rootPath",
            "an admin form has to remove this property from the config it offers — the server refuses a deployment template that sets it, because it names one team");
        local.GetProperty("displayName").GetString().ShouldBe("Local / shared filesystem");
        local.GetProperty("configSchema").GetProperty("properties").TryGetProperty("rootPath", out _).ShouldBeTrue();
        local.GetProperty("secretSchema").GetProperty("properties").EnumerateObject().ShouldBeEmpty("the local provider declares no secret inputs");
        local.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()).ShouldBe([
            "StreamingWrite", "StreamingRead", "RangeRead", "ConditionalCreate", "Delete", "HealthProbe"
        ]);

        var wire = body.GetRawText();
        wire.ShouldNotContain("factoryType", Case.Insensitive);
        wire.ShouldNotContain("LocalFileArtifactBlobBackend", Case.Insensitive);
    }

    [Fact]
    public async Task Missing_authentication_is_rejected()
    {
        var (_, teamId, _) = await SeedUserAndTeamsAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/storage/provider-modules");
        request.Headers.Add("X-Team-Id", teamId.ToString());

        var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Missing_or_foreign_team_scope_is_rejected_fail_closed()
    {
        var (userId, _, foreignTeamId) = await SeedUserAndTeamsAsync();

        var missingTeam = new HttpRequestMessage(HttpMethod.Get, "/api/storage/provider-modules");
        missingTeam.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken.Mint(userId, TestToken.SeedStamp));
        var missingResponse = await _factory.CreateClient().SendAsync(missingTeam);

        missingResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "team scope is required even though the catalog is process-wide");
        (await SendAsync(userId, foreignTeamId)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "a caller cannot use a team it does not belong to as an authorization carrier");
    }

    private async Task<HttpResponseMessage> SendAsync(Guid userId, Guid teamId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/storage/provider-modules");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken.Mint(userId, TestToken.SeedStamp));
        request.Headers.Add("X-Team-Id", teamId.ToString());
        return await _factory.CreateClient().SendAsync(request);
    }

    private async Task<(Guid UserId, Guid TeamId, Guid ForeignTeamId)> SeedUserAndTeamsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var foreignTeamId = Guid.NewGuid();

        db.User.Add(new User { Id = userId, SecurityStamp = TestToken.SeedStamp, Email = $"storage-{suffix}@test.local", Name = "Storage API", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.AddRange(
            new Team { Id = teamId, Slug = $"storage-{suffix}", Name = "Storage", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId },
            new Team { Id = foreignTeamId, Slug = $"foreign-{suffix}", Name = "Foreign", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Member, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();
        return (userId, teamId, foreignTeamId);
    }

    private static async Task<string> DescribeAsync(HttpResponseMessage response) =>
        $"expected 200; got {(int)response.StatusCode}; body: {await response.Content.ReadAsStringAsync()}";
}
