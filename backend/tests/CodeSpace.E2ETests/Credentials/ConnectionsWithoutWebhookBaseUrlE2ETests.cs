using System.Net;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Credentials;

/// <summary>
/// 🟢 High fidelity (real HTTP pipeline in the Production environment, Postgres): a host outside Development that never set
/// <c>Webhooks:BaseUrl</c>. The service that moves a repository between two connections to one host reads that setting when it is built, and it throws
/// when the key is unset — so connecting, disconnecting or listing a credential, and asking a dialog for its candidates, must not build that service.
/// They did once the successor service took it as a dependency, and every OAuth start answered 500
/// (<see cref="OAuthConnectWithoutPublicBaseUrlE2ETests"/> is the other face of this); it is taken lazily now, so only a move between connections
/// needs it, and this pins the credential endpoints that share the dependency. (Listing repositories already needs the key, through the connection-hook
/// coverage the repository service has always taken, and is not what this pins.)
///
/// <para>Diagnose a red here by starting the API with <c>ASPNETCORE_ENVIRONMENT=Production</c> and no <c>Webhooks__BaseUrl</c>: a 500 with
/// "Webhooks:BaseUrl is required outside Development" in the log means something on these paths builds the repository binding service again.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class ConnectionsWithoutWebhookBaseUrlE2ETests : IClassFixture<ProductionWithoutPublicBaseUrlGitWireApiFactory>
{
    private readonly ProductionWithoutPublicBaseUrlGitWireApiFactory _factory;
    private readonly TenantHarness _tenants;

    public ConnectionsWithoutWebhookBaseUrlE2ETests(ProductionWithoutPublicBaseUrlGitWireApiFactory factory)
    {
        _factory = factory;
        _tenants = new TenantHarness(factory);
    }

    [Fact]
    public async Task Listing_credentials_asking_for_candidates_and_disconnecting_work_on_a_host_with_no_webhook_base_url()
    {
        var admin = await _tenants.AccountAsync();
        var team = await _tenants.TeamAsync((admin, TeamRole.Owner));
        var (instance, leaving, staying) = await SeedAsync(team, admin);

        var listed = await _tenants.SendAsync(HttpMethod.Get, "/api/credentials", admin, team);
        var suggested = await _tenants.SendAsync(HttpMethod.Get, $"/api/credentials/{leaving}/relink-suggestions", admin, team);
        var candidates = await _tenants.SendAsync(HttpMethod.Get, $"/api/provider-instances/{instance}/relink-candidates?fromCredentialId={leaving}", admin, team);
        var revoked = await _tenants.SendAsync(HttpMethod.Post, $"/api/credentials/{leaving}/revoke", admin, team, new { moveBindingsToCredentialId = staying });

        listed.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(listed));
        suggested.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(suggested));
        candidates.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(candidates));
        revoked.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(revoked));
        JsonDocument.Parse(await listed.Content.ReadAsStringAsync()).RootElement.EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ShouldBe(new[] { leaving, staying }, ignoreOrder: true);
        JsonDocument.Parse(await revoked.Content.ReadAsStringAsync()).RootElement.GetProperty("movedRepositoryCount").GetInt32().ShouldBe(1, "a Disconnect that names a target within one connection moves the repository without a hook or a second connection in sight");
    }

    private async Task<(Guid Instance, Guid Leaving, Guid Staying)> SeedAsync(Guid team, Guid owner)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var encryptor = scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>();
        var serializer = scope.ServiceProvider.GetRequiredService<ICredentialPayloadSerializer>();

        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team, Provider = ProviderKind.Git, DisplayName = "Git", BaseUrl = $"https://git.stub.test/{Guid.NewGuid():N}", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };

        Credential Token(string name) => new() { Id = Guid.NewGuid(), TeamId = team, ProviderInstanceId = instance.Id, OwnerUserId = owner, Ownership = CredentialOwnership.Personal, AuthType = AuthType.Pat, DisplayName = name, EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = $"pat-{name}" })), Status = CredentialStatus.Active, CreatedBy = owner, LastModifiedBy = owner };

        var leaving = Token("leaving");
        var staying = Token("staying");
        var repository = new Repository { Id = Guid.NewGuid(), TeamId = team, ProviderInstanceId = instance.Id, CredentialId = leaving.Id, ExternalId = "acme/api", NamespacePath = "acme", Name = "api", FullPath = "acme/api", DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = "https://git.stub.test/acme/api", Status = RepositoryStatus.Active };

        db.ProviderInstance.Add(instance);
        db.Credential.AddRange(leaving, staying);
        db.Repository.Add(repository);
        await db.SaveChangesAsync();

        return (instance.Id, leaving.Id, staying.Id);
    }
}
