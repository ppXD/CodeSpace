using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.ProviderInstances;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.ProviderInstances;

/// <summary>
/// A git provider's API address is asked once, when it is typed, whether it redirects to another origin — so the redirect is
/// named to the owner who typed it, with the URL it points at, rather than showing up later as a 401 on every call. An add
/// that redirects stores nothing; an edit that moves the address to one that redirects changes nothing. Existing
/// connections are never rewritten and never blocked: an edit that leaves the address as stored asks nothing, so a
/// connection whose stored URL already redirects keeps saving its other fields. A repoint the endpoint-binding rule refuses
/// is refused before anything is asked, so the host it named is never contacted.
///
/// <para>The probe is the suite's fake (<see cref="TestProviderEndpointProbe"/>), which records what it was asked; the real
/// probe's wire behaviour is pinned beside the class itself, against a loopback host.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ProviderInstanceRedirectProbeFlowTests
{
    private readonly PostgresFixture _fixture;

    public ProviderInstanceRedirectProbeFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task An_add_whose_address_redirects_to_another_origin_stores_nothing_and_names_the_origin()
    {
        var teamId = await SeedTeamAsync();
        var address = UniqueAddress("http");
        Probe().RedirectsTo(address, "https://gitlab.acme.test/api/v4/version");

        var refusal = await Should.ThrowAsync<ProviderBaseUrlRedirectsException>(() => AddAsync(teamId, new AddProviderInstanceCommand { Provider = ProviderKind.GitLab, DisplayName = "GitLab", BaseUrl = address }));

        refusal.FinalOrigin.ShouldBe("https://gitlab.acme.test");
        refusal.Message.ShouldContain("https://gitlab.acme.test");
        (await CountAsync(teamId)).ShouldBe(0, "a refused add stores nothing");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_add_that_does_not_redirect_is_stored_as_written_and_its_api_address_is_the_one_asked_about(bool withApiUrl)
    {
        var teamId = await SeedTeamAsync();
        var baseUrl = UniqueAddress("https");
        var apiUrl = withApiUrl ? UniqueAddress("https") + "/api-root" : null;

        var id = await AddAsync(teamId, new AddProviderInstanceCommand { Provider = ProviderKind.GitLab, DisplayName = "GitLab", BaseUrl = baseUrl + "/", ApiUrl = apiUrl });

        var row = await LoadAsync(id);
        row.BaseUrl.ShouldBe(baseUrl, "stored exactly as an add always stored it: trailing slash trimmed, nothing rewritten");
        row.ApiUrl.ShouldBe(apiUrl);
        Probe().Asked.ShouldContain(apiUrl ?? baseUrl, "provider calls go to the API address when one is set, else the base URL");
    }

    [Fact]
    public async Task A_duplicate_add_is_refused_before_anything_is_asked()
    {
        var teamId = await SeedTeamAsync();
        var address = UniqueAddress("https");
        await AddAsync(teamId, new AddProviderInstanceCommand { Provider = ProviderKind.GitLab, DisplayName = "GitLab", BaseUrl = address });
        var askedBefore = Probe().Asked.Count(a => a == address);

        await Should.ThrowAsync<InvalidOperationException>(() => AddAsync(teamId, new AddProviderInstanceCommand { Provider = ProviderKind.GitLab, DisplayName = "again", BaseUrl = address }));

        Probe().Asked.Count(a => a == address).ShouldBe(askedBefore, "the cheap local refusal comes first");
    }

    [Fact]
    public async Task An_edit_that_moves_the_address_to_one_that_redirects_changes_nothing()
    {
        var (teamId, instanceId, stored) = await SeedInstanceAsync();
        var address = UniqueAddress("http");
        Probe().RedirectsTo(address, $"https://{new Uri(address).Host}:8443/api/v4/version");

        var refusal = await Should.ThrowAsync<ProviderBaseUrlRedirectsException>(() => UpdateAsync(teamId, new UpdateProviderInstanceCommand { ProviderInstanceId = instanceId, DisplayName = "renamed", BaseUrl = address }));

        refusal.FinalOrigin.ShouldEndWith(":8443");
        var row = await LoadAsync(instanceId);
        row.BaseUrl.ShouldBe(stored);
        row.DisplayName.ShouldBe("GitLab", "a refused edit changes nothing, the rest of it included");
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("same base url, trailing slash")]
    [InlineData("same api url")]
    public async Task An_edit_that_leaves_the_address_as_stored_asks_nothing_even_when_that_address_redirects(string edit)
    {
        // Existing connections are never blocked by this: a stored URL that already redirects keeps saving its other
        // fields, and its calls name the redirect when they meet it.
        var (teamId, instanceId, stored) = await SeedInstanceAsync(apiUrl: UniqueAddress("http") + "/api-root");
        var row = await LoadAsync(instanceId);
        Probe().RedirectsTo(stored, "https://gitlab.acme.test/api/v4/version");
        Probe().RedirectsTo(row.ApiUrl!, "https://gitlab.acme.test/api/v4/version");
        var askedBefore = Probe().Asked.Count;

        await UpdateAsync(teamId, edit switch
        {
            "rename" => new UpdateProviderInstanceCommand { ProviderInstanceId = instanceId, DisplayName = "renamed" },
            "same base url, trailing slash" => new UpdateProviderInstanceCommand { ProviderInstanceId = instanceId, BaseUrl = stored + "/", DisplayName = "renamed" },
            _ => new UpdateProviderInstanceCommand { ProviderInstanceId = instanceId, ApiUrl = row.ApiUrl, DisplayName = "renamed" }
        });

        Probe().Asked.Count.ShouldBe(askedBefore);
        (await LoadAsync(instanceId)).DisplayName.ShouldBe("renamed");
        (await LoadAsync(instanceId)).BaseUrl.ShouldBe(stored, "never rewritten");
    }

    [Fact]
    public async Task A_repoint_refused_for_its_bound_token_is_refused_before_the_new_host_is_asked_about()
    {
        // The host an Admin names while a member's token is connected is never contacted at all — not even to ask.
        var (teamId, instanceId, stored) = await SeedInstanceAsync();
        await SeedCredentialAsync(teamId, instanceId);
        var attacker = UniqueAddress("https");

        await Should.ThrowAsync<ProviderEndpointCredentialsBoundException>(() => UpdateAsync(teamId, new UpdateProviderInstanceCommand { ProviderInstanceId = instanceId, BaseUrl = attacker }));

        Probe().Asked.ShouldNotContain(attacker);
        (await LoadAsync(instanceId)).BaseUrl.ShouldBe(stored);
    }

    [Fact]
    public async Task An_allowed_https_upgrade_is_itself_asked_about_so_a_redirect_from_the_new_address_is_named_too()
    {
        var (teamId, instanceId, _) = await SeedInstanceAsync(baseUrl: UniqueAddress("http"));
        await SeedCredentialAsync(teamId, instanceId);
        var row = await LoadAsync(instanceId);
        var upgraded = "https" + row.BaseUrl["http".Length..];
        Probe().RedirectsTo(upgraded, "https://elsewhere.acme.test/api/v4/version");

        // Same host, so the endpoint-binding rule lets it through; the probe is the one that sees where it leads.
        await Should.ThrowAsync<ProviderBaseUrlRedirectsException>(() => UpdateAsync(teamId, new UpdateProviderInstanceCommand { ProviderInstanceId = instanceId, BaseUrl = upgraded }));

        (await LoadAsync(instanceId)).BaseUrl.ShouldBe(row.BaseUrl);
    }

    private TestProviderEndpointProbe Probe()
    {
        using var scope = _fixture.BeginScope();

        return scope.Resolve<TestProviderEndpointProbe>();
    }

    /// <summary>An address of this test's own — the fake probe is one instance for the whole fixture, so tests key their answers by address.</summary>
    private static string UniqueAddress(string scheme) => $"{scheme}://gitlab-{Guid.NewGuid():N}.acme.test";

    private async Task<Guid> AddAsync(Guid teamId, AddProviderInstanceCommand command)
    {
        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);

        return await scope.Resolve<IMediator>().Send(command);
    }

    private async Task UpdateAsync(Guid teamId, UpdateProviderInstanceCommand command)
    {
        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);

        await scope.Resolve<IMediator>().Send(command);
    }

    private async Task<int> CountAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ProviderInstance.AsNoTracking().CountAsync(p => p.TeamId == teamId && p.DeletedDate == null);
    }

    private async Task<ProviderInstance> LoadAsync(Guid instanceId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ProviderInstance.AsNoTracking().SingleAsync(p => p.Id == instanceId);
    }

    private async Task<Guid> SeedTeamAsync()
    {
        var (teamId, instanceId, _) = await SeedInstanceAsync();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.ProviderInstance.Remove(await db.ProviderInstance.SingleAsync(p => p.Id == instanceId));
        await db.SaveChangesAsync();

        return teamId;
    }

    private async Task<(Guid TeamId, Guid InstanceId, string BaseUrl)> SeedInstanceAsync(string? baseUrl = null, string? apiUrl = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var owner = new User { Id = Guid.NewGuid(), Email = $"owner-{suffix}@x", Name = "Owner" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"team-{suffix}", Name = "Team" };
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team.Id, Provider = ProviderKind.GitLab, DisplayName = "GitLab", BaseUrl = baseUrl ?? UniqueAddress("https"), ApiUrl = apiUrl };

        db.User.Add(owner);
        db.Team.Add(team);
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = owner.Id, Role = TeamRole.Owner });
        db.Project.Add(TestProjectSeed.BuildDefaultProject(team.Id, owner.Id));
        db.ProviderInstance.Add(instance);
        await db.SaveChangesAsync();

        return (team.Id, instance.Id, instance.BaseUrl);
    }

    private async Task SeedCredentialAsync(Guid teamId, Guid instanceId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.Credential.Add(new Credential { Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instanceId, AuthType = AuthType.Pat, DisplayName = "member pat", EncryptedPayload = "unused-by-this-test", Status = CredentialStatus.Active });
        await db.SaveChangesAsync();
    }
}
