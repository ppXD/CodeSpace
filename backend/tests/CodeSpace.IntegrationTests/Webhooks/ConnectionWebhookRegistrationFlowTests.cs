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

namespace CodeSpace.IntegrationTests.Webhooks;

/// <summary>
/// The connection-scoped registration path, once per provider, against a real HTTP server standing
/// in for the provider's API. The registrar, the state machine, the provider class, NGitLab /
/// Octokit, and the wire are all real; only what answers is stubbed. That is the tier this belongs
/// in: the endpoint a group hook is created on, and the plan refusal a Free GitLab answers with,
/// are facts about the wire that no double can establish.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ConnectionWebhookRegistrationFlowTests
{
    private readonly PostgresFixture _fixture;

    public ConnectionWebhookRegistrationFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task GitLab_registers_the_hook_on_the_group_and_reaches_Registered()
    {
        using var provider = new StubProviderHost()
            .Answer("GET", "/api/v4/groups/", 200, "[]")
            .Answer("POST", "/api/v4/groups/", 201, """{"id":4242,"url":"https://codespace.test/api/webhooks/connection/x"}""");

        var hookId = await SeedConnectionWebhookAsync(ProviderKind.GitLab, provider.BaseUrl, "acme/platform").ConfigureAwait(false);

        await RunRegistrarAsync(hookId).ConfigureAwait(false);

        var hook = await LoadHookAsync(hookId).ConfigureAwait(false);
        hook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Registered,
            customMessage: $"Group-hook registration should have completed. last_error={hook.LastError}");
        hook.ExternalId.ShouldBe("4242",
            customMessage: "The provider-assigned group-hook id must be written atomically with the Registered transition.");

        // The endpoint is the claim worth pinning: a group hook that lands on the project endpoint
        // would register successfully and cover one repository instead of the whole group.
        var created = provider.Requests.Single(r => r.Method == "POST");
        created.PathAndQuery.ShouldContain("/api/v4/groups/acme%2Fplatform/hooks",
            customMessage: "GitLab addresses a nested group by its URL-encoded full path; a raw slash resolves to a different route.");
    }

    [Fact]
    public async Task GitLab_Free_refusal_names_the_plan_and_keeps_the_provider_answer()
    {
        // A Free instance refuses the Premium group-hooks endpoint. Both halves of the answer matter:
        // last_error has to say what the operator must DO, and the attempt row has to carry what
        // GitLab itself said, because "403" alone reads like a token problem and would send them to
        // re-scope a credential that was never wrong.
        const string gitLabSaid = """{"message":"403 Forbidden - Group hooks are available in GitLab Premium"}""";

        using var provider = new StubProviderHost().Answer("GET", "/api/v4/groups/", 403, gitLabSaid);

        var hookId = await SeedConnectionWebhookAsync(ProviderKind.GitLab, provider.BaseUrl, "acme/platform").ConfigureAwait(false);

        await RunRegistrarAsync(hookId).ConfigureAwait(false);

        var hook = await LoadHookAsync(hookId).ConfigureAwait(false);
        hook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Failed);
        // Asserted on OUR words, not the word "Premium" — GitLab's own body says that too, so a
        // looser assertion would pass on a build that just echoed the provider back and told the
        // operator nothing about what to do.
        hook.LastError.ShouldContain("require the Premium plan",
            customMessage: "A Free instance's refusal must state the plan requirement in our own words — a bare 403 sends the operator to re-scope a working token.");
        hook.LastError.ShouldContain("per-repository webhook scope",
            customMessage: "The refusal has to name the way out that does not cost money, or the only remedy an operator sees is 'pay GitLab'.");

        var attempt = await LoadLatestAttemptAsync(hookId).ConfigureAwait(false);
        attempt.StatusCode.ShouldBe(403);
        attempt.ResponseBody.ShouldContain("Group hooks are available in GitLab Premium",
            customMessage: "The attempt timeline must carry GitLab's own words, not only our paraphrase of them.");
        attempt.RequestUrl.ShouldContain("/api/v4/groups/acme%2Fplatform/hooks");
    }

    [Fact]
    public async Task GitHub_registers_the_hook_on_the_organization_and_reaches_Registered()
    {
        using var provider = new StubProviderHost()
            .Answer("GET", "/orgs/acme/hooks", 200, "[]")
            .Answer("POST", "/orgs/acme/hooks", 201, """{"id":9182,"name":"web","active":true,"events":["push"],"config":{"url":"https://codespace.test/api/webhooks/connection/x"}}""");

        var hookId = await SeedConnectionWebhookAsync(ProviderKind.GitHub, provider.BaseUrl, "acme").ConfigureAwait(false);

        await RunRegistrarAsync(hookId).ConfigureAwait(false);

        var hook = await LoadHookAsync(hookId).ConfigureAwait(false);
        hook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Registered,
            customMessage: $"Organization-hook registration should have completed. last_error={hook.LastError}");
        hook.ExternalId.ShouldBe("9182");

        provider.Requests.Single(r => r.Method == "POST").PathAndQuery.ShouldContain("/orgs/acme/hooks",
            customMessage: "An organization hook lives at /orgs/:org/hooks; the repository endpoint would cover one repo instead of the org.");
    }

    [Fact]
    public async Task An_already_registered_remote_hook_is_reused_rather_than_duplicated()
    {
        // A retry, or a re-dispatch after the worker died between the provider call and the DB write,
        // must not leave a second hook on the group — a group with two of our hooks delivers every
        // event twice, which is the failure connection-wide scope exists to avoid.
        var hookId = Guid.NewGuid();
        var callbackUrl = $"https://codespace.test/api/webhooks/connection/{hookId}";

        var existingHookJson = "[{\"id\":777,\"name\":\"web\",\"active\":true,\"events\":[\"push\"],\"config\":{\"url\":\"" + callbackUrl + "\"}}]";

        using var provider = new StubProviderHost()
            .Answer("GET", "/orgs/acme/hooks", 200, existingHookJson)
            .Answer("POST", "/orgs/acme/hooks", 500, """{"message":"registration must not have been attempted"}""");

        await SeedConnectionWebhookAsync(ProviderKind.GitHub, provider.BaseUrl, "acme", hookId).ConfigureAwait(false);

        await RunRegistrarAsync(hookId).ConfigureAwait(false);

        var hook = await LoadHookAsync(hookId).ConfigureAwait(false);
        hook.ExternalId.ShouldBe("777",
            customMessage: "The existing remote hook's id should have been adopted.");
        provider.Requests.ShouldNotContain(r => r.Method == "POST",
            customMessage: "Finding a hook at our callback URL must skip creation entirely.");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Drive the real registrar through Enqueued → Registering → terminal, as the dispatcher would.</summary>
    private async Task RunRegistrarAsync(Guid hookId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        await db.ConnectionWebhook
            .Where(w => w.Id == hookId)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.RegistrationStatus, RepositoryWebhookRegistrationStatus.Enqueued))
            .ConfigureAwait(false);

        await scope.Resolve<IConnectionWebhookRegistrar>().RunAsync(hookId, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<ConnectionWebhook> LoadHookAsync(Guid hookId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ConnectionWebhook.AsNoTracking().SingleAsync(w => w.Id == hookId).ConfigureAwait(false);
    }

    private async Task<ConnectionWebhookAttempt> LoadLatestAttemptAsync(Guid hookId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ConnectionWebhookAttempt.AsNoTracking()
            .Where(a => a.ConnectionWebhookId == hookId)
            .OrderByDescending(a => a.AttemptNumber)
            .FirstAsync().ConfigureAwait(false);
    }

    private async Task<Guid> SeedConnectionWebhookAsync(ProviderKind provider, string baseUrl, string ownerPath, Guid? hookId = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var owner = new User { Id = Guid.NewGuid(), Email = $"owner-{suffix}@x", Name = "Owner" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"team-{suffix}", Name = "Team" };
        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            Provider = provider,
            DisplayName = "Connection",
            BaseUrl = baseUrl,
            ApiUrl = baseUrl,
            WebhookScope = ProviderWebhookScope.Connection
        };

        var credential = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            ProviderInstanceId = instance.Id,
            AuthType = AuthType.Pat,
            DisplayName = "PAT",
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = "pat-xxx" }))
        };

        var id = hookId ?? Guid.NewGuid();
        db.User.Add(owner);
        db.Team.Add(team);
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = owner.Id, Role = TeamRole.Owner });
        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        db.ConnectionWebhook.Add(new ConnectionWebhook
        {
            Id = id,
            ProviderInstanceId = instance.Id,
            CredentialId = credential.Id,
            OwnerPath = ownerPath,
            CallbackUrl = $"https://codespace.test/api/webhooks/connection/{id}",
            SecretEnc = encryptor.Encrypt("connection-secret"),
            SubscribedEvents = new List<string> { "push" },
            RegistrationStatus = RepositoryWebhookRegistrationStatus.Pending
        });

        await db.SaveChangesAsync().ConfigureAwait(false);

        return id;
    }
}
