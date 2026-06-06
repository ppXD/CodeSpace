using System.Net;
using System.Security.Cryptography;
using System.Text;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Webhooks;

/// <summary>
/// E2E coverage for the webhook ingestion endpoint through the REAL ASP.NET pipeline
/// (routing, [AllowAnonymous], model binding, the controller try/catch, the GlobalExceptionFilter).
/// This is the only tier that pins the actual HTTP status a provider receives — the integration
/// suite drives IMediator directly and never boots the controller. Tier: 🟢 High-fidelity (real
/// app host + real Postgres).
/// </summary>
[Trait("Category", "E2E")]
public sealed class WebhookEndpointE2ETests : IClassFixture<WebhookApiFactory>
{
    private readonly WebhookApiFactory _factory;

    public WebhookEndpointE2ETests(WebhookApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task Unknown_webhook_id_returns_404()
    {
        // No webhook row for this id → ingestion's LoadWebhookAsync throws InvalidOperationException
        // → the controller's catch maps it to 404. Proves the real HTTP pipeline + controller mapping.
        var response = await PostAsync(Guid.NewGuid(), "{}", eventName: "pull_request");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Malformed_payload_returns_200_and_writes_malformed_audit()
    {
        // The flagship E2E: a signed-but-unparseable body MUST come back 200 (not 500/404) so the
        // provider doesn't retry-storm / auto-disable, AND leave a malformed_payload audit row. This
        // is the contract proven only at the HTTP boundary — the controller maps a normal command
        // return to Ok(), which the mediator-direct tests can't observe.
        var secret = $"gh-sec-{Guid.NewGuid():N}";
        var (webhookId, teamId) = await SeedGitHubWebhookAsync(secret);
        var deliveryId = $"e2e-malformed-{Guid.NewGuid():N}";
        const string body = "this is not json at all";

        var response = await PostSignedGitHubAsync(webhookId, body, secret, "pull_request", deliveryId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await AssertMalformedAuditWrittenAsync(deliveryId);
    }

    [Fact]
    public async Task Invalid_signature_returns_401()
    {
        // Wrong HMAC → the signature verifier fails → UnauthorizedAccessException → controller 401.
        var secret = $"gh-sec-{Guid.NewGuid():N}";
        var (webhookId, _) = await SeedGitHubWebhookAsync(secret);
        const string body = """{"action":"opened","pull_request":{"id":1,"number":1,"title":"x","head":{"ref":"f","sha":"s"},"base":{"ref":"main"},"user":{"id":1,"login":"u"},"html_url":"x"}}""";

        var request = BuildRequest(webhookId, body, "pull_request", deliveryId: "e2e-badsig");
        request.Headers.Add("X-Hub-Signature-256", "sha256=ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

        var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Pull_request_opened_returns_200()
    {
        // The happy path end-to-end: a correctly-signed pull_request:opened body flows through the
        // real pipeline → ingestion → normalize → publish → dispatch and returns 200. (No activation
        // is seeded, so the dispatcher records a no-match audit rather than starting a run — the HTTP
        // contract is the same 200 either way, which is what this tier pins.)
        var secret = $"gh-sec-{Guid.NewGuid():N}";
        var (webhookId, _) = await SeedGitHubWebhookAsync(secret);
        const string body = """{"action":"opened","pull_request":{"id":1234567,"number":42,"title":"Add feature","head":{"ref":"feature","sha":"s"},"base":{"ref":"main"},"user":{"id":583231,"login":"octocat"},"html_url":"https://x"}}""";

        var response = await PostSignedGitHubAsync(webhookId, body, secret, "pull_request", $"e2e-opened-{Guid.NewGuid():N}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private Task<HttpResponseMessage> PostAsync(Guid webhookId, string body, string eventName) =>
        _factory.CreateClient().SendAsync(BuildRequest(webhookId, body, eventName, deliveryId: null));

    private Task<HttpResponseMessage> PostSignedGitHubAsync(Guid webhookId, string body, string secret, string eventName, string deliveryId)
    {
        var request = BuildRequest(webhookId, body, eventName, deliveryId);
        request.Headers.Add("X-Hub-Signature-256", ComputeGitHubSignature(body, secret));
        return _factory.CreateClient().SendAsync(request);
    }

    private static HttpRequestMessage BuildRequest(Guid webhookId, string body, string eventName, string? deliveryId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/{webhookId}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-GitHub-Event", eventName);
        if (deliveryId != null) request.Headers.Add("X-GitHub-Delivery", deliveryId);
        return request;
    }

    private static string ComputeGitHubSignature(string body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        return $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private async Task<(Guid WebhookId, Guid TeamId)> SeedGitHubWebhookAsync(string secret)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var encryptor = scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var repoId = Guid.NewGuid();
        var webhookId = Guid.NewGuid();

        db.User.Add(new User { Id = userId, Email = $"e2e-{suffix}@test.local", Name = "E2E", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.Add(new Team { Id = teamId, Slug = $"e2e-{suffix}", Name = "E2E", Kind = TeamKind.Workspace, OwnerUserId = userId, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "GH", BaseUrl = $"https://gh-{suffix}.local", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Repository.Add(new Repository { Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, ExternalId = $"ext-{suffix}", NamespacePath = "acme", Name = "api", FullPath = $"acme/api-{suffix}", WebUrl = "https://gh.local/acme/api", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.RepositoryWebhook.Add(new RepositoryWebhook { Id = webhookId, RepositoryId = repoId, ExternalId = $"wh-{suffix}", CallbackUrl = $"https://x/cb/{suffix}", SecretEnc = encryptor.Encrypt(secret), Active = true, SubscribedEvents = new List<string> { "pull_request" }, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();
        return (webhookId, teamId);
    }

    private async Task AssertMalformedAuditWrittenAsync(string deliveryId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var rejected = await db.WorkflowRunRequest.AsNoTracking()
            .SingleOrDefaultAsync(r => r.ExternalEventId == deliveryId && r.Status == WorkflowRunRequestStatus.Rejected);

        rejected.ShouldNotBeNull(customMessage:
            "A signed-but-malformed payload MUST return 200 AND write a Rejected audit row — not 500/404. " +
            "Check WebhookIngestionService.PublishNormalizedEventAsync + the controller mapping.");
        rejected.Error!.ShouldContain(WorkflowRunRequestRejectionReasons.MalformedPayload);
    }
}
