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
/// The group-hook endpoint through the REAL ASP.NET pipeline. This tier exists for exactly what
/// changed: a per-repository hook is identified by its URL, and this one is not — the route carries
/// only the hook, and which repository a delivery is about is decided from the body. Everything that
/// depends on that is a wire fact: that the route exists and does not collide with
/// <c>/api/webhooks/{id}</c>, and that a delivery for a repository nobody bound comes back 200
/// rather than something a provider would retry or disable the hook over.
///
/// <para>Tier: 🟢 High-fidelity (real app host + real Postgres).</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class ConnectionWebhookEndpointE2ETests : IClassFixture<WebhookApiFactory>
{
    private readonly WebhookApiFactory _factory;

    public ConnectionWebhookEndpointE2ETests(WebhookApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task A_group_delivery_for_a_bound_repository_returns_200_and_touches_that_hook()
    {
        var secret = $"conn-sec-{Guid.NewGuid():N}";
        var seed = await SeedConnectionAsync(secret);
        var body = BuildPushBody(seed.BoundExternalId, seed.BoundFullPath);

        var response = await PostSignedAsync(seed.ConnectionWebhookId, body, secret, $"e2e-bound-{Guid.NewGuid():N}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // last_received_date is the observable proof the route resolved to THIS hook — the id in the
        // URL is the only thing the route has to go on, and a collision with /api/webhooks/{id}
        // would 404 long before any of the body-reading matters.
        await AssertLastReceivedSetAsync(seed.ConnectionWebhookId);
    }

    [Fact]
    public async Task A_group_delivery_for_an_unbound_repository_returns_200_and_is_audited()
    {
        // The ordinary case for a group hook: it covers every project in the group and we bound one.
        // 200 is the contract — any other status is read as "retry", and GitLab disables a hook that
        // keeps failing, which would take out the repositories we DO track.
        var secret = $"conn-sec-{Guid.NewGuid():N}";
        var seed = await SeedConnectionAsync(secret);
        var deliveryId = $"e2e-unbound-{Guid.NewGuid():N}";
        var body = BuildPushBody("88888888", "acme/not-ours");

        var response = await PostSignedAsync(seed.ConnectionWebhookId, body, secret, deliveryId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await AssertNotBoundAuditWrittenAsync(deliveryId);
    }

    [Fact]
    public async Task A_delivery_signed_with_the_wrong_secret_returns_401()
    {
        var seed = await SeedConnectionAsync($"conn-sec-{Guid.NewGuid():N}");
        var body = BuildPushBody(seed.BoundExternalId, seed.BoundFullPath);

        var response = await PostSignedAsync(seed.ConnectionWebhookId, body, "not-the-connection-secret", $"e2e-badsig-{Guid.NewGuid():N}");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_unknown_connection_webhook_id_returns_404()
    {
        var body = BuildPushBody("1", "acme/whatever");

        var response = await PostSignedAsync(Guid.NewGuid(), body, "any-secret", "e2e-unknown");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private Task<HttpResponseMessage> PostSignedAsync(Guid connectionWebhookId, string body, string secret, string deliveryId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/connection/{connectionWebhookId}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-GitHub-Event", "push");
        request.Headers.Add("X-GitHub-Delivery", deliveryId);
        request.Headers.Add("X-Hub-Signature-256", ComputeGitHubSignature(body, secret));

        return _factory.CreateClient().SendAsync(request);
    }

    private static string BuildPushBody(string repositoryId, string fullName) =>
        "{\"ref\":\"refs/heads/main\",\"before\":\"0000\",\"after\":\"abcd\"" +
        ",\"repository\":{\"id\":" + repositoryId + ",\"full_name\":\"" + fullName + "\"}" +
        ",\"pusher\":{\"name\":\"Alice\"},\"sender\":{\"id\":7,\"login\":\"alice\"}" +
        ",\"commits\":[{\"id\":\"abcd\",\"message\":\"Work\",\"author\":{\"email\":\"a@x\",\"name\":\"Alice\"}}]}";

    private static string ComputeGitHubSignature(string body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        return $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private async Task AssertLastReceivedSetAsync(Guid connectionWebhookId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var hook = await db.ConnectionWebhook.AsNoTracking().SingleAsync(w => w.Id == connectionWebhookId);

        hook.LastReceivedDate.ShouldNotBeNull(customMessage:
            "The delivery never reached this hook. Check the route in WebhooksController — /api/webhooks/connection/{id} " +
            "must not be shadowed by /api/webhooks/{webhookId:guid}.");
    }

    private async Task AssertNotBoundAuditWrittenAsync(string deliveryId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var rejected = await db.WorkflowRunRequest.AsNoTracking()
            .SingleOrDefaultAsync(r => r.ExternalEventId == deliveryId && r.Status == WorkflowRunRequestStatus.Rejected);

        rejected.ShouldNotBeNull(customMessage:
            "A group delivery for an unbound repository must leave a Rejected audit row — dropping it silently gives " +
            "the operator nothing to look at when they ask why a repository they thought was covered did nothing.");
        rejected.Error!.ShouldContain(WorkflowRunRequestRejectionReasons.RepositoryNotBound);
    }

    private async Task<ConnectionSeed> SeedConnectionAsync(string secret)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var encryptor = scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var connectionWebhookId = Guid.NewGuid();
        var externalId = $"{DateTime.UtcNow.Ticks % 1000000}";
        var fullPath = $"acme/api-{suffix}";

        db.User.Add(new User { Id = userId, Email = $"e2e-{suffix}@test.local", Name = "E2E", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.Add(new Team { Id = teamId, Slug = $"e2e-{suffix}", Name = "E2E", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "GH", BaseUrl = $"https://gh-{suffix}.local", WebhookScope = ProviderWebhookScope.Connection, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Credential.Add(new Credential { Id = credentialId, TeamId = teamId, ProviderInstanceId = instanceId, AuthType = AuthType.Pat, DisplayName = "PAT", EncryptedPayload = encryptor.Encrypt("{}"), CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Repository.Add(new Repository { Id = repositoryId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId, ExternalId = externalId, NamespacePath = "acme", Name = $"api-{suffix}", FullPath = fullPath, WebUrl = "https://gh.local/acme/api", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.ConnectionWebhook.Add(new ConnectionWebhook
        {
            Id = connectionWebhookId,
            ProviderInstanceId = instanceId,
            CredentialId = credentialId,
            OwnerPath = "acme",
            ExternalId = $"orghook-{suffix}",
            CallbackUrl = $"https://x/cb/connection/{connectionWebhookId}",
            SecretEnc = encryptor.Encrypt(secret),
            SubscribedEvents = new List<string> { "push" },
            Active = true,
            RegistrationStatus = RepositoryWebhookRegistrationStatus.Registered,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId
        });

        await db.SaveChangesAsync();

        return new ConnectionSeed { ConnectionWebhookId = connectionWebhookId, BoundExternalId = externalId, BoundFullPath = fullPath };
    }

    private sealed record ConnectionSeed
    {
        public required Guid ConnectionWebhookId { get; init; }
        public required string BoundExternalId { get; init; }
        public required string BoundFullPath { get; init; }
    }
}
