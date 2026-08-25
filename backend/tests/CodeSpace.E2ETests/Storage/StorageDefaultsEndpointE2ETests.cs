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

/// <summary>
/// The deployment-default admin surface through the real HTTP/auth/EF/Postgres stack.
///
/// <para>Two decisions are only testable at this tier. First, the capability is resolved by the production
/// <c>ApiUser</c> over real <c>role_user</c> / <c>role.status</c> rows rather than a fake principal. Second, the route
/// lives outside <c>api/storage</c> precisely because the SPA injects an ambient <c>X-Team-Id</c> into every request
/// and nothing clears it — so the header is sent here on purpose, naming a team the caller does not belong to, and the
/// write must land on the deployment template regardless.</para>
///
/// <para>Nothing consumes a template yet; these tests assert the control plane only.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class StorageDefaultsEndpointE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private const string DataClassTypeKey = "agent-run-log/v1";
    private readonly TaskLaunchApiFactory _factory;

    public StorageDefaultsEndpointE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    /// <summary>
    /// A deployment admin authors the template, and the ambient team header — present, and naming a team this account
    /// is not a member of — changes nothing. If this surface ever moved under a team-scoped controller, that same
    /// header would decide which team got written to.
    /// </summary>
    [Fact]
    public async Task A_capability_holder_authors_the_template_and_the_ambient_team_header_is_inert()
    {
        var holder = await SeedCapabilityHolderAsync();
        var foreignTeamId = await SeedForeignTeamAsync();

        var create = await SendAsync(holder.UserId, HttpMethod.Post, "/api/admin/storage-defaults", Body(DataClassTypeKey, "Automatic"), foreignTeamId);
        create.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(create));
        var created = await JsonAsync(create);
        var defaultId = created.GetProperty("id").GetGuid();
        created.GetProperty("adoptionPolicy").GetString().ShouldBe("Automatic");
        created.GetProperty("namespaceRoot").GetString().ShouldBe("/srv/codespace/artifacts");

        var withoutTeamHeader = await SendAsync(holder.UserId, HttpMethod.Get, $"/api/admin/storage-defaults/{defaultId}", body: null, teamId: null);
        withoutTeamHeader.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(withoutTeamHeader));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var stored = await db.StorageDefault.AsNoTracking().SingleAsync(value => value.Id == defaultId);
        stored.DataClassTypeKey.ShouldBe(DataClassTypeKey);
        stored.AdoptionPolicy.ShouldBe(StorageDefaultAdoptionPolicy.Automatic);
    }

    [Fact]
    public async Task Authentication_and_the_instance_capability_fail_closed()
    {
        var stranger = await SeedPlainUserAsync();
        var body = Body("workflow-artifact/v1", "Explicit");

        var anonymous = await SendAsync(stranger, HttpMethod.Post, "/api/admin/storage-defaults", body, teamId: null, authenticated: false);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var forbidden = await SendAsync(stranger, HttpMethod.Post, "/api/admin/storage-defaults", body, teamId: null);
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await DescribeAsync(forbidden));

        var read = await SendAsync(stranger, HttpMethod.Get, "/api/admin/storage-defaults", body: null, teamId: null);
        read.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await DescribeAsync(read));

        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().StorageDefault.AsNoTracking()
            .AnyAsync(value => value.DataClassTypeKey == "workflow-artifact/v1")).ShouldBeFalse();
    }

    /// <summary>The owner's irreversibility decision, refused at the HTTP edge rather than only in a unit rule.</summary>
    [Fact]
    public async Task Workflow_artifacts_are_refused_an_automatic_adoption_policy()
    {
        var holder = await SeedCapabilityHolderAsync();

        var response = await SendAsync(holder.UserId, HttpMethod.Post, "/api/admin/storage-defaults", Body("workflow-artifact/v1", "Automatic"), teamId: null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await DescribeAsync(response));
        (await response.Content.ReadAsStringAsync()).ShouldContain("workflow-artifact/v1");
    }

    private async Task<CapabilityHolder> SeedCapabilityHolderAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var permissionId = await db.Permission.AsNoTracking().Where(value => value.Name == Permissions.StorageDefaultsManage).Select(value => value.Id).SingleAsync();
        var user = new User { Id = Guid.NewGuid(), SecurityStamp = TestToken.SeedStamp, Email = $"deployment-{suffix}@test.local", Name = "Deployment Admin", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };
        var role = new Role { Id = Guid.NewGuid(), Name = $"storage-defaults-{suffix}", IsSystem = false, Status = true };
        db.User.Add(user);
        db.Role.Add(role);
        db.RolePermission.Add(new RolePermission { Id = Guid.NewGuid(), RoleId = role.Id, PermissionId = permissionId });
        db.RoleUser.Add(new RoleUser { Id = Guid.NewGuid(), RoleId = role.Id, UserId = user.Id });
        await db.SaveChangesAsync();
        return new CapabilityHolder(user.Id, role.Id);
    }

    private async Task<Guid> SeedPlainUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = new User { Id = Guid.NewGuid(), SecurityStamp = TestToken.SeedStamp, Email = $"stranger-{suffix}@test.local", Name = "Stranger", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };
        db.User.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<Guid> SeedForeignTeamAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var team = new Team { Id = Guid.NewGuid(), Slug = $"defaults-foreign-{suffix}", Name = "Foreign", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };
        db.Team.Add(team);
        await db.SaveChangesAsync();
        return team.Id;
    }

    private async Task<HttpResponseMessage> SendAsync(Guid userId, HttpMethod method, string path, object? body, Guid? teamId, bool authenticated = true)
    {
        var request = new HttpRequestMessage(method, path);
        if (authenticated) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken.Mint(userId, TestToken.SeedStamp));
        if (teamId is { } value) request.Headers.Add("X-Team-Id", value.ToString());
        if (body != null) request.Content = JsonContent.Create(body);
        return await _factory.CreateClient().SendAsync(request);
    }

    private static object Body(string dataClassTypeKey, string adoptionPolicy) => new
    {
        dataClassTypeKey,
        providerTypeKey = "local-rwx/v1",
        nonSecretConfig = new { },
        namespaceRoot = "/srv/codespace/artifacts",
        adoptionPolicy,
        isEnabled = true,
    };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    private static async Task<string> DescribeAsync(HttpResponseMessage response) => $"got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}";

    private sealed record CapabilityHolder(Guid UserId, Guid RoleId);
}
