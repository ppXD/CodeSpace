using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Auth;

/// <summary>
/// The whole reason the account system exists, in one test: somebody opens a workspace, and somebody
/// else ends up working in it.
///
/// <para>Every link of that chain was covered before this class, and the chain itself was not. The
/// tests split at the invitation: creation was exercised against teams made through the real endpoint,
/// while every invite and accept test ran against a team hand-inserted by a seeder. No test file in the
/// repo contained both <c>CreateTeamCommand</c> and <c>AcceptInvitationCommand</c>. The seam between
/// the halves — does a team you JUST made confer the right to invite into it — was the untested part,
/// and it is the part an operator hits first.</para>
///
/// <para>Wire level specifically. <c>members.manage</c> is derived from the creator's team role rather
/// than stored, so it crosses the boundary as a projection the client reads and branches on; a
/// successful <c>POST /api/teams/invitations</c> had never gone through real model binding at all. That
/// is the layer the integration tier skips and the one that has already produced a shipped bug here —
/// see <see cref="InvitationEndpointE2ETests"/>.</para>
///
/// <para>Tier: 🟢 High-fidelity — real app host, real Postgres, real pipeline, real JWTs.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class OpenAWorkspaceAndFillItE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;

    public OpenAWorkspaceAndFillItE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task Someone_who_may_open_a_workspace_can_staff_it()
    {
        var creator = await SeedGrantedAccountAsync();

        // ── Opens it ──────────────────────────────────────────────────────────────
        var created = await PostAsync(creator, "/api/teams", new { name = "Design Guild" });
        created.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(created, "200 from POST /api/teams"));

        var team = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        var teamId = team.GetProperty("id").GetGuid();

        team.GetProperty("slug").GetString().ShouldBe("design-guild");

        // ── And is, by that act, someone who may invite into it ───────────────────
        // Asserted on the projection the client actually branches on, and against the matrix rather
        // than a literal list — a hard-coded list here would be a second copy of the access policy.
        var mine = (await MeTeamsAsync(creator)).Single(t => t.GetProperty("id").GetGuid() == teamId);

        mine.GetProperty("role").GetString().ShouldBe(nameof(TeamRole.Owner));
        Permissions(mine).ShouldContain(TeamPermissions.MembersManage, "opening a workspace is worth nothing if you cannot then put anyone in it");

        // ── Invites ───────────────────────────────────────────────────────────────
        var invited = await PostAsync(creator, "/api/teams/invitations", new { email = "maya@guild.test", role = nameof(TeamRole.Admin) }, teamId);
        invited.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(invited, "200 from POST /api/teams/invitations"));

        var inviteUrl = JsonDocument.Parse(await invited.Content.ReadAsStringAsync()).RootElement.GetProperty("inviteUrl").GetString();
        var token = inviteUrl!.Split('/').Last();

        // ── The invitee, who has no account and no session, follows the link ──────
        var preview = await _factory.CreateClient().GetAsync($"/api/invitations/{token}");
        preview.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(preview, "200 — the link has to be readable by a stranger"));

        var offer = JsonDocument.Parse(await preview.Content.ReadAsStringAsync()).RootElement;
        offer.GetProperty("teamName").GetString().ShouldBe("Design Guild", "a person deciding whether to accept is owed the name of what they are joining");
        offer.GetProperty("accountExists").GetBoolean().ShouldBeFalse();

        // ── And accepts ───────────────────────────────────────────────────────────
        var accepted = await _factory.CreateClient().PostAsJsonAsync($"/api/invitations/{token}/accept", new { name = "Maya", password = "correct-horse-battery" });
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(accepted, "200 with a session"));

        var session = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync()).RootElement;
        session.GetProperty("token").GetString().ShouldNotBeNullOrWhiteSpace("the invitee lands signed in, not at a login form");

        var joined = session.GetProperty("user").GetProperty("teams").EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == teamId);
        joined.GetProperty("role").GetString().ShouldBe(nameof(TeamRole.Admin), "they arrive at the role they were offered, not a default");

        // ── Both people are in the workspace, and the newcomer can see that ───────
        var roster = await GetAsync(session.GetProperty("user").GetProperty("id").GetGuid(), "/api/teams/members", teamId);
        roster.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(roster, "200 — the newcomer reads the roster with their own session"));

        var people = JsonDocument.Parse(await roster.Content.ReadAsStringAsync()).RootElement.EnumerateArray()
            .ToDictionary(m => m.GetProperty("email").GetString()!, m => m.GetProperty("role").GetString());

        people.ShouldContainKeyAndValue("maya@guild.test", nameof(TeamRole.Admin));
        people.Count.ShouldBe(2, "the creator and the person they invited — nobody else, and neither of them missing");
    }

    /// <summary>
    /// The chain does not stop at the person you invited. Opening a workspace is what every account
    /// here may do — you own what you open and staff it yourself — so an account that arrived through
    /// an invitation is not a lesser kind of account that has to ask an administrator to start.
    ///
    /// <para>Wire level because the grant is written by the acceptance path and read back by the
    /// authorization behavior on a LATER request, under a different session. Nothing below this tier
    /// crosses both.</para>
    /// </summary>
    [Fact]
    public async Task Someone_you_invited_can_open_a_workspace_of_their_own()
    {
        var founder = await SeedGrantedAccountAsync();

        var created = await PostAsync(founder, "/api/teams", new { name = "Founding Team" });
        var teamId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var invited = await PostAsync(founder, "/api/teams/invitations", new { email = "newcomer@guild.test", role = nameof(TeamRole.Member) }, teamId);
        var token = JsonDocument.Parse(await invited.Content.ReadAsStringAsync()).RootElement.GetProperty("inviteUrl").GetString()!.Split('/').Last();

        var accepted = await _factory.CreateClient().PostAsJsonAsync($"/api/invitations/{token}/accept", new { name = "Newcomer", password = "correct-horse-battery" });
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(accepted, "200 with a session"));

        // Their own session, not the founder's — the newcomer was invited at Member, the lowest role
        // that can do anything, so nothing about their standing in that team is carrying this.
        var newcomerId = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync()).RootElement.GetProperty("user").GetProperty("id").GetGuid();

        var theirs = await PostAsync(newcomerId, "/api/teams", new { name = "Newcomer Workshop" });

        theirs.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(theirs, "200 — opening a workspace is not a privilege an administrator hands out"));

        var mine = (await MeTeamsAsync(newcomerId)).Single(t => t.GetProperty("name").GetString() == "Newcomer Workshop");
        mine.GetProperty("role").GetString().ShouldBe(nameof(TeamRole.Owner));
        Permissions(mine).ShouldContain(TeamPermissions.MembersManage, "and they can staff it in turn");
    }

    [Fact]
    public async Task A_workspace_you_did_not_open_is_not_yours_to_staff()
    {
        // The other half of the same claim. Creating a team confers management of THAT team; if the
        // grant leaked to any team the caller can name, the header would be an authorization bypass.
        var creator = await SeedGrantedAccountAsync();
        var stranger = await SeedGrantedAccountAsync();

        var created = await PostAsync(creator, "/api/teams", new { name = "Not Yours" });
        var teamId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var attempt = await PostAsync(stranger, "/api/teams/invitations", new { email = "gate@crash.test", role = nameof(TeamRole.Member) }, teamId);

        attempt.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await DescribeAsync(attempt, "403 — naming someone else's team in the header is not membership of it"));
    }

    // ─── Drivers ────────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> PostAsync(Guid userId, string path, object body, Guid? teamId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };

        await AuthenticateAsync(request, userId, teamId);

        return await _factory.CreateClient().SendAsync(request);
    }

    private async Task<HttpResponseMessage> GetAsync(Guid userId, string path, Guid? teamId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);

        await AuthenticateAsync(request, userId, teamId);

        return await _factory.CreateClient().SendAsync(request);
    }

    private async Task AuthenticateAsync(HttpRequestMessage request, Guid userId, Guid? teamId)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TestToken.ForAsync(_factory, userId));

        if (teamId != null) request.Headers.Add(HeaderCurrentTeam.HeaderName, teamId.Value.ToString());
    }

    private async Task<IReadOnlyList<JsonElement>> MeTeamsAsync(Guid userId)
    {
        var response = await GetAsync(userId, "/api/users/me");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(response, "200 from /api/users/me"));

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("teams").EnumerateArray().ToList();
    }

    private static IReadOnlyList<string> Permissions(JsonElement team) =>
        team.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToList();

    /// <summary>An account holding <c>teams.create</c> by an individual grant — no Admin role, so the
    /// role's blanket bypass cannot be what carries the request through.</summary>
    private async Task<Guid> SeedGrantedAccountAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"founder-{Guid.NewGuid():N}@test.local",
            Name = "Founder",
            SecurityStamp = TestToken.SeedStamp,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        };

        var permissionId = await db.Permission.AsNoTracking().Where(p => p.Name == Messages.Constants.Permissions.TeamsCreate).Select(p => p.Id).SingleAsync();

        db.User.Add(user);
        db.UserPermission.Add(new UserPermission { Id = Guid.NewGuid(), UserId = user.Id, PermissionId = permissionId, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();

        return user.Id;
    }

    private static async Task<string> DescribeAsync(HttpResponseMessage response, string expectation) =>
        $"expected {expectation}; got {(int)response.StatusCode}; body: {await response.Content.ReadAsStringAsync()}";
}
