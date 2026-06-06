using System.Security.Cryptography;
using System.Text;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Webhooks;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Events.Issue;
using CodeSpace.Messages.Events.PullRequest;
using CodeSpace.Messages.Events.Push;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Webhooks;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ReceiveWebhookFlowTests
{
    private readonly PostgresFixture _fixture;

    public ReceiveWebhookFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task GitHub_pull_request_opened_publishes_event_and_updates_last_received()
    {
        var secret = $"gh-sec-{Guid.NewGuid():N}";
        var body = @"{""action"":""opened"",""pull_request"":{""id"":1234567,""number"":42,""title"":""Add feature"",""body"":""Body"",""head"":{""ref"":""feature"",""sha"":""s""},""base"":{""ref"":""main""},""user"":{""id"":583231,""login"":""octocat""},""html_url"":""https://x""}}";
        var headers = new Dictionary<string, string>
        {
            ["X-GitHub-Event"] = "pull_request",
            ["X-GitHub-Delivery"] = "delivery-gh-1",
            ["X-Hub-Signature-256"] = ComputeGitHubSignature(body, secret)
        };

        var (webhookId, repositoryId) = await SeedAsync(ProviderKind.GitHub, secret).ConfigureAwait(false);
        ClearCapturedEvents();

        await SendReceiveWebhookAsync(webhookId, body, headers).ConfigureAwait(false);

        await AssertWebhookLastReceivedSetAsync(webhookId).ConfigureAwait(false);

        var captured = SnapshotCapturedEvents();
        captured.OfType<PullRequestOpenedEvent>().ShouldContain(e => e.RepositoryId == repositoryId && e.Number == 42 && e.Title == "Add feature");
    }

    [Fact]
    public async Task GitLab_merge_request_open_publishes_event()
    {
        var secret = $"gl-sec-{Guid.NewGuid():N}";
        var body = @"{""object_kind"":""merge_request"",""user"":{""id"":1,""username"":""alice""},""object_attributes"":{""id"":99,""iid"":5,""title"":""MR Title"",""description"":""Body"",""source_branch"":""feature"",""target_branch"":""main"",""action"":""open"",""url"":""https://x""}}";
        var headers = new Dictionary<string, string>
        {
            ["X-Gitlab-Event"] = "Merge Request Hook",
            ["X-Gitlab-Event-UUID"] = "uuid-gl-1",
            ["X-Gitlab-Token"] = secret
        };

        var (webhookId, repositoryId) = await SeedAsync(ProviderKind.GitLab, secret).ConfigureAwait(false);
        ClearCapturedEvents();

        await SendReceiveWebhookAsync(webhookId, body, headers).ConfigureAwait(false);

        await AssertWebhookLastReceivedSetAsync(webhookId).ConfigureAwait(false);

        var captured = SnapshotCapturedEvents();
        captured.OfType<PullRequestOpenedEvent>().ShouldContain(e => e.RepositoryId == repositoryId && e.Number == 5 && e.AuthorName == "alice");
    }

    [Fact]
    public async Task GitLab_merge_request_update_with_oldrev_publishes_synchronized_event()
    {
        // A real code push to an open MR: GitLab fires action:"update" WITH object_attributes.oldrev.
        // The full HTTP → signature → normalize path must publish a PullRequestSynchronizedEvent so
        // `trigger.pr.updated` workflows fire on actual new commits.
        var secret = $"gl-sec-{Guid.NewGuid():N}";
        var body = @"{""object_kind"":""merge_request"",""user"":{""id"":1,""username"":""alice""},""object_attributes"":{""id"":99,""iid"":5,""title"":""MR Title"",""source_branch"":""feature"",""target_branch"":""main"",""action"":""update"",""oldrev"":""old-sha"",""last_commit"":{""id"":""new-sha""},""url"":""https://x""}}";
        var headers = new Dictionary<string, string>
        {
            ["X-Gitlab-Event"] = "Merge Request Hook",
            ["X-Gitlab-Event-UUID"] = "uuid-gl-update-push",
            ["X-Gitlab-Token"] = secret
        };

        var (webhookId, repositoryId) = await SeedAsync(ProviderKind.GitLab, secret).ConfigureAwait(false);
        ClearCapturedEvents();

        await SendReceiveWebhookAsync(webhookId, body, headers).ConfigureAwait(false);

        await AssertWebhookLastReceivedSetAsync(webhookId).ConfigureAwait(false);

        var captured = SnapshotCapturedEvents();
        captured.OfType<PullRequestSynchronizedEvent>().ShouldContain(e => e.RepositoryId == repositoryId && e.Number == 5 && e.NewHeadSha == "new-sha");
    }

    [Fact]
    public async Task GitLab_merge_request_metadata_update_without_oldrev_publishes_nothing()
    {
        // A metadata-only MR edit (label / assignee / description) also fires action:"update" but
        // WITHOUT object_attributes.oldrev. Ingestion must succeed (LastReceived set) yet publish NO
        // event — otherwise every label edit would spuriously start a `trigger.pr.updated` run with
        // empty head SHAs. Regression guard for the GitLab metadata-update false-fire.
        var secret = $"gl-sec-{Guid.NewGuid():N}";
        var body = @"{""object_kind"":""merge_request"",""user"":{""id"":1,""username"":""alice""},""object_attributes"":{""id"":99,""iid"":5,""title"":""MR Title"",""source_branch"":""feature"",""target_branch"":""main"",""action"":""update"",""url"":""https://x""}}";
        var headers = new Dictionary<string, string>
        {
            ["X-Gitlab-Event"] = "Merge Request Hook",
            ["X-Gitlab-Event-UUID"] = "uuid-gl-update-meta",
            ["X-Gitlab-Token"] = secret
        };

        var (webhookId, _) = await SeedAsync(ProviderKind.GitLab, secret).ConfigureAwait(false);
        ClearCapturedEvents();

        await SendReceiveWebhookAsync(webhookId, body, headers).ConfigureAwait(false);

        await AssertWebhookLastReceivedSetAsync(webhookId).ConfigureAwait(false);
        SnapshotCapturedEvents().ShouldBeEmpty();
    }

    [Fact]
    public async Task Invalid_signature_throws_Unauthorized_and_publishes_nothing()
    {
        var secret = $"gh-sec-{Guid.NewGuid():N}";
        var body = @"{""action"":""opened"",""pull_request"":{""id"":1,""number"":1,""title"":""x"",""head"":{""ref"":""f"",""sha"":""s""},""base"":{""ref"":""main""},""user"":{""id"":1,""login"":""u""},""html_url"":""x""}}";
        var headers = new Dictionary<string, string>
        {
            ["X-GitHub-Event"] = "pull_request",
            ["X-Hub-Signature-256"] = "sha256=ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"
        };

        var (webhookId, _) = await SeedAsync(ProviderKind.GitHub, secret).ConfigureAwait(false);
        ClearCapturedEvents();

        var act = async () => await SendReceiveWebhookAsync(webhookId, body, headers).ConfigureAwait(false);
        await act.ShouldThrowAsync<UnauthorizedAccessException>().ConfigureAwait(false);

        SnapshotCapturedEvents().ShouldBeEmpty();
    }

    [Fact]
    public async Task Unknown_webhook_id_throws_InvalidOperation()
    {
        var unknownId = Guid.NewGuid();
        var headers = new Dictionary<string, string> { ["X-GitHub-Event"] = "pull_request" };

        var act = async () => await SendReceiveWebhookAsync(unknownId, "{}", headers).ConfigureAwait(false);

        await act.ShouldThrowAsync<InvalidOperationException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task Unhandled_event_type_returns_without_publish_but_updates_last_received()
    {
        var secret = $"gh-sec-{Guid.NewGuid():N}";
        var body = "{}";
        var headers = new Dictionary<string, string>
        {
            ["X-GitHub-Event"] = "ping",
            ["X-Hub-Signature-256"] = ComputeGitHubSignature(body, secret)
        };

        var (webhookId, _) = await SeedAsync(ProviderKind.GitHub, secret).ConfigureAwait(false);
        ClearCapturedEvents();

        await SendReceiveWebhookAsync(webhookId, body, headers).ConfigureAwait(false);

        await AssertWebhookLastReceivedSetAsync(webhookId).ConfigureAwait(false);
        SnapshotCapturedEvents().ShouldBeEmpty();
    }

    [Fact]
    public async Task GitHub_push_event_publishes_PushReceivedEvent()
    {
        var secret = $"gh-sec-{Guid.NewGuid():N}";
        var body = @"{""ref"":""refs/heads/main"",""before"":""before-sha"",""after"":""after-sha"",""pusher"":{""name"":""octocat"",""email"":""o@x""},""sender"":{""id"":583231,""login"":""octocat""},""commits"":[{""id"":""abc"",""message"":""m"",""author"":{""name"":""o"",""email"":""o@x""}}]}";
        var headers = new Dictionary<string, string>
        {
            ["X-GitHub-Event"] = "push",
            ["X-Hub-Signature-256"] = ComputeGitHubSignature(body, secret)
        };

        var (webhookId, repositoryId) = await SeedAsync(ProviderKind.GitHub, secret).ConfigureAwait(false);
        ClearCapturedEvents();

        await SendReceiveWebhookAsync(webhookId, body, headers).ConfigureAwait(false);

        var captured = SnapshotCapturedEvents();
        captured.OfType<PushReceivedEvent>().ShouldContain(e => e.RepositoryId == repositoryId && e.AfterSha == "after-sha");
    }

    private async Task<(Guid WebhookId, Guid RepositoryId)> SeedAsync(ProviderKind providerKind, string webhookSecret)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);

        var owner = new User { Id = Guid.NewGuid(), Email = $"owner-{suffix}@x", Name = "Owner" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"team-{suffix}", Name = "Team", OwnerUserId = owner.Id };
        var project = TestProjectSeed.BuildDefaultProject(team.Id, owner.Id);
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team.Id, Provider = providerKind, DisplayName = "Inst", BaseUrl = $"https://{providerKind}-{suffix}.example.com" };
        var credential = new Credential { Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, AuthType = AuthType.Pat, DisplayName = "PAT", EncryptedPayload = encryptor.Encrypt("{\"token\":\"x\"}") };
        var repo = new Repository { Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, CredentialId = credential.Id, ExternalId = $"42-{suffix}", NamespacePath = "n", Name = "r", FullPath = $"n/r-{suffix}", WebUrl = "https://x" };
        var webhook = new RepositoryWebhook { Id = Guid.NewGuid(), RepositoryId = repo.Id, ExternalId = $"wh-{suffix}", CallbackUrl = "https://x/cb", SecretEnc = encryptor.Encrypt(webhookSecret), SubscribedEvents = new List<string> { "push", "pull_request" } };

        db.User.Add(owner);
        db.Team.Add(team);
        db.Project.Add(project);
        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        db.Repository.Add(repo);
        db.RepositoryWebhook.Add(webhook);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (webhook.Id, repo.Id);
    }

    private async Task SendReceiveWebhookAsync(Guid webhookId, string body, IReadOnlyDictionary<string, string> headers)
    {
        using var scope = _fixture.BeginScope();
        var mediator = scope.Resolve<IMediator>();
        await mediator.Send(new ReceiveWebhookCommand
        {
            WebhookId = webhookId,
            Body = body,
            Headers = headers
        }).ConfigureAwait(false);
    }

    private async Task AssertWebhookLastReceivedSetAsync(Guid webhookId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var webhook = await db.RepositoryWebhook.AsNoTracking().SingleAsync(w => w.Id == webhookId).ConfigureAwait(false);
        webhook.LastReceivedDate.ShouldNotBeNull();
    }

    private void ClearCapturedEvents()
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<CapturedNormalizedEvents>().Clear();
    }

    private IReadOnlyList<Messages.Events.NormalizedEvent> SnapshotCapturedEvents()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<CapturedNormalizedEvents>().Snapshot();
    }

    private static string ComputeGitHubSignature(string body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        return $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
