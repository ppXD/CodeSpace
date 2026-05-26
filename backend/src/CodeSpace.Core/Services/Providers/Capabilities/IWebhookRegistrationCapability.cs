using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Providers.Capabilities;

/// <summary>
/// Remote-side webhook lifecycle. Providers that cannot host webhooks (e.g. a plain Git
/// remote with no API) simply do not implement this — the registry's TryGet path tells the
/// UI / binding flow that webhooks are not available for that provider.
/// </summary>
public interface IWebhookRegistrationCapability : IProviderCapability
{
    /// <summary>
    /// Lookup an existing webhook on the remote by its callback URL. Returns null if no
    /// matching hook exists. Used by <c>IRepositoryWebhookRegistrar</c> to make the
    /// registration call idempotent: a Hangfire retry of the same registration job (or a
    /// reconciler re-dispatch after the worker crashed between provider call + DB write)
    /// must NOT create a second remote hook pointing at the same callback URL.
    /// </summary>
    Task<RemoteWebhook?> FindWebhookByCallbackUrlAsync(ProviderContext context, RemoteRepository repository, string callbackUrl, CancellationToken cancellationToken);

    Task<RemoteWebhook> RegisterWebhookAsync(ProviderContext context, RemoteRepository repository, WebhookRegistration request, CancellationToken cancellationToken);
    Task DeleteWebhookAsync(ProviderContext context, RemoteRepository repository, string externalWebhookId, CancellationToken cancellationToken);
}
