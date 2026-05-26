using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Webhooks;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Webhooks;

/// <summary>
/// Pins the ingestion audit contract. Two coverage tiers:
///
///   <list type="bullet">
///     <item>End-to-end via <see cref="IWebhookIngestionService"/>: covers the wiring that
///           the ingestion service actually CALLS the auditor on each failure branch
///           (inactive webhook, normalizer-returns-null).</item>
///     <item>Direct auditor calls: cover the row shape + dedup contract without depending
///           on the TestRepositoryProvider's verifier behavior.</item>
///   </list>
///
/// The test infrastructure's TestRepositoryProvider returns true from VerifySignature and
/// null from Normalize — so end-to-end coverage naturally hits the EventNotMapped branch,
/// while the bad-signature branch is tested via the auditor surface directly.
/// </summary>
[Collection(PostgresCollection.Name)]
public class WebhookIngestionAuditFlowTests
{
    private readonly PostgresFixture _fixture;

    public WebhookIngestionAuditFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Unmapped_event_writes_rejected_audit_row_via_ingestion_service()
    {
        // TestRepositoryProvider's signature verifier always passes + normalizer always
        // returns null → hits the EventNotMapped branch end-to-end through the real service.
        var (teamId, webhookId) = await SeedActiveWebhookAsync();

        using (var scope = _fixture.BeginScope())
        {
            var ingest = scope.Resolve<IWebhookIngestionService>();
            await ingest.IngestAsync(
                webhookId,
                body: """{"event_type":"deployment"}""",
                headers: new Dictionary<string, string> { ["X-GitHub-Delivery"] = Guid.NewGuid().ToString("N") },
                CancellationToken.None);
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var rejected = await db.WorkflowRunRequest.AsNoTracking()
            .SingleAsync(r => r.TeamId == teamId && r.Status == WorkflowRunRequestStatus.Rejected);

        rejected.Error.ShouldNotBeNullOrEmpty();
        rejected.Error!.ShouldContain(WorkflowRunRequestRejectionReasons.EventNotMapped,
            customMessage: "EventNotMapped MUST be in the error column so the audit view's filter finds it; got: " + rejected.Error);
        rejected.SourceType.ShouldStartWith(WorkflowRunSourceTypes.ProviderPrefix);
        rejected.ActorType.ShouldBe(WorkflowRunActorTypes.Webhook);
        rejected.ExternalEventId.ShouldNotBeNullOrEmpty(
            "delivery id MUST be captured for the unmapped case — sig already passed so the header is trusted");
    }

    [Fact]
    public async Task Inactive_webhook_writes_rejected_audit_row_via_ingestion_service()
    {
        var (teamId, webhookId) = await SeedInactiveWebhookAsync();

        using (var scope = _fixture.BeginScope())
        {
            var ingest = scope.Resolve<IWebhookIngestionService>();
            await Should.ThrowAsync<InvalidOperationException>(() => ingest.IngestAsync(
                webhookId,
                body: "{}",
                headers: new Dictionary<string, string>(),
                CancellationToken.None));
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var rejected = await db.WorkflowRunRequest.AsNoTracking()
            .SingleAsync(r => r.TeamId == teamId && r.Status == WorkflowRunRequestStatus.Rejected);

        rejected.Error.ShouldContain(WorkflowRunRequestRejectionReasons.WebhookInactive);
    }

    [Fact]
    public async Task Auditor_writes_signature_invalid_row_directly()
    {
        var teamId = await SeedBareTeamAsync();
        var deliveryId = Guid.NewGuid().ToString("N");

        using (var scope = _fixture.BeginScope())
        {
            var auditor = scope.Resolve<IIngestionAuditor>();
            await auditor.WriteWebhookRejectedAsync(new WebhookRejectionContext
            {
                TeamId = teamId,
                Reason = WorkflowRunRequestRejectionReasons.SignatureInvalid,
                Detail = "HMAC-SHA256 mismatch",
                SourceType = $"{WorkflowRunSourceTypes.ProviderPrefix}github",
                ExternalEventId = deliveryId,
                VerificationResultJson = """{"validated":false}""",
            }, CancellationToken.None);
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var rejected = await db.WorkflowRunRequest.AsNoTracking()
            .SingleAsync(r => r.TeamId == teamId);

        rejected.Status.ShouldBe(WorkflowRunRequestStatus.Rejected);
        rejected.Error.ShouldContain(WorkflowRunRequestRejectionReasons.SignatureInvalid);
        rejected.Error.ShouldContain("HMAC-SHA256 mismatch",
            customMessage: "free-text detail MUST be appended to the reason so triage has the underlying error");
        rejected.VerificationResultJson.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Auditor_dedupes_duplicate_provider_retries_on_external_event_id()
    {
        var teamId = await SeedBareTeamAsync();
        var deliveryId = Guid.NewGuid().ToString("N");
        var ctx = new WebhookRejectionContext
        {
            TeamId = teamId,
            Reason = WorkflowRunRequestRejectionReasons.SignatureInvalid,
            Detail = "first attempt",
            SourceType = $"{WorkflowRunSourceTypes.ProviderPrefix}github",
            ExternalEventId = deliveryId,
        };

        // Provider retries the same delivery with the same signature → second write must NOT
        // duplicate the audit row. The unique index on (source_type, external_event_id)
        // makes the dedup automatic; the auditor swallows the unique-violation.
        using (var scope = _fixture.BeginScope())
        {
            var auditor = scope.Resolve<IIngestionAuditor>();
            await auditor.WriteWebhookRejectedAsync(ctx, CancellationToken.None);
        }
        using (var scope = _fixture.BeginScope())
        {
            var auditor = scope.Resolve<IIngestionAuditor>();
            await auditor.WriteWebhookRejectedAsync(ctx with { Detail = "retry" }, CancellationToken.None);
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var rows = await db.WorkflowRunRequest.AsNoTracking()
            .Where(r => r.TeamId == teamId && r.ExternalEventId == deliveryId)
            .ToListAsync();

        rows.Count.ShouldBe(1,
            "duplicate provider retry MUST NOT insert a second audit row — the unique index dedupes by (source_type, external_event_id)");
        rows[0].Error!.ShouldContain("first attempt");
        // first write wins — the retry's detail is silently dropped because the existing row already captures the rejection
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<(Guid TeamId, Guid WebhookId)> SeedActiveWebhookAsync() => await SeedWebhookAsync(active: true);
    private async Task<(Guid TeamId, Guid WebhookId)> SeedInactiveWebhookAsync() => await SeedWebhookAsync(active: false);

    private async Task<Guid> SeedBareTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"audit-{userId:N}@test.local", Name = $"audit-{userId:N}", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.Add(new Team { Id = teamId, Slug = $"audit-{teamId:N}", Name = "Audit Test", Kind = TeamKind.Workspace, OwnerUserId = userId, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        await db.SaveChangesAsync();
        return teamId;
    }

    private async Task<(Guid TeamId, Guid WebhookId)> SeedWebhookAsync(bool active)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();

        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        db.User.Add(new User { Id = userId, Email = $"audit-{userId:N}@test.local", Name = $"audit-{userId:N}", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.Add(new Team { Id = teamId, Slug = $"audit-{teamId:N}", Name = "Audit Test", Kind = TeamKind.Workspace, OwnerUserId = userId, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        var providerInstanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance
        {
            Id = providerInstanceId,
            TeamId = teamId,
            Provider = ProviderKind.Git,                          // matches TestRepositoryProvider.Kind
            BaseUrl = "https://test.local",
            DisplayName = "Test PI",
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });

        var repositoryId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repositoryId,
            TeamId = teamId,
            ProviderInstanceId = providerInstanceId,
            ExternalId = "test-ext-" + Guid.NewGuid().ToString("N")[..6],
            NamespacePath = "audit",
            FullPath = "audit/repo",
            Name = "repo",
            WebUrl = "https://test.local/audit/repo",
            DefaultBranch = "main",
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });

        var webhookId = Guid.NewGuid();
        db.RepositoryWebhook.Add(new RepositoryWebhook
        {
            Id = webhookId,
            RepositoryId = repositoryId,
            ExternalId = "wh-" + Guid.NewGuid().ToString("N")[..6],
            CallbackUrl = $"https://test.local/webhooks/{webhookId}",
            SecretEnc = encryptor.Encrypt("secret"),
            Active = active,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return (teamId, webhookId);
    }
}
