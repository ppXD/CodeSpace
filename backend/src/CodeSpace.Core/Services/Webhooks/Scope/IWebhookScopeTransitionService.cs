using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Webhooks.Scope;

/// <summary>
/// Moves a connection from one webhook scope to the other.
///
/// <para>The whole job is an ordering: the outgoing mode's hooks are retired BEFORE the incoming
/// mode's are registered. That order is chosen deliberately and it costs something — between the two
/// steps, events for this connection are not delivered at all, for as long as the registrations
/// take. The alternative costs more. Registering first would mean every event in the overlap arrives
/// twice, once per mode, and a duplicated delivery starts a duplicated workflow run: a second review
/// posted, a second PR opened, a second agent spending tokens on work already done. A missed window
/// is recoverable by whoever pushes again; a duplicated side effect is not.</para>
///
/// <para>Both directions are handled, because leaving the incoming mode unregistered would silently
/// switch the connection off. Switching back to per-repository re-registers a hook for every bound
/// repository, which is exactly what binding them would have done.</para>
/// </summary>
public interface IWebhookScopeTransitionService
{
    /// <summary>
    /// Apply a scope change that has already been written to the connection. Call AFTER the
    /// transaction carrying the new scope commits — the registrations it dispatches read that value.
    /// </summary>
    Task ApplyAsync(Guid providerInstanceId, ProviderWebhookScope previousScope, CancellationToken cancellationToken);
}
