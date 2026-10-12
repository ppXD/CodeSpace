using System.Net;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Repositories;

/// <summary>
/// 🟢 High fidelity (real HTTP pipeline and authorization behaviours, Postgres; the provider probe is a stub): the
/// credential and repository writes addressed by a resource id hold the same role bar as the writes that create those
/// resources.
///
/// <para>The gap this pins: these four commands carried only their resource marker, which checks that the resource is
/// in the caller's team and that the caller is a member — no role. A Viewer, refused when ADDING a credential, could
/// revoke the Owner's credential, re-point a repository at it, spend the bound credential on a provider probe, and
/// delete the repository. The architecture test that asserts every team write declares a permission only scanned
/// commands carrying the membership marker, so these four were never in its sight.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class ResourceKeyedWritePermissionE2ETests : IClassFixture<GitWireApiFactory>
{
    private readonly GitWireApiFactory _factory;
    private readonly TenantHarness _tenants;

    public ResourceKeyedWritePermissionE2ETests(GitWireApiFactory factory)
    {
        _factory = factory;
        _tenants = new TenantHarness(factory);
    }

    [Theory]
    [InlineData(TeamRole.Viewer)]
    [InlineData(TeamRole.Member)]
    public async Task Below_admin_every_resource_keyed_credential_and_repository_write_is_refused(TeamRole role)
    {
        var world = await SeedWorldAsync();
        var caller = await _tenants.AccountAsync();
        await _tenants.AddMemberAsync(world.TeamId, caller, role);

        var test = await _tenants.SendAsync(HttpMethod.Post, $"/api/repositories/{world.RepositoryId}/test", caller, world.TeamId);
        var relink = await _tenants.SendAsync(HttpMethod.Post, $"/api/repositories/{world.RepositoryId}/relink-credential", caller, world.TeamId, new { newCredentialId = world.SpareCredentialId });
        var revoke = await _tenants.SendAsync(HttpMethod.Post, $"/api/credentials/{world.SpareCredentialId}/revoke", caller, world.TeamId);
        var unbind = await _tenants.SendAsync(HttpMethod.Delete, $"/api/repositories/{world.RepositoryId}", caller, world.TeamId);

        test.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"POST /repositories/{{id}}/test — {await TenantHarness.DescribeAsync(test)}");
        relink.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"POST /repositories/{{id}}/relink-credential — {await TenantHarness.DescribeAsync(relink)}");
        revoke.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"POST /credentials/{{id}}/revoke — {await TenantHarness.DescribeAsync(revoke)}");
        unbind.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"DELETE /repositories/{{id}} — {await TenantHarness.DescribeAsync(unbind)}");

        _factory.Wire.ProbedCredentials.ShouldNotContain(world.BoundCredentialId, "a refused test must not spend the bound credential on the provider");

        var (spare, repository) = await _tenants.DbAsync(async db => (
            await db.Credential.AsNoTracking().SingleAsync(c => c.Id == world.SpareCredentialId),
            await db.Repository.AsNoTracking().SingleAsync(r => r.Id == world.RepositoryId)));

        spare.Status.ShouldBe(CredentialStatus.Active, "the Owner's credential is untouched");
        repository.CredentialId.ShouldBe(world.BoundCredentialId, "the repository still points where the Owner bound it");
        repository.DeletedDate.ShouldBeNull("the repository is still bound");
    }

    /// <summary>
    /// Any member can link a personal token of their own (the self-service identity endpoint takes no role), and the
    /// Personal tab's Disconnect is how they take it back. Gating revoke on credentials.manage outright closed the hole
    /// above and took that away from every Member and Viewer; it is theirs again, and only for their own credential.
    /// </summary>
    [Theory]
    [InlineData(TeamRole.Viewer)]
    [InlineData(TeamRole.Member)]
    public async Task Below_admin_a_member_disconnects_the_personal_credential_they_linked_and_no_other(TeamRole role)
    {
        var world = await SeedWorldAsync();
        var caller = await _tenants.AccountAsync();
        await _tenants.AddMemberAsync(world.TeamId, caller, role);

        var link = await _tenants.SendAsync(HttpMethod.Post, "/api/me/identities/pat", caller, world.TeamId, new { providerInstanceId = world.InstanceId, accessToken = $"own-pat-{Guid.NewGuid():N}" });
        link.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(link));
        var own = await _tenants.DbAsync(db => db.Credential.AsNoTracking().SingleAsync(c => c.ProviderInstanceId == world.InstanceId && c.OwnerUserId == caller));
        own.Ownership.ShouldBe(CredentialOwnership.Personal);

        var revokeOwn = await _tenants.SendAsync(HttpMethod.Post, $"/api/credentials/{own.Id}/revoke", caller, world.TeamId);
        var revokeTeammates = await _tenants.SendAsync(HttpMethod.Post, $"/api/credentials/{world.SpareCredentialId}/revoke", caller, world.TeamId);
        var revokeTeams = await _tenants.SendAsync(HttpMethod.Post, $"/api/credentials/{world.TeamCredentialId}/revoke", caller, world.TeamId);

        revokeOwn.StatusCode.ShouldBe(HttpStatusCode.OK, $"their own personal credential — {await TenantHarness.DescribeAsync(revokeOwn)}");
        revokeTeammates.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"the Owner's personal credential — {await TenantHarness.DescribeAsync(revokeTeammates)}");
        revokeTeams.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"the team's service credential — {await TenantHarness.DescribeAsync(revokeTeams)}");

        var statuses = await _tenants.DbAsync(db => db.Credential.AsNoTracking().Where(c => c.Id == own.Id || c.Id == world.SpareCredentialId || c.Id == world.TeamCredentialId).ToDictionaryAsync(c => c.Id, c => c.Status));
        statuses[own.Id].ShouldBe(CredentialStatus.Revoked);
        statuses[world.SpareCredentialId].ShouldBe(CredentialStatus.Active);
        statuses[world.TeamCredentialId].ShouldBe(CredentialStatus.Active);
    }

    [Fact]
    public async Task An_admin_revokes_a_teammates_personal_credential_and_the_teams_own()
    {
        var world = await SeedWorldAsync();
        var admin = await _tenants.AccountAsync();
        await _tenants.AddMemberAsync(world.TeamId, admin, TeamRole.Admin);

        var teammates = await _tenants.SendAsync(HttpMethod.Post, $"/api/credentials/{world.SpareCredentialId}/revoke", admin, world.TeamId);
        var teams = await _tenants.SendAsync(HttpMethod.Post, $"/api/credentials/{world.TeamCredentialId}/revoke", admin, world.TeamId);

        teammates.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(teammates));
        teams.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(teams));
    }

    [Fact]
    public async Task An_admin_performs_every_resource_keyed_credential_and_repository_write()
    {
        var world = await SeedWorldAsync();
        var admin = await _tenants.AccountAsync();
        await _tenants.AddMemberAsync(world.TeamId, admin, TeamRole.Admin);

        var test = await _tenants.SendAsync(HttpMethod.Post, $"/api/repositories/{world.RepositoryId}/test", admin, world.TeamId);
        test.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(test));

        var relink = await _tenants.SendAsync(HttpMethod.Post, $"/api/repositories/{world.RepositoryId}/relink-credential", admin, world.TeamId, new { newCredentialId = world.SpareCredentialId });
        relink.IsSuccessStatusCode.ShouldBeTrue(await TenantHarness.DescribeAsync(relink));

        var revoke = await _tenants.SendAsync(HttpMethod.Post, $"/api/credentials/{world.BoundCredentialId}/revoke", admin, world.TeamId);
        revoke.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(revoke));

        var unbind = await _tenants.SendAsync(HttpMethod.Delete, $"/api/repositories/{world.RepositoryId}", admin, world.TeamId);
        unbind.IsSuccessStatusCode.ShouldBeTrue(await TenantHarness.DescribeAsync(unbind));
    }

    [Fact]
    public async Task An_admin_of_another_team_still_reaches_none_of_them()
    {
        var world = await SeedWorldAsync();
        var outsider = await _tenants.AccountAsync();
        var outsiderTeam = await _tenants.TeamAsync((outsider, TeamRole.Owner));

        var revoke = await _tenants.SendAsync(HttpMethod.Post, $"/api/credentials/{world.SpareCredentialId}/revoke", outsider, outsiderTeam);
        var unbind = await _tenants.SendAsync(HttpMethod.Delete, $"/api/repositories/{world.RepositoryId}", outsider, outsiderTeam);

        revoke.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await TenantHarness.DescribeAsync(revoke));
        unbind.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await TenantHarness.DescribeAsync(unbind));
    }

    // ─── Plumbing ────────────────────────────────────────────────────────────────

    private sealed record World(Guid TeamId, Guid OwnerId, Guid InstanceId, Guid RepositoryId, Guid BoundCredentialId, Guid SpareCredentialId, Guid TeamCredentialId);

    /// <summary>A team whose Owner bound a repository through one credential and holds a second one for the same provider, beside a team service credential nobody owns.</summary>
    private async Task<World> SeedWorldAsync()
    {
        var owner = await _tenants.AccountAsync();
        var teamId = await _tenants.TeamAsync((owner, TeamRole.Owner));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..10];

        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = teamId, Provider = ProviderKind.Git, DisplayName = "Git", BaseUrl = $"https://git.stub.test/{suffix}", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };
        var bound = BuildCredential(scope.ServiceProvider, teamId, instance.Id, owner, "bound");
        var spare = BuildCredential(scope.ServiceProvider, teamId, instance.Id, owner, "spare");
        var teamService = BuildCredential(scope.ServiceProvider, teamId, instance.Id, ownerId: null, "team-service");
        var repository = new Repository { Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instance.Id, CredentialId = bound.Id, ExternalId = $"ext-{suffix}", NamespacePath = "acme", Name = $"private-{suffix}", FullPath = $"acme/private-{suffix}", DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = "https://git.stub.test", Status = RepositoryStatus.Active, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };

        db.ProviderInstance.Add(instance);
        db.Credential.AddRange(bound, spare, teamService);
        db.Repository.Add(repository);
        await db.SaveChangesAsync();

        return new World(teamId, owner, instance.Id, repository.Id, bound.Id, spare.Id, teamService.Id);
    }

    private static Credential BuildCredential(IServiceProvider services, Guid teamId, Guid instanceId, Guid? ownerId, string label)
    {
        var payload = services.GetRequiredService<ICredentialPayloadSerializer>().Serialize(new PatPayload { Token = $"pat-{label}-{Guid.NewGuid():N}" });

        return new Credential { Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instanceId, OwnerUserId = ownerId, Ownership = ownerId == null ? CredentialOwnership.TeamService : CredentialOwnership.Personal, AuthType = AuthType.Pat, DisplayName = label, EncryptedPayload = services.GetRequiredService<IPayloadEncryptor>().Encrypt(payload), Status = CredentialStatus.Active, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };
    }
}
