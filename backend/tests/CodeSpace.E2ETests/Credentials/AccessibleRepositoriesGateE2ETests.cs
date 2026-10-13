using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.E2ETests.Credentials;

/// <summary>
/// Listing what a credential reaches (GET /api/credentials/{id}/accessible-repositories) presents that credential to the
/// provider. On membership alone a Viewer could make the platform call GitLab with the Owner's personal token and read
/// back every project the Owner's account can see — private ones outside the team included. Now a Personal credential
/// lists for its owner or a repos.manage holder, and a team-service credential for a repos.manage holder; everyone else
/// is refused before the token leaves the platform.
///
/// <para>Driven through the real HTTP surface (POST /api/provider-instances, POST /api/credentials/pat and
/// /group-access-token, GET accessible-repositories), the real mediator pipeline and the real GitLab provider over
/// NGitLab, against a GitLab on a loopback port that records which token each request carried.</para>
///
/// <para>Fidelity: HIGH. Only GitLab is a stub, and it is a real socket the production client speaks HTTP to.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class AccessibleRepositoriesGateE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TenantHarness _tenants;

    public AccessibleRepositoriesGateE2ETests(TaskLaunchApiFactory factory) { _tenants = new TenantHarness(factory); }

    public enum Holder { OwnersPersonal, MembersPersonal, Team }

    [Theory]
    [InlineData(TeamRole.Viewer, Holder.OwnersPersonal, HttpStatusCode.Forbidden)]
    [InlineData(TeamRole.Member, Holder.OwnersPersonal, HttpStatusCode.Forbidden)]
    [InlineData(TeamRole.Member, Holder.Team, HttpStatusCode.Forbidden)]
    [InlineData(TeamRole.Viewer, Holder.Team, HttpStatusCode.Forbidden)]
    [InlineData(TeamRole.Member, Holder.MembersPersonal, HttpStatusCode.OK)]
    [InlineData(TeamRole.Owner, Holder.OwnersPersonal, HttpStatusCode.OK)]
    [InlineData(TeamRole.Admin, Holder.OwnersPersonal, HttpStatusCode.OK)]
    [InlineData(TeamRole.Admin, Holder.Team, HttpStatusCode.OK)]
    public async Task Only_the_owner_of_a_personal_credential_or_a_repos_manage_holder_lists_what_it_reaches(TeamRole caller, Holder holder, HttpStatusCode expected)
    {
        await using var gitLab = RecordingProviderStub.Start();
        var world = await SeedAsync(gitLab.BaseUrl);
        var (credentialId, token) = world.Credential(holder);

        var listing = await _tenants.SendAsync(HttpMethod.Get, $"/api/credentials/{credentialId}/accessible-repositories", world.User(caller), world.Team);

        listing.StatusCode.ShouldBe(expected, $"{caller} listing through {holder} — {await TenantHarness.DescribeAsync(listing)}");

        if (expected == HttpStatusCode.OK)
        {
            gitLab.Carrying(token).ShouldNotBeEmpty("fixture check: an allowed listing presents the token to GitLab");
            return;
        }

        gitLab.Carrying(token).ShouldBeEmpty($"{caller} was refused before the {holder} token was ever presented to the provider");
    }

    private sealed record World(Guid Team, Guid Owner, Guid Admin, Guid Member, Guid Viewer, (Guid, string) OwnersPat, (Guid, string) MembersPat, (Guid, string) TeamToken)
    {
        public Guid User(TeamRole role) => role switch { TeamRole.Owner => Owner, TeamRole.Admin => Admin, TeamRole.Member => Member, _ => Viewer };

        public (Guid Id, string Token) Credential(Holder holder) => holder switch { Holder.OwnersPersonal => OwnersPat, Holder.MembersPersonal => MembersPat, _ => TeamToken };
    }

    /// <summary>A team of four roles on one GitLab connection: the Owner's personal token, the Member's (added for them by the Owner), and a team-service group token.</summary>
    private async Task<World> SeedAsync(string gitLabUrl)
    {
        var owner = await _tenants.AccountAsync();
        var admin = await _tenants.AccountAsync();
        var member = await _tenants.AccountAsync();
        var viewer = await _tenants.AccountAsync();
        var team = await _tenants.TeamAsync((owner, TeamRole.Owner), (admin, TeamRole.Admin), (member, TeamRole.Member), (viewer, TeamRole.Viewer));
        var canary = Guid.NewGuid().ToString("N");
        var instanceId = await CreatedIdAsync(await _tenants.SendAsync(HttpMethod.Post, "/api/provider-instances", owner, team, new { provider = "GitLab", displayName = "gitlab", baseUrl = gitLabUrl }));

        var ownersPat = $"glpat-owner-{canary}";
        var membersPat = $"glpat-member-{canary}";
        var teamToken = $"glgat-team-{canary}";
        var ownersId = await CreatedIdAsync(await _tenants.SendAsync(HttpMethod.Post, "/api/credentials/pat", owner, team, new { providerInstanceId = instanceId, displayName = "owner pat", token = ownersPat }));
        var membersId = await CreatedIdAsync(await _tenants.SendAsync(HttpMethod.Post, "/api/credentials/pat", owner, team, new { providerInstanceId = instanceId, ownerUserId = member, displayName = "member pat", token = membersPat }));
        var teamId = await CreatedIdAsync(await _tenants.SendAsync(HttpMethod.Post, "/api/credentials/group-access-token", owner, team, new { providerInstanceId = instanceId, displayName = "team token", token = teamToken }));

        return new World(team, owner, admin, member, viewer, (ownersId, ownersPat), (membersId, membersPat), (teamId, teamToken));
    }

    private static async Task<Guid> CreatedIdAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(response));

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}
