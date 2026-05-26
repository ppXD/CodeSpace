using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Providers.Capabilities;

/// <summary>
/// Remote-side webhook lifecycle. Providers that cannot host webhooks (e.g. a plain Git
/// remote with no API) simply do not implement this — the registry's TryGet path tells the
/// UI / binding flow that webhooks are not available for that provider.
/// </summary>
public interface IWebhookRegistrationCapability : IProviderCapability
{
    Task<RemoteWebhook> RegisterWebhookAsync(ProviderContext context, RemoteRepository repository, WebhookRegistration request, CancellationToken cancellationToken);
    Task DeleteWebhookAsync(ProviderContext context, RemoteRepository repository, string externalWebhookId, CancellationToken cancellationToken);
}
