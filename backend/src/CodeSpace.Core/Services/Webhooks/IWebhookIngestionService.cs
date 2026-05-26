namespace CodeSpace.Core.Services.Webhooks;

/// <summary>
/// Single ingress for an inbound webhook delivery. Looks up the registered webhook,
/// verifies the provider's signature against the stored secret, normalises the
/// payload into a domain-event notification, and publishes it via Mediator.
///
/// Publishing notifications via IMediator IS permitted from a service (output
/// dispatch) — Rule 16's no-Mediator-coupling principle covers input (handlers
/// must not be invoked through Mediator beyond the request shape itself).
/// </summary>
public interface IWebhookIngestionService
{
    Task IngestAsync(Guid webhookId, string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken);
}
