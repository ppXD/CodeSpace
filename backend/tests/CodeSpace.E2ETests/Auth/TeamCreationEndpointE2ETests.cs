using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Auth;

/// <summary>
/// That a <c>user_permission</c> row actually grants a capability — on the wire, where the real
/// <c>ApiUser</c> runs the role/permission join.
///
/// <para>The integration suite cannot make this claim. Its identity double reports whatever the test
/// hands it and never reads the database, so a granted row there proves the BEHAVIOR reads
/// <c>ICurrentUser.Permissions</c> and nothing about whether a grant reaches it. The instance
/// permission tables sat unused since migration 0004; this is the first thing that depends on them
/// working end to end, and the only place that can be shown is here.</para>
///
/// <para>Tier: 🟢 High-fidelity — real app host, real Postgres, real pipeline.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class TeamCreationEndpointE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;

    public TeamCreationEndpointE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task An_account_with_no_grant_is_refused()
    {
        var userId = await SeedAccountAsync(granted: false);

        var response = await CreateTeamAsync(userId, "Ungranted");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await DescribeAsync(response, "403"));
    }

    [Fact]
    public async Task A_row_in_user_permission_is_what_lets_them_through()
    {
        // The whole claim: the ONLY difference between this and the test above is one row in the
        // instance permission table. If the join in ApiUser ever stops working, this reds and that
        // one stays green.
        var userId = await SeedAccountAsync(granted: true);

        var response = await CreateTeamAsync(userId, "Granted Workspace");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(response, "200"));

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("slug").GetString().ShouldBe("granted-workspace");
    }

    [Fact]
    public async Task The_created_team_shows_up_as_the_creator_owning_it()
    {
        // Proved through /me rather than the schema, because that projection is what the client uses
        // to decide the team exists and what may be done in it.
        var userId = await SeedAccountAsync(granted: true);

        var created = await CreateTeamAsync(userId, "Visible To Me");
        created.StatusCode.ShouldBe(HttpStatusCode.OK);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/users/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TestToken.ForAsync(_factory, userId));
        var me = await _factory.CreateClient().SendAsync(request);

        var teams = JsonDocument.Parse(await me.Content.ReadAsStringAsync()).RootElement.GetProperty("teams");
        var mine = teams.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "Visible To Me");

        mine.GetProperty("role").GetString().ShouldBe("Owner");
        mine.GetProperty("permissions").GetArrayLength().ShouldBeGreaterThan(0, "an owner of a team they just made holds its capabilities");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> CreateTeamAsync(Guid userId, string name)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/teams") { Content = JsonContent.Create(new { name }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TestToken.ForAsync(_factory, userId));

        return await _factory.CreateClient().SendAsync(request);
    }

    private async Task<Guid> SeedAccountAsync(bool granted)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"creator-{Guid.NewGuid():N}@test.local",
            Name = "Creator",
            SecurityStamp = TestToken.SeedStamp,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        };

        db.User.Add(user);

        if (granted)
        {
            var permissionId = await db.Permission.AsNoTracking().Where(p => p.Name == Permissions.TeamsCreate).Select(p => p.Id).SingleAsync();
            db.UserPermission.Add(new UserPermission { Id = Guid.NewGuid(), UserId = user.Id, PermissionId = permissionId, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        }

        await db.SaveChangesAsync();

        return user.Id;
    }

    private static async Task<string> DescribeAsync(HttpResponseMessage response, string expectation) =>
        $"expected {expectation}; got {(int)response.StatusCode}; body: {await response.Content.ReadAsStringAsync()}";
}
