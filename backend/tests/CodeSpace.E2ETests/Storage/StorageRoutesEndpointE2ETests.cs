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

/// <summary>The versioned storage-routing Settings control plane through the real HTTP/auth/EF/Postgres stack.</summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class StorageRoutesEndpointE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;

    public StorageRoutesEndpointE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task Admin_creates_pages_gets_appends_activates_and_retires_a_secret_free_route_ledger()
    {
        var world = await SeedWorldAsync(TeamRole.Admin, includeForeignMembership: true);
        var firstProfile = await SeedProfileAsync(world.TeamId, world.UserId, "primary-store");
        var secondProfile = await SeedProfileAsync(world.TeamId, world.UserId, "archive-store", revisions: 2);

        var create = await SendAsync(world, HttpMethod.Post, "/api/storage/routes", new
        {
            dataClassTypeKey = " Agent-Run-Log/v1 ", storageProfileId = firstProfile,
            profileRevisionMode = "CurrentAtWrite",
        });
        create.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(create));
        var createWire = await create.Content.ReadAsStringAsync();
        AssertNoProfileMaterial(createWire);
        var created = Json(createWire);
        var routeId = created.GetProperty("id").GetGuid();
        created.GetProperty("dataClassTypeKey").GetString().ShouldBe("agent-run-log/v1");
        created.GetProperty("state").GetString().ShouldBe("Draft");
        created.GetProperty("currentRevision").GetInt32().ShouldBe(1);
        created.GetProperty("currentTarget").GetProperty("storageProfileStableName").GetString().ShouldBe("primary-store");
        created.GetProperty("revisionPage").GetProperty("items").GetArrayLength().ShouldBe(1);

        var append = await SendAsync(world, HttpMethod.Post, $"/api/storage/routes/{routeId}/revisions", new
        {
            expectedXmin = created.GetProperty("xmin").GetUInt32(), expectedCurrentRevision = 1,
            storageProfileId = secondProfile, profileRevisionMode = "Pinned", pinnedProfileRevision = 1,
        });
        append.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(append));
        var revised = Json(await append.Content.ReadAsStringAsync());
        revised.GetProperty("currentRevision").GetInt32().ShouldBe(2);
        revised.GetProperty("revisionPage").GetProperty("items").EnumerateArray().Select(value => value.GetProperty("revision").GetInt32()).ShouldBe([2, 1]);
        revised.GetProperty("currentTarget").GetProperty("pinnedProfileRevision").GetInt32().ShouldBe(1);

        var activate = await SetStateAsync(world, routeId, revised, "Active");
        activate.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(activate));
        var active = Json(await activate.Content.ReadAsStringAsync());

        var page = await SendAsync(world, HttpMethod.Get, "/api/storage/routes/page?limit=1");
        page.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(page));
        var pageJson = Json(await page.Content.ReadAsStringAsync());
        pageJson.GetProperty("items").GetArrayLength().ShouldBe(1);
        pageJson.GetProperty("items")[0].GetProperty("storageProfileId").GetGuid().ShouldBe(secondProfile);
        AssertNoProfileMaterial(pageJson.GetRawText());

        var get = await SendAsync(world, HttpMethod.Get, $"/api/storage/routes/{routeId}");
        get.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(get));
        AssertNoProfileMaterial(await get.Content.ReadAsStringAsync());

        var retire = await SetStateAsync(world, routeId, active, "Retired");
        retire.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(retire));
        Json(await retire.Content.ReadAsStringAsync()).GetProperty("state").GetString().ShouldBe("Retired");
    }

    [Fact]
    public async Task A_route_can_only_name_a_data_class_this_build_actually_reads()
    {
        var world = await SeedWorldAsync(TeamRole.Admin);
        var profileId = await SeedProfileAsync(world.TeamId, world.UserId, "known-class-store");

        var discovery = await SendAsync(world, HttpMethod.Get, "/api/storage/data-classes");
        discovery.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(discovery));
        Json(await discovery.Content.ReadAsStringAsync()).EnumerateArray().Select(value => value.GetProperty("typeKey").GetString())
            .ShouldBe(["agent-run-log/v1", "workflow-artifact/v1"], "Settings can only offer classes some runtime consumer reads");

        // The plural an operator types by hand. It satisfies the open key pattern, so it used to create a listable
        // route that no consumer ever asks for — storage the operator believes is configured and that routes nothing.
        var typo = await SendAsync(world, HttpMethod.Post, "/api/storage/routes", new { dataClassTypeKey = "workflow-artifacts/v1", storageProfileId = profileId });
        typo.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await DescribeAsync(typo));
        (await typo.Content.ReadAsStringAsync()).ShouldContain("workflow-artifact/v1", Case.Sensitive,
            "the refusal must name the keys the operator can actually choose");

        var known = await SendAsync(world, HttpMethod.Post, "/api/storage/routes", new { dataClassTypeKey = " Workflow-Artifact/v1 ", storageProfileId = profileId });
        known.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(known));

        using var scope = _factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().StorageRoute.AsNoTracking()
            .Where(route => route.TeamId == world.TeamId).Select(route => route.DataClassTypeKey).ToListAsync();
        stored.ShouldBe(["workflow-artifact/v1"]);
    }

    [Fact]
    public async Task Authentication_storage_admin_permission_and_team_scope_fail_closed()
    {
        var member = await SeedWorldAsync(TeamRole.Member);
        var profileId = await SeedProfileAsync(member.TeamId, member.UserId, "member-store");
        var body = new { dataClassTypeKey = "workflow-artifact/v1", storageProfileId = profileId };

        var anonymous = await SendAsync(member, HttpMethod.Post, "/api/storage/routes", body, authenticated: false);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var forbidden = await SendAsync(member, HttpMethod.Post, "/api/storage/routes", body);
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await DescribeAsync(forbidden));
        var foreign = await SendAsync(member with { TeamId = member.ForeignTeamId }, HttpMethod.Get, "/api/storage/routes/page");
        foreign.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await DescribeAsync(foreign));
    }

    [Fact]
    public async Task Profile_ownership_active_state_and_exact_pinned_revision_are_validated()
    {
        var world = await SeedWorldAsync(TeamRole.Admin, includeForeignMembership: true);
        var active = await SeedProfileAsync(world.TeamId, world.UserId, "active-store");
        var disabled = await SeedProfileAsync(world.TeamId, world.UserId, "disabled-store", StorageProfileState.Disabled);
        var foreign = await SeedProfileAsync(world.ForeignTeamId, world.UserId, "foreign-store");

        foreach (var item in new[]
        {
            new { Case = "a disabled profile", ProfileId = disabled, Revision = (int?)null },
            new { Case = "another team's profile", ProfileId = foreign, Revision = (int?)null },
            new { Case = "a pinned revision that does not exist", ProfileId = active, Revision = (int?)99 },
        })
        {
            // A data class this build routes, so the PROFILE rule is what refuses each case rather than the class gate.
            var response = await SendAsync(world, HttpMethod.Post, "/api/storage/routes", new
            {
                dataClassTypeKey = "workflow-artifact/v1", storageProfileId = item.ProfileId,
                profileRevisionMode = item.Revision.HasValue ? "Pinned" : "CurrentAtWrite", pinnedProfileRevision = item.Revision,
            });
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"{item.Case}: {await DescribeAsync(response)}");
        }

        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().StorageRoute.CountAsync(value => value.TeamId == world.TeamId)).ShouldBe(0);
    }

    [Fact]
    public async Task Route_pages_use_bounded_stable_keyset_order_without_overlap()
    {
        var world = await SeedWorldAsync(TeamRole.Admin);
        var profileId = await SeedProfileAsync(world.TeamId, world.UserId, "page-store");
        // A team can hold at most one route per data class, and this build routes two — so two rows and a page size of
        // one is the whole keyset surface there is to page.
        foreach (var key in new[] { "workflow-artifact/v1", "agent-run-log/v1" })
        {
            var created = await SendAsync(world, HttpMethod.Post, "/api/storage/routes", new { dataClassTypeKey = key, storageProfileId = profileId });
            created.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(created));
        }

        var firstResponse = await SendAsync(world, HttpMethod.Get, "/api/storage/routes/page?limit=1");
        var first = Json(await firstResponse.Content.ReadAsStringAsync());
        first.GetProperty("items").EnumerateArray().Select(value => value.GetProperty("dataClassTypeKey").GetString()).ShouldBe([
            "agent-run-log/v1",
        ]);
        var cursor = Uri.EscapeDataString(first.GetProperty("nextCursor").GetString()!);
        var secondResponse = await SendAsync(world, HttpMethod.Get, $"/api/storage/routes/page?limit=1&cursor={cursor}");
        var second = Json(await secondResponse.Content.ReadAsStringAsync());
        second.GetProperty("items").EnumerateArray().Select(value => value.GetProperty("dataClassTypeKey").GetString()).ShouldBe([
            "workflow-artifact/v1",
        ]);
        second.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Revision_history_is_bounded_descending_and_keyset_pages_have_no_duplicates()
    {
        var world = await SeedWorldAsync(TeamRole.Admin);
        var profileId = await SeedProfileAsync(world.TeamId, world.UserId, "history-store");
        var create = await SendAsync(world, HttpMethod.Post, "/api/storage/routes", new { dataClassTypeKey = "workflow-artifact/v1", storageProfileId = profileId });
        var current = Json(await create.Content.ReadAsStringAsync());
        var routeId = current.GetProperty("id").GetGuid();

        for (var revision = 2; revision <= 28; revision++)
        {
            var append = await SendAsync(world, HttpMethod.Post, $"/api/storage/routes/{routeId}/revisions", new
            {
                expectedXmin = current.GetProperty("xmin").GetUInt32(), expectedCurrentRevision = revision - 1,
                storageProfileId = profileId, profileRevisionMode = "CurrentAtWrite",
            });
            append.StatusCode.ShouldBe(HttpStatusCode.OK, $"revision {revision}: {await DescribeAsync(append)}");
            current = Json(await append.Content.ReadAsStringAsync());
        }

        current.GetProperty("revisionPage").GetProperty("items").GetArrayLength().ShouldBe(25);
        current.GetProperty("revisionPage").GetProperty("nextCursor").GetString().ShouldNotBeNullOrWhiteSpace();

        var seen = new List<int>();
        string? cursor = null;
        do
        {
            var suffix = cursor == null ? string.Empty : $"&revisionCursor={Uri.EscapeDataString(cursor)}";
            var response = await SendAsync(world, HttpMethod.Get, $"/api/storage/routes/{routeId}?revisionLimit=10{suffix}");
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(response));
            var page = Json(await response.Content.ReadAsStringAsync()).GetProperty("revisionPage");
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(value => value.GetProperty("revision").GetInt32()));
            cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextCursor").GetString();
        } while (cursor != null);

        seen.ShouldBe(Enumerable.Range(1, 28).Reverse());
        seen.Distinct().Count().ShouldBe(seen.Count);
    }

    [Fact]
    public async Task Concurrent_append_and_retire_have_one_winner_and_the_loser_is_a_conflict()
    {
        var world = await SeedWorldAsync(TeamRole.Admin);
        var profileId = await SeedProfileAsync(world.TeamId, world.UserId, "race-store");
        var create = await SendAsync(world, HttpMethod.Post, "/api/storage/routes", new { dataClassTypeKey = "agent-run-log/v1", storageProfileId = profileId });
        var original = Json(await create.Content.ReadAsStringAsync());
        var routeId = original.GetProperty("id").GetGuid();
        var expectedXmin = original.GetProperty("xmin").GetUInt32();

        var responses = await Task.WhenAll(
            SendAsync(world, HttpMethod.Post, $"/api/storage/routes/{routeId}/revisions", new
            {
                expectedXmin, expectedCurrentRevision = 1, storageProfileId = profileId, profileRevisionMode = "CurrentAtWrite",
            }),
            SendAsync(world, HttpMethod.Put, $"/api/storage/routes/{routeId}/state", new
            {
                expectedXmin, expectedCurrentRevision = 1, state = "Retired",
            }));

        responses.Select(response => response.StatusCode).OrderBy(status => status).ShouldBe([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        var loser = responses.Single(response => response.StatusCode == HttpStatusCode.Conflict);
        (await loser.Content.ReadAsStringAsync()).ShouldContain("storage_route_conflict");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var route = await db.StorageRoute.AsNoTracking().SingleAsync(value => value.Id == routeId);
        var revisionCount = await db.StorageRouteRevision.CountAsync(value => value.StorageRouteId == routeId);
        if (route.State == StorageRouteState.Retired) revisionCount.ShouldBe(1);
        else
        {
            route.State.ShouldBe(StorageRouteState.Draft);
            route.CurrentRevision.ShouldBe(2);
            revisionCount.ShouldBe(2);
        }
    }

    [Fact]
    public async Task Stale_writes_and_retired_mutations_do_not_change_history_or_identity()
    {
        var world = await SeedWorldAsync(TeamRole.Admin);
        var profileId = await SeedProfileAsync(world.TeamId, world.UserId, "concurrent-store");
        var createdResponse = await SendAsync(world, HttpMethod.Post, "/api/storage/routes", new { dataClassTypeKey = "workflow-artifact/v1", storageProfileId = profileId });
        var created = Json(await createdResponse.Content.ReadAsStringAsync());
        var routeId = created.GetProperty("id").GetGuid();

        var activate = await SetStateAsync(world, routeId, created, "Active");
        activate.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(activate));
        var active = Json(await activate.Content.ReadAsStringAsync());

        var stale = await SendAsync(world, HttpMethod.Post, $"/api/storage/routes/{routeId}/revisions", new
        {
            expectedXmin = created.GetProperty("xmin").GetUInt32(), expectedCurrentRevision = 1,
            storageProfileId = profileId, profileRevisionMode = "CurrentAtWrite",
        });
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict, await DescribeAsync(stale));

        var retire = await SetStateAsync(world, routeId, active, "Retired");
        var retired = Json(await retire.Content.ReadAsStringAsync());
        var append = await SendAsync(world, HttpMethod.Post, $"/api/storage/routes/{routeId}/revisions", new
        {
            expectedXmin = retired.GetProperty("xmin").GetUInt32(), expectedCurrentRevision = 1,
            storageProfileId = profileId, profileRevisionMode = "CurrentAtWrite",
        });
        append.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await DescribeAsync(append));
        var reactivate = await SetStateAsync(world, routeId, retired, "Active");
        reactivate.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await DescribeAsync(reactivate));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        (await db.StorageRouteRevision.CountAsync(value => value.StorageRouteId == routeId)).ShouldBe(1);
        var stored = await db.StorageRoute.AsNoTracking().SingleAsync(value => value.Id == routeId);
        stored.DataClassTypeKey.ShouldBe("workflow-artifact/v1");
        stored.State.ShouldBe(StorageRouteState.Retired);
    }

    private async Task<World> SeedWorldAsync(TeamRole role, bool includeForeignMembership = false)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var foreignTeamId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, SecurityStamp = TestToken.SeedStamp, Email = $"route-{suffix}@test.local", Name = "Storage Route", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.AddRange(
            new Team { Id = teamId, Slug = $"route-{suffix}", Name = "Routes", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId },
            new Team { Id = foreignTeamId, Slug = $"route-foreign-{suffix}", Name = "Foreign", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = role, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        if (includeForeignMembership)
            db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = foreignTeamId, UserId = userId, Role = role, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        await db.SaveChangesAsync();
        return new World(userId, teamId, foreignTeamId);
    }

    private async Task<Guid> SeedProfileAsync(Guid teamId, Guid actorId, string stableName, StorageProfileState state = StorageProfileState.Active, int revisions = 1)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var profile = new StorageProfile
        {
            Id = Guid.NewGuid(), TeamId = teamId, StableName = stableName, CurrentRevision = revisions, State = state,
            CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        };
        for (var revision = 1; revision <= revisions; revision++)
        {
            profile.Revisions.Add(new StorageProfileRevision
            {
                Id = Guid.NewGuid(), TeamId = teamId, StorageProfileId = profile.Id, Revision = revision,
                ProviderTypeKey = "local-rwx/v1", NonSecretConfigJson = "{\"rootPath\":\"/do-not-expose-route-config\"}",
                CredentialRef = "db:11111111-2222-3333-4444-555555555555:1",
                NamespaceFingerprint = $"sha256:{new string((char)('a' + revision - 1), 64)}", CreatedDate = now, CreatedBy = actorId,
            });
        }
        db.StorageProfile.Add(profile);
        await db.SaveChangesAsync();
        return profile.Id;
    }

    private Task<HttpResponseMessage> SetStateAsync(World world, Guid routeId, JsonElement current, string state) =>
        SendAsync(world, HttpMethod.Put, $"/api/storage/routes/{routeId}/state", new
        {
            expectedXmin = current.GetProperty("xmin").GetUInt32(),
            expectedCurrentRevision = current.GetProperty("currentRevision").GetInt32(), state,
        });

    private async Task<HttpResponseMessage> SendAsync(World world, HttpMethod method, string path, object? body = null, bool authenticated = true)
    {
        var request = new HttpRequestMessage(method, path);
        if (authenticated) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken.Mint(world.UserId, TestToken.SeedStamp));
        request.Headers.Add("X-Team-Id", world.TeamId.ToString());
        if (body != null) request.Content = JsonContent.Create(body);
        return await _factory.CreateClient().SendAsync(request);
    }

    private static void AssertNoProfileMaterial(string wire)
    {
        wire.ShouldNotContain("do-not-expose-route-config");
        wire.ShouldNotContain("credentialRef", Case.Insensitive);
        wire.ShouldNotContain("nonSecretConfig", Case.Insensitive);
        wire.ShouldNotContain("namespaceFingerprint", Case.Insensitive);
    }

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static async Task<string> DescribeAsync(HttpResponseMessage response) => $"got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}";
    private sealed record World(Guid UserId, Guid TeamId, Guid ForeignTeamId);
}
