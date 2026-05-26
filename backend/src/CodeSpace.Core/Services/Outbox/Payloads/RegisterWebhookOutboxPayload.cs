namespace CodeSpace.Core.Services.Outbox.Payloads;

/// <summary>
/// JSON shape for OutboxMessageTypes.RegisterWebhook. The bind flow pre-allocates the
/// webhook id + secret so the callback URL is known before the remote call, then the
/// dispatcher persists the RepositoryWebhook row only after the remote registration
/// succeeds. Plain-text secret here is acceptable for v1 (DB is trust boundary); future
/// hardening can wrap this payload in IPayloadEncryptor.
/// </summary>
public sealed record RegisterWebhookOutboxPayload
{
    public required Guid WebhookId { get; init; }
    public required Guid RepositoryId { get; init; }
    public required string CallbackUrl { get; init; }
    public required string Secret { get; init; }
    public required IReadOnlyList<string> SubscribedEvents { get; init; }
}
