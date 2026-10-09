using System.Security.Cryptography;
using System.Text;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Webhooks;
using CodeSpace.Core.Services.Webhooks.Exceptions;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Webhooks;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Events;
using CodeSpace.Messages.Events.PullRequest;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Webhooks;

/// <summary>
/// What a per-repository hook keeps when a stranger or a replay reaches it, through the real command, transaction,
/// auditor and claim table: refusals anyone can cause collapse to one bounded row per (hook, reason) per day; a signed
/// GitHub body without its delivery id is refused; a signed body replayed under a fresh id is not published again,
/// while the provider redelivering the SAME id still is (the per-activation idempotency key handles that one).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class WebhookIngressRefusalFlowTests
{
    private readonly PostgresFixture _fixture;

    public WebhookIngressRefusalFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Bad_signatures_collapse_to_one_bounded_row_and_keep_the_providers_own_headers()
    {
        var seed = await SeedAsync(ProviderKind.GitHub);

        for (var i = 0; i < 5; i++)
        {
            var headers = new Dictionary<string, string> { ["X-GitHub-Event"] = "pull_request", ["X-GitHub-Delivery"] = $"forged-{i}", ["X-Hub-Signature-256"] = "sha256=00", ["User-Agent"] = new string('<', 4000) };
            foreach (var junk in Enumerable.Range(0, 100)) headers[$"X-Junk-{junk}-{new string('n', 300)}"] = "v";

            await Should.ThrowAsync<UnauthorizedAccessException>(() => SendAsync(seed.WebhookId, "{}", headers));
        }

        var rows = await LoadRefusalsAsync(seed.RepositoryId, WorkflowRunRequestRejectionReasons.SignatureInvalid);
        rows.Count.ShouldBe(1, customMessage: "a refusal anyone can cause is one row per (hook, reason) per day — check BuildRefusalWindowKey in VerifySignatureOrAuditAsync");
        var stored = rows[0].RawHeadersRedactedJson.ShouldNotBeNull();
        stored.Length.ShouldBeLessThan(4096);
        stored.ShouldContain("forged-0", customMessage: "the provider's delivery id survives the cap, so the row can be matched against the provider's log");
    }

    [Theory]
    [InlineData(HookState.Inactive, WorkflowRunRequestRejectionReasons.WebhookInactive)]
    [InlineData(HookState.Retired, WorkflowRunRequestRejectionReasons.WebhookRetired)]
    [InlineData(HookState.RepositoryRemoved, WorkflowRunRequestRejectionReasons.WebhookRetired)]
    public async Task A_switched_off_or_retired_hook_records_its_refusal_once_however_often_it_is_posted_to(HookState state, string reason)
    {
        var seed = await SeedAsync(ProviderKind.GitHub);
        await PutInStateAsync(seed, state);

        for (var i = 0; i < 3; i++)
            await Should.ThrowAsync<InvalidOperationException>(() => SendAsync(seed.WebhookId, "{}", new Dictionary<string, string> { ["X-GitHub-Event"] = "push" }));

        (await LoadRefusalsAsync(seed.RepositoryId, reason)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_signed_github_delivery_without_its_delivery_id_is_refused_and_recorded_once()
    {
        var seed = await SeedAsync(ProviderKind.GitHub);
        var body = OpenedBody(number: 1);

        for (var i = 0; i < 2; i++)
            await Should.ThrowAsync<WebhookDeliveryUnidentifiedException>(() => SendAsync(seed.WebhookId, body, GitHubHeaders(body, seed.Secret, deliveryId: null)));

        (await LoadRefusalsAsync(seed.RepositoryId, WorkflowRunRequestRejectionReasons.DeliveryIdMissing)).Count.ShouldBe(1);
        (await LoadLastReceivedAsync(seed.WebhookId)).ShouldBeNull("a refused delivery does not count as received");
    }

    [Fact]
    public async Task A_replayed_body_is_published_once_and_a_redelivery_of_the_same_id_is_published_again()
    {
        var seed = await SeedAsync(ProviderKind.GitHub);
        var body = OpenedBody(number: 2);
        ClearCapturedEvents();

        await SendAsync(seed.WebhookId, body, GitHubHeaders(body, seed.Secret, "captured-1"));
        for (var i = 0; i < 3; i++) await SendAsync(seed.WebhookId, body, GitHubHeaders(body, seed.Secret, $"attacker-{i}"));
        await SendAsync(seed.WebhookId, body, GitHubHeaders(body, seed.Secret, "captured-1"));

        SnapshotCapturedEvents().OfType<PullRequestOpenedEvent>().Select(e => e.ProviderEventId).ShouldBe(new[] { "captured-1", "captured-1" },
            customMessage: "a fresh id on an old body is a replay; the same id is the provider redelivering, which must still reach dispatch so an operator's Redeliver works");
        (await LoadRefusalsAsync(seed.RepositoryId, WorkflowRunRequestRejectionReasons.DeliveryReplayed)).Count.ShouldBe(1, customMessage: "one row per replayed body, however often it is posted");
    }

    [Fact]
    public async Task A_gitlab_body_posted_twice_is_published_twice()
    {
        var seed = await SeedAsync(ProviderKind.GitLab);
        const string body = """{"object_kind":"merge_request","user":{"id":1,"username":"alice"},"object_attributes":{"id":99,"iid":5,"title":"t","source_branch":"f","target_branch":"main","action":"open","url":"https://x"}}""";
        ClearCapturedEvents();

        for (var i = 0; i < 2; i++)
            await SendAsync(seed.WebhookId, body, new Dictionary<string, string> { ["X-Gitlab-Event"] = "Merge Request Hook", ["X-Gitlab-Event-UUID"] = $"gl-{Guid.NewGuid():N}", ["X-Gitlab-Token"] = seed.Secret });

        SnapshotCapturedEvents().OfType<PullRequestOpenedEvent>().Count().ShouldBe(2, customMessage: "GitLab's token signs any body, so a replay check would stop nothing — it is GitHub-shaped providers only");
    }

    public enum HookState { Inactive, Retired, RepositoryRemoved }

    private static string OpenedBody(int number) =>
        "{\"action\":\"opened\",\"pull_request\":{\"id\":" + (500 + number) + ",\"number\":" + number + ",\"title\":\"t\",\"head\":{\"ref\":\"f\",\"sha\":\"s\"},\"base\":{\"ref\":\"main\"},\"user\":{\"id\":5,\"login\":\"u\"},\"html_url\":\"https://x\"}}";

    private static Dictionary<string, string> GitHubHeaders(string body, string secret, string? deliveryId)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var headers = new Dictionary<string, string> { ["X-GitHub-Event"] = "pull_request", ["X-Hub-Signature-256"] = "sha256=" + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant() };
        if (deliveryId != null) headers["X-GitHub-Delivery"] = deliveryId;
        return headers;
    }

    private async Task SendAsync(Guid webhookId, string body, IReadOnlyDictionary<string, string> headers)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IMediator>().Send(new ReceiveWebhookCommand { WebhookId = webhookId, Body = body, Headers = headers });
    }

    private sealed record Seed(Guid WebhookId, Guid RepositoryId, string Secret);

    private async Task<Seed> SeedAsync(ProviderKind provider)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var team = new Team { Id = Guid.NewGuid(), Slug = $"ingress-{suffix}", Name = "Team" };
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team.Id, Provider = provider, DisplayName = "Inst", BaseUrl = $"https://{provider}-{suffix}.invalid" };
        var repository = new Repository { Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, ExternalId = $"ext-{suffix}", NamespacePath = "n", Name = "r", FullPath = $"n/r-{suffix}", WebUrl = "https://x" };
        var seed = new Seed(Guid.NewGuid(), repository.Id, $"sec-{Guid.NewGuid():N}");

        db.Team.Add(team);
        db.ProviderInstance.Add(instance);
        db.Repository.Add(repository);
        db.RepositoryWebhook.Add(new RepositoryWebhook { Id = seed.WebhookId, RepositoryId = repository.Id, ExternalId = $"wh-{suffix}", CallbackUrl = "https://x/cb", SecretEnc = encryptor.Encrypt(seed.Secret), SubscribedEvents = new List<string> { "*" } });
        await db.SaveChangesAsync();

        return seed;
    }

    private async Task PutInStateAsync(Seed seed, HookState state)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        if (state == HookState.Inactive) await db.RepositoryWebhook.Where(w => w.Id == seed.WebhookId).ExecuteUpdateAsync(s => s.SetProperty(w => w.Active, false));
        if (state == HookState.Retired) await db.RepositoryWebhook.Where(w => w.Id == seed.WebhookId).ExecuteUpdateAsync(s => s.SetProperty(w => w.RegistrationStatus, RepositoryWebhookRegistrationStatus.Cancelled));
        if (state == HookState.RepositoryRemoved) await db.Repository.Where(r => r.Id == seed.RepositoryId).ExecuteUpdateAsync(s => s.SetProperty(r => r.DeletedDate, (DateTimeOffset?)DateTimeOffset.UtcNow));
    }

    private async Task<List<WorkflowRunRequest>> LoadRefusalsAsync(Guid repositoryId, string reason)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRequest.AsNoTracking()
            .Where(r => r.RepositoryId == repositoryId && r.Status == WorkflowRunRequestStatus.Rejected && r.Error!.StartsWith(reason + ":"))
            .ToListAsync();
    }

    private async Task<DateTimeOffset?> LoadLastReceivedAsync(Guid webhookId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().RepositoryWebhook.AsNoTracking().Where(w => w.Id == webhookId).Select(w => w.LastReceivedDate).SingleAsync();
    }

    private void ClearCapturedEvents()
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<CapturedNormalizedEvents>().Clear();
    }

    private IReadOnlyList<NormalizedEvent> SnapshotCapturedEvents()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<CapturedNormalizedEvents>().Snapshot();
    }
}
