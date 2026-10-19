using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// What else leans on each listed credential beside its repositories, counted for the whole list in one read apiece — the rest of
/// the answer <see cref="ICredentialRelinkService.GetDependentsAsync"/> gives for one credential, with the same rules (a hook is in
/// service by <see cref="WebhookRegistrationLifecycle.InService"/>, an identity is live until it is soft-deleted), so the list's
/// <c>DependentCount</c> and a removal's refusal never disagree on whether a dead row is history.
/// </summary>
public sealed partial class CredentialService
{
    private async Task<Dictionary<Guid, int>> CountInServiceConnectionWebhooksAsync(List<Guid> credentialIds, CancellationToken cancellationToken) =>
        await _db.ConnectionWebhook.AsNoTracking()
            .Where(w => credentialIds.Contains(w.CredentialId) && WebhookRegistrationLifecycle.InService.Contains(w.RegistrationStatus))
            .GroupBy(w => w.CredentialId)
            .Select(g => new { CredentialId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.CredentialId, g => g.Count, cancellationToken).ConfigureAwait(false);

    private async Task<Dictionary<Guid, int>> CountLiveIdentitiesAsync(List<Guid> credentialIds, CancellationToken cancellationToken) =>
        await _db.UserProviderIdentity.AsNoTracking()
            .Where(i => credentialIds.Contains(i.CredentialId) && i.DeletedDate == null)
            .GroupBy(i => i.CredentialId)
            .Select(g => new { CredentialId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.CredentialId, g => g.Count, cancellationToken).ConfigureAwait(false);
}
