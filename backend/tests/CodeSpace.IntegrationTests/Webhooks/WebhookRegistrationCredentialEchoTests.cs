using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Webhooks.Registration;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using static CodeSpace.IntegrationTests.Webhooks.StubProviderHost;

namespace CodeSpace.IntegrationTests.Webhooks;

/// <summary>
/// A webhook registration the provider refuses while QUOTING the credential it was presented must not persist that
/// credential. The refusal lands in <c>repository_webhook.last_error</c>, <c>*_webhook_attempt.error</c> and
/// <c>*_webhook_attempt.response_body</c>, which <c>GET /api/repositories/{id}/webhooks</c> (and the connection coverage
/// read) return to any member of the team, a Viewer included.
///
/// <para>Driven through the real registrars and the real GitLab and GitHub providers — NGitLab, Octokit and the raw
/// group-hook calls — against a loopback provider that echoes the request's <c>PRIVATE-TOKEN</c> and
/// <c>Authorization</c> into every refusal. The fixture check that the token WAS presented is what makes the absence an
/// end-to-end claim rather than a claim about a provider that never echoes.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class WebhookRegistrationCredentialEchoTests
{
    private readonly PostgresFixture _fixture;

    public WebhookRegistrationCredentialEchoTests(PostgresFixture fixture) { _fixture = fixture; }

    public enum Refused { Create, List }

    [Theory]
    [InlineData(ProviderKind.GitLab, Refused.Create)]
    [InlineData(ProviderKind.GitLab, Refused.List)]
    [InlineData(ProviderKind.GitHub, Refused.Create)]
    public async Task A_refused_repository_hook_persists_no_echoed_token(ProviderKind kind, Refused refused)
    {
        var token = (kind == ProviderKind.GitLab ? "glpat-" : "ghp_") + "HOOKECHO" + Guid.NewGuid().ToString("N");
        var hooks = kind == ProviderKind.GitLab ? "/api/v4/projects/4242/hooks" : "/repositories/4242/hooks";
        using var provider = new StubProviderHost()
            .Answer("POST", hooks, request => Echo(403, request))
            .Answer("GET", hooks, request => refused == Refused.List || kind == ProviderKind.GitHub ? Echo(403, request) : new StubReply(200, "[]"))
            .Answer("GET", "/api/v4/projects/4242", 200, GitLabProjectJson)
            .Answer("GET", "/repositories/4242", 200, GitHubRepositoryJson);

        var webhookId = await SeedEnqueuedRepositoryWebhookAsync(kind, provider.BaseUrl, token);

        await RunAsync<IRepositoryWebhookRegistrar>(registrar => registrar.RunAsync(webhookId, CancellationToken.None));

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var hook = await db.RepositoryWebhook.AsNoTracking().SingleAsync(w => w.Id == webhookId);
        var attempt = await db.RepositoryWebhookAttempt.AsNoTracking().SingleAsync(a => a.RepositoryWebhookId == webhookId);

        PresentedTheToken(provider, token);
        hook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Failed, "fixture check: the provider refused the registration");
        hook.LastError.ShouldNotBeNullOrEmpty();
        hook.LastError.ShouldNotContain(token, Case.Sensitive, "last_error is returned to every member");
        attempt.Error.ShouldNotBeNullOrEmpty();
        attempt.Error.ShouldNotContain(token, Case.Sensitive, "the attempt's error is returned to every member");
        (attempt.ResponseBody ?? "").ShouldNotContain(token, Case.Sensitive, "the attempt's response body is returned to every member");
        (attempt.RequestHeadersJson ?? "").ShouldNotContain(token, Case.Sensitive);
        (attempt.ResponseBody ?? attempt.Error).ShouldContain("403", Case.Sensitive, "the provider's refusal is still the evidence");
    }

    [Fact]
    public async Task A_refused_group_hook_persists_no_echoed_token()
    {
        var token = "glpat-GROUPECHO" + Guid.NewGuid().ToString("N");
        using var provider = new StubProviderHost()
            .Answer("POST", "/api/v4/groups/acme%2Fplatform/hooks", request => Echo(403, request))
            .Answer("GET", "/api/v4/groups/acme%2Fplatform/hooks", 200, "[]");

        var hookId = await SeedEnqueuedGitLabGroupHookAsync(provider.BaseUrl, token);

        await RunAsync<IConnectionWebhookRegistrar>(registrar => registrar.RunAsync(hookId, CancellationToken.None));

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var hook = await db.ConnectionWebhook.AsNoTracking().SingleAsync(w => w.Id == hookId);
        var attempt = await db.ConnectionWebhookAttempt.AsNoTracking().SingleAsync(a => a.ConnectionWebhookId == hookId);

        PresentedTheToken(provider, token);
        hook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Failed, "fixture check: GitLab refused the group hook");
        (hook.LastError ?? "").ShouldNotContain(token, Case.Sensitive);
        (attempt.Error ?? "").ShouldNotContain(token, Case.Sensitive);
        attempt.StatusCode.ShouldBe(403);
        attempt.ResponseBody.ShouldNotBeNull().ShouldContain("403 refused", Case.Sensitive, "GitLab's own words are the evidence the attempt row exists to keep");
        attempt.ResponseBody.ShouldNotContain(token, Case.Sensitive, "the attempt's response body is returned to every member");
    }

    private static void PresentedTheToken(StubProviderHost provider, string token) =>
        provider.Requests.ShouldContain(request => (request.AuthorizationHeader ?? "").Contains(token) || (request.PrivateTokenHeader ?? "").Contains(token), "fixture check: the token was presented, so every refusal quoted it");

    private static StubReply Echo(int status, RecordedRequest request) => new(status, JsonSerializer.Serialize(new { message = $"{status} refused (PRIVATE-TOKEN={request.PrivateTokenHeader}; Authorization={request.AuthorizationHeader})" }));

    /// <summary>A registrar swallows the failure it records; anything it lets escape is not this test's subject, so the rows are what is read.</summary>
    private async Task RunAsync<TRegistrar>(Func<TRegistrar, Task> run) where TRegistrar : notnull
    {
        using var scope = _fixture.BeginScope();

        try { await run(scope.Resolve<TRegistrar>()); }
        catch (Exception) { /* recorded on the row before it escaped, if it did */ }
    }

    private async Task<Guid> SeedEnqueuedRepositoryWebhookAsync(ProviderKind kind, string baseUrl, string token)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{Guid.NewGuid().ToString("N")[..8]}", Name = "Team" };
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team.Id, Provider = kind, DisplayName = "echoing provider", BaseUrl = baseUrl, ApiUrl = baseUrl };
        var credential = new Credential { Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, Ownership = CredentialOwnership.TeamService, AuthType = AuthType.Pat, DisplayName = "connection", EncryptedPayload = encryptor.Encrypt(scope.Resolve<ICredentialPayloadSerializer>().Serialize(new PatPayload { Token = token })), Status = CredentialStatus.Active };
        var repository = new Repository { Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, CredentialId = credential.Id, ExternalId = "4242", NamespacePath = "acme", Name = "api", FullPath = "acme/api", DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = "https://forge.test/acme/api", Status = RepositoryStatus.Active };
        var webhookId = Guid.NewGuid();

        db.Team.Add(team);
        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        db.Repository.Add(repository);
        db.RepositoryWebhook.Add(new RepositoryWebhook { Id = webhookId, RepositoryId = repository.Id, CallbackUrl = $"https://codespace.test/api/webhooks/{webhookId}", SecretEnc = encryptor.Encrypt("whsec-echo-loopback"), SubscribedEvents = ["push"], RegistrationStatus = RepositoryWebhookRegistrationStatus.Enqueued, EnqueuedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        return webhookId;
    }

    private async Task<Guid> SeedEnqueuedGitLabGroupHookAsync(string baseUrl, string token)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{Guid.NewGuid().ToString("N")[..8]}", Name = "Team" };
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team.Id, Provider = ProviderKind.GitLab, DisplayName = "echoing gitlab", BaseUrl = baseUrl, ApiUrl = baseUrl, WebhookScope = ProviderWebhookScope.Connection };
        var credential = new Credential { Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, AuthType = AuthType.Pat, DisplayName = "connection", EncryptedPayload = encryptor.Encrypt(scope.Resolve<ICredentialPayloadSerializer>().Serialize(new PatPayload { Token = token })) };
        var hookId = Guid.NewGuid();

        db.Team.Add(team);
        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        db.ConnectionWebhook.Add(new ConnectionWebhook { Id = hookId, ProviderInstanceId = instance.Id, CredentialId = credential.Id, OwnerPath = "acme/platform", CallbackUrl = $"https://codespace.test/api/webhooks/connection/{hookId}", SecretEnc = encryptor.Encrypt("whsec-echo-loopback"), SubscribedEvents = ["Push Hook"], RegistrationStatus = RepositoryWebhookRegistrationStatus.Enqueued });
        await db.SaveChangesAsync();

        return hookId;
    }

    private const string GitLabProjectJson = """{"id":4242,"name":"api","path":"api","path_with_namespace":"acme/api","namespace":{"id":1,"full_path":"acme","path":"acme","name":"acme","kind":"group"},"default_branch":"main","visibility":"private","web_url":"https://forge.test/acme/api","http_url_to_repo":"https://forge.test/acme/api.git","ssh_url_to_repo":"git@forge.test:acme/api.git","archived":false}""";

    private const string GitHubRepositoryJson = """{"id":4242,"name":"api","full_name":"acme/api","owner":{"login":"acme"},"default_branch":"main","private":true,"html_url":"https://forge.test/acme/api","clone_url":"https://forge.test/acme/api.git","ssh_url":"git@forge.test:acme/api.git","archived":false}""";
}
