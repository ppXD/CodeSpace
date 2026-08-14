using System.Linq.Expressions;
using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Events;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Webhooks;

/// <summary>
/// Both ingress pipelines, and the one thing that differs between them: WHO the delivery is about.
/// A per-repository hook answers that from its own callback URL; a connection-scoped hook answers it
/// from the payload, because one group hook carries every project underneath the owner.
///
/// <para>Everything downstream of that question is shared — the same signature check, the same
/// normalizer, the same rejection rows — which is what <see cref="IngestionSubject"/> is for: the
/// audit writers in the <c>.Audit</c> partial take a subject, so a hook that fails reads the same
/// whichever scope it was in.</para>
/// </summary>
public sealed partial class WebhookIngestionService : IWebhookIngestionService, IConnectionWebhookIngestionService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IProviderRegistry _registry;
    private readonly IPayloadEncryptor _encryptor;
    private readonly IMediator _mediator;
    private readonly IIngestionAuditor _auditor;
    private readonly ILogger<WebhookIngestionService> _logger;

    public WebhookIngestionService(CodeSpaceDbContext db, IProviderRegistry registry, IPayloadEncryptor encryptor, IMediator mediator, IIngestionAuditor auditor, ILogger<WebhookIngestionService> logger)
    {
        _db = db;
        _registry = registry;
        _encryptor = encryptor;
        _mediator = mediator;
        _auditor = auditor;
        _logger = logger;
    }

    public async Task IngestAsync(Guid webhookId, string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        var webhook = await LoadWebhookAsync(webhookId, cancellationToken).ConfigureAwait(false);
        var subject = DescribeRepositoryHook(webhook);

        await EnsureActiveOrAuditAsync(webhook.Active, subject, headers, cancellationToken).ConfigureAwait(false);

        var verifier = _registry.Require<IWebhookSignatureVerifier>(subject.Provider);
        var normalizer = _registry.Require<IWebhookEventNormalizer>(subject.Provider);
        var secret = _encryptor.Decrypt(webhook.SecretEnc);

        await VerifySignatureOrAuditAsync(verifier, body, headers, secret, subject, cancellationToken).ConfigureAwait(false);

        webhook.LastReceivedDate = DateTimeOffset.UtcNow;

        await PublishNormalizedEventAsync(normalizer, subject, body, headers, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A group / organization delivery. The id in the route names only the HOOK; the repository is
    /// still to be found, so identification sits between the signature check and normalization —
    /// after, because an unverified body is not evidence of anything, and before, because the
    /// normalizer has to be HANDED a repository id.
    /// </summary>
    public async Task IngestConnectionAsync(Guid connectionWebhookId, string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        var webhook = await LoadConnectionWebhookAsync(connectionWebhookId, cancellationToken).ConfigureAwait(false);
        var subject = DescribeConnectionHook(webhook);

        await EnsureActiveOrAuditAsync(webhook.Active, subject, headers, cancellationToken).ConfigureAwait(false);
        await EnsureNotRetiredOrAuditAsync(webhook.RegistrationStatus, subject, headers, cancellationToken).ConfigureAwait(false);

        var verifier = _registry.Require<IWebhookSignatureVerifier>(subject.Provider);
        var normalizer = _registry.Require<IWebhookEventNormalizer>(subject.Provider);
        var secret = _encryptor.Decrypt(webhook.SecretEnc);

        await VerifySignatureOrAuditAsync(verifier, body, headers, secret, subject, cancellationToken).ConfigureAwait(false);

        webhook.LastReceivedDate = DateTimeOffset.UtcNow;

        await RouteConnectionDeliveryAsync(webhook, normalizer, subject, body, headers, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Read the repository out of the payload, match it against what this connection has bound, and
    /// hand the rest to the shared normalization step under a subject that now names the repository.
    /// Identification runs inside the same catch as normalization because it reads the same untrusted
    /// body and fails the same ways.
    /// </summary>
    private async Task RouteConnectionDeliveryAsync(ConnectionWebhook webhook, IWebhookEventNormalizer normalizer, IngestionSubject subject, string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        var identifier = _registry.Require<IWebhookRepositoryIdentifier>(subject.Provider);

        WebhookRepositoryIdentity? identity;

        try
        {
            identity = identifier.Identify(body, headers);
        }
        catch (Exception ex) when (IsUnreadablePayload(ex))
        {
            await AuditMalformedPayloadAsync(subject, headers, ex, cancellationToken).ConfigureAwait(false);
            return;
        }

        // No identity at all is a group-level event — a membership change, a ping. It names no
        // project, so it is the same "nothing here acts on this" the normalizer reports for an
        // event type we do not track, and it is recorded as that rather than as a fault.
        if (identity == null)
        {
            await AuditEventNotMappedAsync(subject, headers, cancellationToken).ConfigureAwait(false);
            return;
        }

        var repositoryId = await MatchBoundRepositoryAsync(webhook.ProviderInstanceId, identity, cancellationToken).ConfigureAwait(false);

        if (repositoryId == null)
        {
            await AuditRepositoryNotBoundAsync(subject, identity, headers, cancellationToken).ConfigureAwait(false);
            return;
        }

        await PublishNormalizedEventAsync(normalizer, subject with { RepositoryId = repositoryId }, body, headers, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Which bound repository this delivery is about, or null when it is none of ours.
    ///
    /// <para>The id is authoritative and EXCLUSIVE: when the payload carries one, a miss is the whole
    /// answer. Falling through to the path on a miss would be a spoofing hole, not a kindness — a
    /// group hook receives events for every project in the group, including ones we have never
    /// bound, so a payload naming an id we do not know could otherwise be matched by path onto a
    /// repository we do. The path is consulted only for payload shapes that carry no id at all.</para>
    /// </summary>
    private async Task<Guid?> MatchBoundRepositoryAsync(Guid providerInstanceId, WebhookRepositoryIdentity identity, CancellationToken cancellationToken)
    {
        if (identity.ExternalId != null) return await FindBoundAsync(providerInstanceId, r => r.ExternalId == identity.ExternalId, cancellationToken).ConfigureAwait(false);
        if (identity.FullPath != null) return await FindBoundAsync(providerInstanceId, r => r.FullPath == identity.FullPath, cancellationToken).ConfigureAwait(false);

        return null;
    }

    private async Task<Guid?> FindBoundAsync(Guid providerInstanceId, Expression<Func<Repository, bool>> match, CancellationToken cancellationToken) =>
        await _db.Repository.AsNoTracking()
            .Where(r => r.ProviderInstanceId == providerInstanceId && r.DeletedDate == null)
            .Where(match)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    private async Task<RepositoryWebhook> LoadWebhookAsync(Guid webhookId, CancellationToken cancellationToken)
    {
        var webhook = await _db.RepositoryWebhook
            .Include(w => w.Repository).ThenInclude(r => r.ProviderInstance)
            .SingleOrDefaultAsync(w => w.Id == webhookId, cancellationToken).ConfigureAwait(false);

        if (webhook == null) throw new InvalidOperationException($"Webhook {webhookId} not found");

        return webhook;
    }

    private async Task<ConnectionWebhook> LoadConnectionWebhookAsync(Guid connectionWebhookId, CancellationToken cancellationToken)
    {
        var webhook = await _db.ConnectionWebhook
            .Include(w => w.ProviderInstance)
            .SingleOrDefaultAsync(w => w.Id == connectionWebhookId, cancellationToken).ConfigureAwait(false);

        if (webhook == null) throw new InvalidOperationException($"Connection webhook {connectionWebhookId} not found");

        return webhook;
    }

    private static IngestionSubject DescribeRepositoryHook(RepositoryWebhook webhook) => new()
    {
        TeamId = webhook.Repository.TeamId,
        WebhookId = webhook.Id,
        Provider = webhook.Repository.ProviderInstance.Provider,
        RepositoryId = webhook.RepositoryId
    };

    private static IngestionSubject DescribeConnectionHook(ConnectionWebhook webhook) => new()
    {
        TeamId = webhook.ProviderInstance.TeamId,
        WebhookId = webhook.Id,
        Provider = webhook.ProviderInstance.Provider,
        RepositoryId = null
    };

    private async Task PublishNormalizedEventAsync(IWebhookEventNormalizer normalizer, IngestionSubject subject, string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        var repositoryId = RequireRepositoryId(subject);

        NormalizedEvent? normalizedEvent;

        try
        {
            normalizedEvent = normalizer.Normalize(repositoryId, body, headers);
        }
        catch (Exception ex) when (IsUnreadablePayload(ex))
        {
            await AuditMalformedPayloadAsync(subject, headers, ex, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (normalizedEvent == null)
        {
            await AuditEventNotMappedAsync(subject, headers, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _mediator.Publish(normalizedEvent, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Every way a reader can fail on an untrusted body: JsonException (not JSON),
    /// KeyNotFoundException (missing GetProperty), InvalidOperationException (GetString/GetBoolean
    /// on the wrong ValueKind), FormatException/OverflowException (GetInt32 on a JSON number that
    /// isn't a valid Int32 — a float, scientific notation, or an out-of-range value). All are
    /// malformed-payload symptoms, not system faults, so they audit + return 200 rather than
    /// escaping as a 500 (which makes providers retry-storm / GitLab auto-disable the webhook).
    /// </summary>
    private static bool IsUnreadablePayload(Exception ex) =>
        ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException;

    /// <summary>
    /// The repository a delivery is FOR, at the point normalization needs it. Every path that gets
    /// here has one — the per-repository hook knows it from its own row, the connection hook from
    /// the payload match that had to succeed first — so an absent id is a broken pipeline, not an
    /// input we should be degrading over.
    /// </summary>
    private static Guid RequireRepositoryId(IngestionSubject subject)
    {
        if (subject.RepositoryId == null)
            throw new InvalidOperationException($"Webhook {subject.WebhookId} reached normalization without a repository");

        return subject.RepositoryId.Value;
    }

    /// <summary>
    /// What the audit writers need to describe a delivery, independent of which table the hook lives
    /// in. Exists so the per-repository and connection-scoped pipelines write the SAME rejection rows
    /// through the same writers — a hook that fails should read the same whichever scope it was in.
    /// </summary>
    private sealed record IngestionSubject
    {
        public required Guid TeamId { get; init; }

        /// <summary>The hook's own id — a <c>repository_webhook</c> row or a <c>connection_webhook</c> one.</summary>
        public required Guid WebhookId { get; init; }

        public required ProviderKind Provider { get; init; }

        /// <summary>
        /// Null until known. The per-repository path knows it from the hook itself; the connection
        /// path only after the payload has been read and matched, and every rejection before that
        /// point is legitimately unattributed.
        /// </summary>
        public required Guid? RepositoryId { get; init; }
    }
}
