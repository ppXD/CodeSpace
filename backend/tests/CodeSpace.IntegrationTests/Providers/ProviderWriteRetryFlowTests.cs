using System.Text.Json;
using System.Web;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Core.Services.Webhooks.Registration;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Webhooks;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using static CodeSpace.IntegrationTests.Webhooks.StubProviderHost;

namespace CodeSpace.IntegrationTests.Providers;

/// <summary>
/// A pull-request comment through the whole stack — <see cref="IPullRequestService"/>, the provider registry, the
/// real GitHub provider, the container's singleton resilience wrapper, Octokit, the wire — against a loopback
/// GitHub that saves the comment and then answers 502, the way a gateway does when the backend committed but the
/// answer did not make it back. The retry has to adopt the saved comment: one POST, one comment on the pull
/// request, and that comment is what the service returns.
///
/// <para>Webhook registration the same way, through the real registrars and the database: a hook the provider
/// created before its answer was lost is adopted, so the row reaches Registered with that hook's id — never
/// Failed on GitHub's 422 for a second hook at the URL, never pointing at a second hook GitLab took.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ProviderWriteRetryFlowTests
{
    private readonly PostgresFixture _fixture;

    public ProviderWriteRetryFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_comment_GitHub_saved_before_answering_502_is_adopted_not_posted_again()
    {
        var saved = new List<string>();

        using var github = new StubProviderHost()
            .Answer("POST", "/repos/acme/api/issues/7/comments", request => SaveThenFailTheFirst(saved, request))
            .Answer("GET", "/repos/acme/api/issues/7/comments", _ => new StubReply(200, "[" + string.Join(",", saved.Select((body, i) => CommentJson(i + 1, body))) + "]"));

        var seed = await SeedGitHubRepositoryAsync(github.BaseUrl);

        using var scope = _fixture.BeginScope();
        var comment = await scope.Resolve<IPullRequestService>().PostCommentAsync(seed.RepositoryId, seed.TeamId, 7, "Looks good.", CancellationToken.None);

        saved.ShouldHaveSingleItem("GitHub saved the comment before the 502 — posting it again would show it twice on the pull request").ShouldStartWith("Looks good.\n\n<!-- codespace:idempotency:");
        comment.ExternalId.ShouldBe("1", customMessage: "the service must hand back the comment that landed, found by its marker");
        comment.Body.ShouldBe("Looks good.", customMessage: "CodeSpace shows what was written; the marker lives only in GitHub's copy");
        github.Requests.Count(r => r.Method == "POST").ShouldBe(1, customMessage: "after an ambiguous failure the provider probes; it must not re-send a create the probe found");
    }

    [Fact]
    public async Task A_repository_hook_GitHub_created_before_answering_502_is_adopted_and_the_registration_completes()
    {
        var hooks = new List<string>();

        using var github = new StubProviderHost()
            .Answer("POST", "/repositories/4242/hooks", request => SaveGitHubHookThenFailTheFirst(hooks, request))
            .Answer("GET", "/repositories/4242/hooks", _ => new StubReply(200, "[" + string.Join(",", hooks.Select((url, i) => GitHubHookJson(i + 1, url))) + "]"))
            .Answer("GET", "/repositories/4242", 200, GitHubRepositoryJson);

        var seed = await SeedGitHubRepositoryAsync(github.BaseUrl);
        var webhookId = await SeedEnqueuedRepositoryWebhookAsync(seed.RepositoryId);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IRepositoryWebhookRegistrar>().RunAsync(webhookId, CancellationToken.None);

        var webhook = await LoadRepositoryWebhookAsync(webhookId);
        webhook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Registered, customMessage: $"GitHub holds the hook — a registration that succeeded must not back off as failed. last_error={webhook.LastError}");
        webhook.ExternalId.ShouldBe("1", customMessage: "the row must point at the hook GitHub created, found by its callback URL");
        hooks.ShouldHaveSingleItem();
        github.Requests.Count(r => r.Method == "POST").ShouldBe(1, customMessage: "after the 502 the provider probes; it must not re-send a create the probe found");
    }

    [Fact]
    public async Task A_group_hook_GitLab_created_before_the_connection_dropped_is_adopted_not_created_again()
    {
        var hooks = new List<string>();

        using var gitlab = new StubProviderHost()
            .Answer("POST", "/api/v4/groups/acme%2Fplatform/hooks", request => SaveGitLabHookThenDropTheFirst(hooks, request))
            .Answer("GET", "/api/v4/groups/acme%2Fplatform/hooks", _ => new StubReply(200, "[" + string.Join(",", hooks.Select((url, i) => GitLabHookJson(i + 1, url))) + "]"));

        var hookId = await SeedEnqueuedGitLabGroupHookAsync(gitlab.BaseUrl);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IConnectionWebhookRegistrar>().RunAsync(hookId, CancellationToken.None);

        var hook = await LoadConnectionWebhookAsync(hookId);
        hook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Registered, customMessage: $"last_error={hook.LastError}");
        hooks.ShouldHaveSingleItem("GitLab takes a second hook at one URL — re-sending a create that landed leaves the group delivering every event twice");
        hook.ExternalId.ShouldBe("1", customMessage: "the row must point at the hook that landed, not at a second one");
        gitlab.Requests.Count(r => r.Method == "POST").ShouldBe(1);
    }

    [Fact]
    public async Task A_group_hook_past_the_first_page_of_GitLab_s_list_is_reused_not_created_again()
    {
        // An earlier run created this registration's hook and died before recording it, and the group holds more hooks than
        // GitLab puts on a page. The registrar's idempotency check must read past the first page, or it creates a second.
        var hooks = Enumerable.Range(1, 120).Select(i => $"https://codespace.test/api/webhooks/other-registration-{i}").ToList();

        using var gitlab = new StubProviderHost()
            .Answer("POST", "/api/v4/groups/acme%2Fplatform/hooks", 500, """{"message":"registration must not have been attempted"}""")
            .Answer("GET", "/api/v4/groups/acme%2Fplatform/hooks", request => GitLabHooksPage(hooks, request));

        var hookId = await SeedEnqueuedGitLabGroupHookAsync(gitlab.BaseUrl);
        hooks.Add($"https://codespace.test/api/webhooks/connection/{hookId}");

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IConnectionWebhookRegistrar>().RunAsync(hookId, CancellationToken.None);

        var hook = await LoadConnectionWebhookAsync(hookId);
        hook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Registered, customMessage: $"last_error={hook.LastError}");
        hook.ExternalId.ShouldBe(hooks.Count.ToString(), customMessage: "the hook the earlier run created is on GitLab's second page; it must be adopted");
        gitlab.Requests.ShouldNotContain(r => r.Method == "POST", "finding the hook at this registration's callback URL must skip creation");
    }

    /// <summary>GitHub's hooks endpoint: saves the hook, then loses the first answer to a 502. A second hook at one URL is refused with 422, as GitHub refuses it.</summary>
    private static StubReply SaveGitHubHookThenFailTheFirst(List<string> hooks, RecordedRequest request)
    {
        var url = JsonDocument.Parse(request.Body).RootElement.GetProperty("config").GetProperty("url").GetString()!;

        if (hooks.Contains(url, StringComparer.OrdinalIgnoreCase)) return new StubReply(422, """{"message":"Validation Failed","errors":[{"resource":"Hook","code":"custom","message":"Hook already exists on this repository"}]}""");

        hooks.Add(url);

        return hooks.Count == 1 ? new StubReply(502, """{"message":"Bad Gateway"}""") : new StubReply(201, GitHubHookJson(hooks.Count, url));
    }

    /// <summary>GitLab's group hooks endpoint: saves every hook it is sent — a second at one URL too, as GitLab does — and drops the connection on the first answer.</summary>
    private static StubReply SaveGitLabHookThenDropTheFirst(List<string> hooks, RecordedRequest request)
    {
        hooks.Add(JsonDocument.Parse(request.Body).RootElement.GetProperty("url").GetString()!);

        return hooks.Count == 1 ? StubReply.DropConnection : new StubReply(201, GitLabHookJson(hooks.Count, hooks[^1]));
    }

    /// <summary>GitLab's paging: <c>per_page</c> hooks (20 unless asked for more) from <c>page</c>, and <c>X-Next-Page</c> naming the next page, empty on the last.</summary>
    private static StubReply GitLabHooksPage(List<string> hooks, RecordedRequest request)
    {
        var query = HttpUtility.ParseQueryString(new Uri(new Uri("http://stub"), request.PathAndQuery).Query);
        var perPage = int.Parse(query["per_page"] ?? "20");
        var page = int.Parse(query["page"] ?? "1");
        var slice = hooks.Skip((page - 1) * perPage).Take(perPage).Select((url, i) => GitLabHookJson((page - 1) * perPage + i + 1, url));
        var nextPage = hooks.Count > page * perPage ? (page + 1).ToString() : string.Empty;

        return new StubReply(200, "[" + string.Join(",", slice) + "]") { Headers = new Dictionary<string, string> { ["X-Next-Page"] = nextPage } };
    }

    private static string GitHubHookJson(long id, string url) => JsonSerializer.Serialize(new { id, name = "web", active = true, events = new[] { "*" }, config = new { url, content_type = "json" } });

    private static string GitLabHookJson(long id, string url) => JsonSerializer.Serialize(new { id, url, push_events = true, merge_requests_events = true, issues_events = true });

    private const string GitHubRepositoryJson = """{"id":4242,"name":"api","full_name":"acme/api","owner":{"login":"acme"},"default_branch":"main","private":true,"html_url":"https://github.test/acme/api","clone_url":"https://github.test/acme/api.git","ssh_url":"git@github.test:acme/api.git","archived":false}""";

    private async Task<Guid> SeedEnqueuedRepositoryWebhookAsync(Guid repositoryId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();

        db.RepositoryWebhook.Add(new RepositoryWebhook
        {
            Id = id, RepositoryId = repositoryId, CallbackUrl = $"https://codespace.test/api/webhooks/{id}", SecretEnc = scope.Resolve<IPayloadEncryptor>().Encrypt("whsec-loopback"),
            SubscribedEvents = new List<string> { "push" }, RegistrationStatus = RepositoryWebhookRegistrationStatus.Enqueued, EnqueuedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync().ConfigureAwait(false);

        return id;
    }

    private async Task<Guid> SeedEnqueuedGitLabGroupHookAsync(string baseUrl)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{suffix}", Name = "Team" };
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team.Id, Provider = ProviderKind.GitLab, DisplayName = "loopback", BaseUrl = baseUrl, ApiUrl = baseUrl, WebhookScope = ProviderWebhookScope.Connection };
        var credential = new Credential
        {
            Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, AuthType = AuthType.Pat, DisplayName = "connection",
            EncryptedPayload = encryptor.Encrypt(scope.Resolve<ICredentialPayloadSerializer>().Serialize(new PatPayload { Token = "glpat-loopback" }))
        };
        var id = Guid.NewGuid();

        db.Team.Add(team);
        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        db.ConnectionWebhook.Add(new ConnectionWebhook
        {
            Id = id, ProviderInstanceId = instance.Id, CredentialId = credential.Id, OwnerPath = "acme/platform", CallbackUrl = $"https://codespace.test/api/webhooks/connection/{id}",
            SecretEnc = encryptor.Encrypt("whsec-loopback"), SubscribedEvents = new List<string> { "Push Hook" }, RegistrationStatus = RepositoryWebhookRegistrationStatus.Enqueued
        });

        await db.SaveChangesAsync().ConfigureAwait(false);

        return id;
    }

    private async Task<RepositoryWebhook> LoadRepositoryWebhookAsync(Guid id)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().RepositoryWebhook.AsNoTracking().SingleAsync(w => w.Id == id).ConfigureAwait(false);
    }

    private async Task<ConnectionWebhook> LoadConnectionWebhookAsync(Guid id)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ConnectionWebhook.AsNoTracking().SingleAsync(w => w.Id == id).ConfigureAwait(false);
    }

    private static StubReply SaveThenFailTheFirst(List<string> saved, RecordedRequest request)
    {
        saved.Add(JsonDocument.Parse(request.Body).RootElement.GetProperty("body").GetString()!);

        return saved.Count == 1 ? new StubReply(502, """{"message":"Bad Gateway"}""") : new StubReply(201, CommentJson(saved.Count, saved[^1]));
    }

    private static string CommentJson(long id, string body) => JsonSerializer.Serialize(new { id, body, user = new { login = "codespace-bot" }, created_at = "2026-09-24T08:00:00Z", html_url = $"https://github.test/acme/api/pull/7#issuecomment-{id}" });

    private async Task<SeedResult> SeedGitHubRepositoryAsync(string baseUrl)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = new User { Id = Guid.NewGuid(), Email = $"u-{suffix}@x", Name = "tester" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{suffix}", Name = "Team" };
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team.Id, Provider = ProviderKind.GitHub, DisplayName = "loopback", BaseUrl = baseUrl, ApiUrl = baseUrl };
        var credential = new Credential
        {
            Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, Ownership = CredentialOwnership.TeamService,
            AuthType = AuthType.Pat, DisplayName = "connection", EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = "ghp_loopback" })), Status = CredentialStatus.Active
        };
        var repository = new Repository
        {
            Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, CredentialId = credential.Id,
            ExternalId = "4242", NamespacePath = "acme", Name = "api", FullPath = "acme/api",
            DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = "https://github.test/acme/api", Status = RepositoryStatus.Active
        };

        db.User.Add(user);
        db.Team.Add(team);
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = user.Id, Role = TeamRole.Owner });
        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        db.Repository.Add(repository);

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new SeedResult(team.Id, repository.Id);
    }

    private sealed record SeedResult(Guid TeamId, Guid RepositoryId);
}
