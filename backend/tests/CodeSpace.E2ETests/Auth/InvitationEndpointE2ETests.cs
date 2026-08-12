using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Auth;

/// <summary>
/// The invitation endpoints on the WIRE, through the real ASP.NET pipeline — routing, model binding,
/// the anonymous exemption from the global fallback policy, and the exception filter.
///
/// <para>The bug this pins: <c>AcceptInvitationCommand.Token</c> is supplied by the route and merged
/// by the controller, so the request body legitimately omits it. Declared as a non-nullable string it
/// became implicitly required under <c>[ApiController]</c>, and every acceptance was rejected by model
/// binding with <c>"The Token field is required."</c> — a 400 raised BEFORE the controller ran, so no
/// filter shaped it and the invitee saw a bare "Bad Request".</para>
///
/// <para>Nineteen integration tests covered this flow and every one of them passed, because they
/// dispatch the command through the mediator directly. Model binding is the one layer they skip, and
/// it is where the bug lived. Only the wire can catch it.</para>
///
/// <para>Tier: 🟢 High-fidelity — real app host, real Postgres, real pipeline.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class InvitationEndpointE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;

    public InvitationEndpointE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task Accepting_over_http_creates_the_account_and_returns_a_session()
    {
        var (token, email) = await SeedInvitationAsync();

        var response = await _factory.CreateClient().PostAsJsonAsync($"/api/invitations/{token}/accept", new { name = "Wire Newcomer", password = "correct-horse-battery" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(response, "200 with a session"));

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("token").GetString().ShouldNotBeNullOrWhiteSpace("the invitee must land signed in, not at a login form");
        body.GetProperty("user").GetProperty("email").GetString().ShouldBe(email);

        // The shipped page reads user.teams to pick which team to land in.
        body.GetProperty("user").GetProperty("teams").GetArrayLength().ShouldBeGreaterThanOrEqualTo(2, "the invited team plus the personal workspace acceptance creates");
    }

    [Fact]
    public async Task The_body_does_not_have_to_repeat_the_token_that_is_already_in_the_route()
    {
        // The regression itself, stated as the contract: a body of exactly what the client sends must
        // not be rejected for omitting a field the URL already carries.
        var (token, _) = await SeedInvitationAsync();

        var response = await _factory.CreateClient().PostAsJsonAsync($"/api/invitations/{token}/accept", new { name = "No Token In Body", password = "correct-horse-battery" });

        response.StatusCode.ShouldNotBe(HttpStatusCode.BadRequest, await DescribeAsync(response, "anything but a model-binding rejection"));
    }

    [Fact]
    public async Task Previewing_needs_no_session()
    {
        // Anonymous by design: the person holding the link has no account. If the global fallback policy
        // ever stops being exempted here, this endpoint 401s and the page bounces them to sign in for an
        // account they do not have.
        var (token, email) = await SeedInvitationAsync();

        var response = await _factory.CreateClient().GetAsync($"/api/invitations/{token}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(response, "200 without any Authorization header"));

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("email").GetString().ShouldBe(email);
        body.GetProperty("teamName").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_token_that_resolves_to_nothing_is_a_shaped_404_the_page_can_render()
    {
        // Not a masked 500 and not a bare framework 400: the page renders its own terminal state from
        // this, so it has to arrive with the envelope every other failure uses.
        var response = await _factory.CreateClient().GetAsync("/api/invitations/not-a-real-token");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await DescribeAsync(response, "404"));

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("code").GetString().ShouldBe("invitation_not_usable");
        body.GetProperty("message").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Inviting_without_a_session_is_refused()
    {
        // Only the two token-bearing surfaces are anonymous. The management side must still be behind
        // the fallback policy — an open invite endpoint would let anyone add themselves to any team.
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/teams/invitations", new { email = "nobody@test.local", role = "Member" });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await DescribeAsync(response, "401"));
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds a pending invitation straight to the row, hashing the token the same way the service does.
    /// Going through the create endpoint would need a signed-in member with members.manage, which is a
    /// different thing to test and would make a failure here ambiguous.
    /// </summary>
    private async Task<(string Token, string Email)> SeedInvitationAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var owner = new User { Id = Guid.NewGuid(), Email = $"inviter-{suffix}@test.local", Name = "Inviter", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"wire-{suffix}", Name = "Wire Team", OwnerUserId = owner.Id, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };

        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var email = $"invitee-{suffix}@test.local";

        db.User.Add(owner);
        db.Team.Add(team);
        db.TeamInvitation.Add(new TeamInvitation
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            Email = email,
            Role = TeamRole.Member,
            TokenHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))),
            Status = InvitationStatus.Pending,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            InvitedByUserId = owner.Id,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId
        });

        await db.SaveChangesAsync();

        return (token, email);
    }

    private static async Task<string> DescribeAsync(HttpResponseMessage response, string expectation) =>
        $"expected {expectation}; got {(int)response.StatusCode}; body: {await response.Content.ReadAsStringAsync()}";
}
