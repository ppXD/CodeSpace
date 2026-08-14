using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Providers.Capabilities;

/// <summary>
/// Remote-side lifecycle for a hook registered ABOVE the repository — a GitLab group hook or a
/// GitHub organization hook, covering every project underneath one owner.
///
/// <para>A sibling of <see cref="IWebhookRegistrationCapability"/>, not a widening of it. The two
/// differ in what they are addressed by (an owner path versus a repository), what they cost (a
/// GitLab group hook is Premium; a project hook is not), and who can grant them (organization
/// admin versus repository admin). A provider can host one and refuse the other — GitLab Free
/// does exactly that — and the registry's TryGet path is what lets the binding flow and the UI
/// find out which, per provider, without a nullable method on a shared interface.</para>
/// </summary>
public interface IConnectionWebhookRegistrationCapability : IProviderCapability
{
    /// <summary>
    /// Lookup an existing hook on the owner by its callback URL. Returns null if no matching hook
    /// exists. Makes registration idempotent for exactly the reasons the repository-scoped twin
    /// does: a background-job retry, or a re-dispatch after the worker died between the provider
    /// call and the DB write, must not leave a second hook on the group.
    /// </summary>
    Task<RemoteWebhook?> FindConnectionWebhookByCallbackUrlAsync(ProviderContext context, string ownerPath, string callbackUrl, CancellationToken cancellationToken);

    Task<RemoteWebhook> RegisterConnectionWebhookAsync(ProviderContext context, string ownerPath, WebhookRegistration request, CancellationToken cancellationToken);

    Task DeleteConnectionWebhookAsync(ProviderContext context, string ownerPath, string externalWebhookId, CancellationToken cancellationToken);
}
