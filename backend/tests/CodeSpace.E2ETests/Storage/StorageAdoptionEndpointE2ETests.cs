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
/// Adoption over the real HTTP surface: the team-scoped half of the deployment-defaults tier, where the ambient
/// <c>X-Team-Id</c> header is not merely tolerated but load-bearing — it names the team being taken onto the default.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class StorageAdoptionEndpointE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private const string DataClassTypeKey = "agent-run-log/v1";
    private readonly TaskLaunchApiFactory _factory;

    public StorageAdoptionEndpointE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task A_team_admin_adopts_the_deployment_default_and_the_second_call_says_nothing_changed()
    {
        var world = await SeedTeamAsync(TeamRole.Owner);
        await AuthorTemplateAsync(NewRoot());

        var first = await AdoptAsync(world);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());

        var adopted = await ReadAsync(first);
        adopted.GetProperty("outcome").GetString().ShouldBe("Adopted");
        adopted.GetProperty("storageRouteId").GetString().ShouldNotBeNullOrWhiteSpace();

        var again = await ReadAsync(await AdoptAsync(world));
        again.GetProperty("outcome").GetString().ShouldBe("AlreadyAdopted",
            "a second adoption must be idempotent and say so, not fail and not silently create a second profile");
        again.GetProperty("storageProfileId").GetString().ShouldBe(adopted.GetProperty("storageProfileId").GetString());
    }

    [Fact]
    public async Task The_status_list_says_what_the_screen_may_offer_before_and_after_adopting()
    {
        var world = await SeedTeamAsync(TeamRole.Owner);
        await AuthorTemplateAsync(NewRoot());

        var before = await StatusAsync(world);
        before.GetProperty("defaultAvailable").GetBoolean().ShouldBeTrue();
        before.GetProperty("canAdopt").GetBoolean().ShouldBeTrue();
        before.GetProperty("adopted").GetBoolean().ShouldBeFalse();
        before.GetProperty("displayName").GetString().ShouldNotBeNullOrWhiteSpace("a screen renders the class by name, not by its wire key");

        await AdoptAsync(world);

        var after = await StatusAsync(world);
        after.GetProperty("adopted").GetBoolean().ShouldBeTrue();
        after.GetProperty("canAdopt").GetBoolean().ShouldBeFalse("offering adoption to a team already on it is an action that can only answer AlreadyAdopted");
        after.GetProperty("sourceRevision").GetInt32().ShouldBe(after.GetProperty("templateRevision").GetInt32(),
            "a team just materialized is on the template's current revision, so nothing should report it as stale");
    }

    [Fact]
    public async Task A_class_the_deployment_authored_no_default_for_is_reported_rather_than_hidden()
    {
        // The state every new deployment is in. A screen that only listed classes WITH a default could not explain
        // the absence, so the list must carry them with the reason.
        var world = await SeedTeamAsync(TeamRole.Owner);

        var statuses = await AllStatusesAsync(world);

        statuses.ShouldNotBeEmpty("every routed data class this build knows must appear, with or without a default");
        statuses.ShouldAllBe(status => status.GetProperty("dataClassTypeKey").GetString()!.Length > 0);
    }

    [Fact]
    public async Task Adopting_without_the_storage_capability_is_refused()
    {
        var world = await SeedTeamAsync(TeamRole.Member);
        await AuthorTemplateAsync(NewRoot());

        var response = await AdoptAsync(world);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "a member who cannot manage storage cannot take the team off the storage it has");
        await AssertNoRouteAsync(world.TeamId);
    }

    [Fact]
    public async Task Adopting_for_a_team_the_caller_does_not_belong_to_is_refused()
    {
        // The ambient header names the team here, so the check that it is the CALLER'S team is the only thing between
        // an adoption request and any team id the caller can type.
        var world = await SeedTeamAsync(TeamRole.Owner);
        await AuthorTemplateAsync(NewRoot());

        var response = await SendAsync(world.UserId, HttpMethod.Post, "/api/storage/adoptions", new { dataClassTypeKey = DataClassTypeKey }, world.ForeignTeamId);

        response.StatusCode.ShouldBeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
        await AssertNoRouteAsync(world.ForeignTeamId);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        var world = await SeedTeamAsync(TeamRole.Owner);

        var response = await SendAsync(world.UserId, HttpMethod.Get, "/api/storage/adoptions", null, world.TeamId, authenticated: false);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_destination_that_refuses_writes_answers_with_a_reason_and_leaves_the_team_untouched()
    {
        // Not a 4xx. "The destination refused a write" is an answer a screen renders differently from "you may not do
        // this" — and the team must be exactly as it was, because nothing a half-adoption writes can be deleted.
        var world = await SeedTeamAsync(TeamRole.Owner);
        await AuthorTemplateAsync("/dev/null/codespace-cannot-write-here");

        var response = await AdoptAsync(world);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await ReadAsync(response);
        result.GetProperty("outcome").GetString().ShouldBe("DestinationUnusable");
        result.GetProperty("detail").GetString().ShouldNotBeNullOrWhiteSpace("the operator has to be told which of the credential and the namespace to fix");

        await AssertNoRouteAsync(world.TeamId);
    }

    // ─── World + helpers ─────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> AdoptAsync(World world) =>
        await SendAsync(world.UserId, HttpMethod.Post, "/api/storage/adoptions", new { dataClassTypeKey = DataClassTypeKey }, world.TeamId);

    private async Task<JsonElement> StatusAsync(World world) =>
        (await AllStatusesAsync(world)).Single(status => status.GetProperty("dataClassTypeKey").GetString() == DataClassTypeKey);

    private async Task<IReadOnlyList<JsonElement>> AllStatusesAsync(World world)
    {
        var response = await SendAsync(world.UserId, HttpMethod.Get, "/api/storage/adoptions", null, world.TeamId);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return (await ReadAsync(response)).EnumerateArray().ToList();
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "codespace-adoption", Guid.NewGuid().ToString("N"));

    /// <summary>Authors the deployment template directly, because this suite is about the TEAM half of the tier — the admin half has its own file, and going through that surface here would make every test depend on it.</summary>
    private async Task AuthorTemplateAsync(string namespaceRoot)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var existing = await db.StorageDefault.SingleOrDefaultAsync(row => row.DataClassTypeKey == DataClassTypeKey);

        if (existing != null)
        {
            existing.NamespaceRoot = namespaceRoot;
            existing.IsEnabled = true;
            existing.Revision += 1;
            existing.LastModifiedDate = now;
            await db.SaveChangesAsync();
            return;
        }

        db.StorageDefault.Add(new StorageDefault
        {
            Id = Guid.NewGuid(), DataClassTypeKey = DataClassTypeKey, ProviderTypeKey = "local-rwx/v1",
            NonSecretConfigJson = "{}", NamespaceRoot = namespaceRoot, CredentialId = null,
            AdoptionPolicy = StorageDefaultAdoptionPolicy.Automatic, IsEnabled = true, Revision = 1,
            CreatedDate = now, CreatedBy = SystemUsers.SeederId, LastModifiedDate = now, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
    }

    private async Task AssertNoRouteAsync(Guid teamId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        (await db.StorageRoute.AsNoTracking().CountAsync(row => row.TeamId == teamId))
            .ShouldBe(0, "a refused adoption must leave nothing — a profile, credential or route it created could never be deleted");
        (await db.StorageProfile.AsNoTracking().CountAsync(row => row.TeamId == teamId)).ShouldBe(0);
    }

    private async Task<World> SeedTeamAsync(TeamRole role)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var foreignTeamId = Guid.NewGuid();

        db.User.Add(new User { Id = userId, SecurityStamp = TestToken.SeedStamp, Email = $"adoption-{suffix}@test.local", Name = "Storage Adoption", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.AddRange(
            new Team { Id = teamId, Slug = $"adoption-{suffix}", Name = "Adoption", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId },
            new Team { Id = foreignTeamId, Slug = $"adoption-foreign-{suffix}", Name = "Foreign", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = role, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        await db.SaveChangesAsync();

        return new World(userId, teamId, foreignTeamId);
    }

    private async Task<HttpResponseMessage> SendAsync(Guid userId, HttpMethod method, string path, object? body, Guid? teamId, bool authenticated = true)
    {
        var request = new HttpRequestMessage(method, path);
        if (authenticated) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken.Mint(userId, TestToken.SeedStamp));
        if (teamId is { } value) request.Headers.Add("X-Team-Id", value.ToString());
        if (body != null) request.Content = JsonContent.Create(body);

        return await _factory.CreateClient().SendAsync(request);
    }

    private sealed record World(Guid UserId, Guid TeamId, Guid ForeignTeamId);
}
