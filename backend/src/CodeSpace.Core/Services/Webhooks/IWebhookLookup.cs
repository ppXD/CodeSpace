using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Webhooks;

/// <summary>Whether a hook id names a hook — asked before a delivery's body is read, so a body posted to an id naming nothing never is.</summary>
public interface IWebhookLookup
{
    Task<bool> ExistsAsync(ProviderWebhookScope scope, Guid webhookId, CancellationToken cancellationToken);
}
