using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.ProviderInstances;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using CodeSpace.Messages.Queries.ProviderInstances;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.ProviderInstances;

/// <summary>
/// A git provider connection's secrets — the tokens members connected through it and its OAuth client secret — are bound
/// to the host they were entered for. An edit that moves the host while a token is connected is refused and changes
/// nothing; one that moves it past a stored client secret needs the secret again; anything that keeps the host moves
/// freely. The URLs the list shows are masked where they embed credentials, and sending a masked URL back keeps the
/// stored one — real credentials included — so the edit form and any client that PATCHes back what it GETs keep working.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ProviderInstanceEndpointBindingFlowTests
{
    private const string Host = "https://gitlab.acme.test";

    private readonly PostgresFixture _fixture;

    public ProviderInstanceEndpointBindingFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Theory]
    [InlineData("base", CredentialStatus.Active)]
    [InlineData("api", CredentialStatus.Active)]
    [InlineData("base", CredentialStatus.Expired)]
    [InlineData("api", CredentialStatus.Error)]
    public async Task A_host_move_is_refused_while_a_token_is_connected_and_changes_nothing(string field, CredentialStatus status)
    {
        var (teamId, instanceId) = await SeedInstanceAsync(Host, apiUrl: null, withOAuth: false);
        await SeedCredentialAsync(teamId, instanceId, status);

        var refusal = await Should.ThrowAsync<ProviderEndpointCredentialsBoundException>(() => UpdateAsync(teamId, new UpdateProviderInstanceCommand { ProviderInstanceId = instanceId, DisplayName = "renamed", BaseUrl = field == "base" ? "https://attacker.test" : null, ApiUrl = field == "api" ? "https://attacker.test" : null }));

        refusal.BoundCredentialCount.ShouldBe(1, "an Expired or Error token is still decrypted and presented, so it binds the host too");
        var row = await LoadAsync(instanceId);
        row.BaseUrl.ShouldBe(Host);
        row.ApiUrl.ShouldBeNull();
        row.DisplayName.ShouldBe("GitLab", "a refused edit changes nothing, the rest of it included");
    }

    [Theory]
    [InlineData("base", "http://gitlab.acme.test", "https://gitlab.acme.test")]
    [InlineData("base", "http://gitlab.acme.test:8080", "https://gitlab.acme.test:8080")]
    [InlineData("api", "http://gitlab.acme.test", "https://gitlab.acme.test")]
    public async Task An_upgrade_of_http_to_https_on_the_same_host_is_allowed_while_a_token_is_connected(string field, string from, string to)
    {
        // The self-service fix for a base URL that redirects to https. Refused, it left the owner one way out — disconnecting
        // every member's token, which strands every repository bound to one of them.
        var (teamId, instanceId) = await SeedInstanceAsync(field == "base" ? from : Host, apiUrl: field == "api" ? from : null, withOAuth: true);
        await SeedCredentialAsync(teamId, instanceId, CredentialStatus.Active);

        await UpdateAsync(teamId, new UpdateProviderInstanceCommand { ProviderInstanceId = instanceId, BaseUrl = field == "base" ? to : null, ApiUrl = field == "api" ? to : null, OauthClientSecret = "" });

        var row = await LoadAsync(instanceId);
        (field == "base" ? row.BaseUrl : row.ApiUrl).ShouldBe(to);
        using var verify = _fixture.BeginScope();
        verify.Resolve<IPayloadEncryptor>().Decrypt(row.OauthClientSecretEnc!).ShouldBe("original-secret", "an upgrade of the same host needs no new client secret and replaces none");
    }

    [Theory]
    [InlineData("base")]
    [InlineData("api")]
    public async Task An_upgrade_past_a_connected_token_is_saved_only_where_the_new_address_answers(string field)
    {
        // The carve-out sends a token nowhere new, but it can still land it on an address nothing serves over TLS — a host
        // that does not do https, a certificate this server does not trust — and undoing that is itself an endpoint move,
        // refused while the token is connected. So the new address must answer HTTP before the edit is kept.
        var host = $"gitlab-{Guid.NewGuid():N}.acme.test";
        var (teamId, instanceId) = await SeedInstanceAsync(field == "base" ? $"http://{host}" : Host, apiUrl: field == "api" ? $"http://{host}" : null, withOAuth: false);
        await SeedCredentialAsync(teamId, instanceId, CredentialStatus.Active);
        Probe().NeverAnswers($"https://{host}");

        var refusal = await Should.ThrowAsync<ProviderEndpointUnreachableException>(() => UpdateAsync(teamId, new UpdateProviderInstanceCommand { ProviderInstanceId = instanceId, BaseUrl = field == "base" ? $"https://{host}" : null, ApiUrl = field == "api" ? $"https://{host}" : null }));

        refusal.Origin.ShouldBe($"https://{host}");
        var row = await LoadAsync(instanceId);
        (field == "base" ? row.BaseUrl : row.ApiUrl).ShouldBe($"http://{host}", "a refused edit changes nothing: the owner is never stranded on an address nothing answers at");
    }

    [Fact]
    public async Task An_upgrade_past_a_connected_token_asks_whether_the_new_address_answers_and_is_kept_when_it_does()
    {
        var host = $"gitlab-{Guid.NewGuid():N}.acme.test";
        var (teamId, instanceId) = await SeedInstanceAsync($"http://{host}", apiUrl: null, withOAuth: false);
        await SeedCredentialAsync(teamId, instanceId, CredentialStatus.Active);

        await UpdateAsync(teamId, new UpdateProviderInstanceCommand { ProviderInstanceId = instanceId, BaseUrl = $"https://{host}" });

        Probe().AskedToAnswer.ShouldContain($"https://{host}");
        (await LoadAsync(instanceId)).BaseUrl.ShouldBe($"https://{host}");
    }

    [Fact]
    public async Task An_upgrade_with_no_token_connected_is_not_asked_whether_it_answers_it_can_be_undone_freely()
    {
        var host = $"gitlab-{Guid.NewGuid():N}.acme.test";
        var (teamId, instanceId) = await SeedInstanceAsync($"http://{host}", apiUrl: null, withOAuth: false);
        Probe().NeverAnswers($"https://{host}");

        await UpdateAsync(teamId, new UpdateProviderInstanceCommand { ProviderInstanceId = instanceId, BaseUrl = $"https://{host}" });

        Probe().AskedToAnswer.ShouldNotContain($"https://{host}", "with nothing bound a mistaken address is one more edit away, so nothing is asked");
        (await LoadAsync(instanceId)).BaseUrl.ShouldBe($"https://{host}");
    }

    [Fact]
    public async Task An_edit_that_is_not_an_upgrade_is_not_asked_whether_it_answers()
    {
        var host = $"gitlab-{Guid.NewGuid():N}.acme.test";
        var (teamId, instanceId) = await SeedInstanceAsync($"https://{host}", apiUrl: null, withOAuth: false);
        await SeedCredentialAsync(teamId, instanceId, CredentialStatus.Active);

        await UpdateAsync(teamId, new UpdateProviderInstanceCommand { ProviderInstanceId = instanceId, DisplayName = "renamed", BaseUrl = $"https://{host}/gitlab" });

        Probe().AskedToAnswer.ShouldNotContain(a => a.Contains(host), "a rename, or a path on the same origin, relies on no carve-out");
    }

    [Theory]
    [InlineData("http://gitlab.acme.test", "https://gitlab.acme.test:8443")]      // another port
    [InlineData("http://gitlab.acme.test:8080", "https://gitlab.acme.test")]
    [InlineData("http://gitlab.acme.test", "https://gitlab-alias.acme.test")]     // an alias host
    [InlineData("https://gitlab.acme.test", "http://gitlab.acme.test")]           // a downgrade
    public async Task An_upgrade_that_changes_anything_else_is_still_refused_while_a_token_is_connected(string from, string to)
    {
        var (teamId, instanceId) = await SeedInstanceAsync(from, apiUrl: null, withOAuth: false);
        await SeedCredentialAsync(teamId, instanceId, CredentialStatus.Active);

        await Should.ThrowAsync<ProviderEndpointCredentialsBoundException>(() => UpdateAsync(teamId, new UpdateProviderInstanceCommand { ProviderInstanceId = instanceId, BaseUrl = to }));

        (await LoadAsync(instanceId)).BaseUrl.ShouldBe(from);
    }

    [Fact]
    public async Task A_disconnected_token_does_not_bind_the_host()
    {
        var (teamId, instanceId) = await SeedInstanceAsync(Host, apiUrl: null, withOAuth: false);
        await SeedCredentialAsync(teamId, instanceId, CredentialStatus.Revoked);

        await UpdateAsync(teamId, new UpdateProviderInstanceCommand { ProviderInstanceId = instanceId, BaseUrl = "https://gitlab.new.test" });

        (await LoadAsync(instanceId)).BaseUrl.ShouldBe("https://gitlab.new.test", "a disconnect revoked the row and blanked its token: nothing would follow the move");
    }

    [Fact]
    public async Task A_path_only_edit_moves_nothing_and_is_kept_with_a_token_connected()
    {
        var (teamId, instanceId) = await SeedInstanceAsync(Host, apiUrl: null, withOAuth: true);
        await SeedCredentialAsync(teamId, instanceId, CredentialStatus.Active);

        await UpdateAsync(teamId, new UpdateProviderInstanceCommand { ProviderInstanceId = instanceId, BaseUrl = Host + "/gitlab/", ApiUrl = Host + "/gitlab/api" });

        var row = await LoadAsync(instanceId);
        row.BaseUrl.ShouldBe(Host + "/gitlab");
        row.ApiUrl.ShouldBe(Host + "/gitlab/api");
    }

    [Fact]
    public async Task A_host_move_past_a_stored_client_secret_needs_the_secret_again()
    {
        var (teamId, instanceId) = await SeedInstanceAsync(Host, apiUrl: null, withOAuth: true);

        await Should.ThrowAsync<ProviderEndpointSecretRequiredException>(() => UpdateAsync(teamId, new UpdateProviderInstanceCommand { ProviderInstanceId = instanceId, BaseUrl = "https://gitlab.new.test", OauthClientSecret = "" }));

        (await LoadAsync(instanceId)).BaseUrl.ShouldBe(Host);

        await UpdateAsync(teamId, new UpdateProviderInstanceCommand { ProviderInstanceId = instanceId, BaseUrl = "https://gitlab.new.test", OauthClientId = "new-host-app", OauthClientSecret = "new-host-secret" });

        using var verify = _fixture.BeginScope();
        var row = await verify.Resolve<CodeSpaceDbContext>().ProviderInstance.AsNoTracking().SingleAsync(p => p.Id == instanceId);
        row.BaseUrl.ShouldBe("https://gitlab.new.test");
        verify.Resolve<IPayloadEncryptor>().Decrypt(row.OauthClientSecretEnc!).ShouldBe("new-host-secret");
    }

    [Fact]
    public async Task The_list_masks_embedded_credentials_and_sending_them_back_keeps_the_stored_urls()
    {
        var canary = "CANARY" + Guid.NewGuid().ToString("N")[..12];
        var baseUrl = $"https://svc:{canary}@gitlab.acme.test";
        var apiUrl = $"https://gitlab.acme.test/api?private_token={canary}";
        var webUrl = $"https://viewer:{canary}@gitlab.acme.test";
        var (teamId, instanceId) = await SeedInstanceAsync(baseUrl, apiUrl, withOAuth: false, webUrl);
        await SeedCredentialAsync(teamId, instanceId, CredentialStatus.Active);

        var listed = (await ListAsync(teamId)).Single(p => p.Id == instanceId);

        listed.BaseUrl.ShouldBe("https://***:***@gitlab.acme.test");
        listed.ApiUrl.ShouldBe("https://gitlab.acme.test/api?private_token=***");
        listed.WebUrl.ShouldBe("https://***:***@gitlab.acme.test");

        await UpdateAsync(teamId, new UpdateProviderInstanceCommand { ProviderInstanceId = instanceId, DisplayName = "renamed", BaseUrl = listed.BaseUrl, ApiUrl = listed.ApiUrl, WebUrl = listed.WebUrl });

        var row = await LoadAsync(instanceId);
        row.DisplayName.ShouldBe("renamed");
        row.BaseUrl.ShouldBe(baseUrl, "the masked form sent back means unchanged — the real credentials are kept");
        row.ApiUrl.ShouldBe(apiUrl);
        row.WebUrl.ShouldBe(webUrl);
    }

    [Fact]
    public async Task A_placeholder_left_in_an_edited_url_is_refused_rather_than_stored_as_the_credential()
    {
        var baseUrl = "https://svc:CANARY-PW-0123@gitlab.acme.test";
        var (teamId, instanceId) = await SeedInstanceAsync(baseUrl, apiUrl: null, withOAuth: false);

        var refusal = await Should.ThrowAsync<ArgumentException>(() => UpdateAsync(teamId, new UpdateProviderInstanceCommand { ProviderInstanceId = instanceId, BaseUrl = "https://***:***@gitlab.acme.test/gitlab" }));

        refusal.Message.ShouldNotContain("CANARY");
        (await LoadAsync(instanceId)).BaseUrl.ShouldBe(baseUrl);
    }

    private TestProviderEndpointProbe Probe()
    {
        using var scope = _fixture.BeginScope();

        return scope.Resolve<TestProviderEndpointProbe>();
    }

    private async Task UpdateAsync(Guid teamId, UpdateProviderInstanceCommand command)
    {
        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);

        await scope.Resolve<IMediator>().Send(command);
    }

    private async Task<IReadOnlyList<CodeSpace.Messages.Dtos.ProviderInstances.ProviderInstanceSummary>> ListAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);

        return await scope.Resolve<IMediator>().Send(new ListProviderInstancesQuery());
    }

    private async Task<ProviderInstance> LoadAsync(Guid instanceId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ProviderInstance.AsNoTracking().SingleAsync(p => p.Id == instanceId);
    }

    private async Task<(Guid TeamId, Guid InstanceId)> SeedInstanceAsync(string baseUrl, string? apiUrl, bool withOAuth, string? webUrl = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var owner = new User { Id = Guid.NewGuid(), Email = $"owner-{suffix}@x", Name = "Owner" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"team-{suffix}", Name = "Team" };
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team.Id, Provider = ProviderKind.GitLab, DisplayName = "GitLab", BaseUrl = baseUrl, ApiUrl = apiUrl, WebUrl = webUrl, OauthClientId = withOAuth ? "client-id" : null, OauthClientSecretEnc = withOAuth ? encryptor.Encrypt("original-secret") : null };

        db.User.Add(owner);
        db.Team.Add(team);
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = owner.Id, Role = TeamRole.Owner });
        db.Project.Add(TestProjectSeed.BuildDefaultProject(team.Id, owner.Id));
        db.ProviderInstance.Add(instance);
        await db.SaveChangesAsync();

        return (team.Id, instance.Id);
    }

    private async Task SeedCredentialAsync(Guid teamId, Guid instanceId, CredentialStatus status)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var payload = status == CredentialStatus.Revoked ? "" : scope.Resolve<IPayloadEncryptor>().Encrypt(scope.Resolve<ICredentialPayloadSerializer>().Serialize(new PatPayload { Token = "glpat-BOUND-" + Guid.NewGuid().ToString("N") }));

        db.Credential.Add(new Credential { Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instanceId, AuthType = AuthType.Pat, DisplayName = "member pat", EncryptedPayload = payload, Status = status });
        await db.SaveChangesAsync();
    }
}
