using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Webhooks;

/// <summary>A primary-key probe of the table the scope names. Inactive and retired hooks exist: their refusal needs the delivery's headers, so ingestion makes it.</summary>
public sealed class WebhookLookup : IWebhookLookup, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public WebhookLookup(CodeSpaceDbContext db) { _db = db; }

    public async Task<bool> ExistsAsync(ProviderWebhookScope scope, Guid webhookId, CancellationToken cancellationToken) => scope == ProviderWebhookScope.Connection
        ? await _db.ConnectionWebhook.AsNoTracking().AnyAsync(w => w.Id == webhookId, cancellationToken).ConfigureAwait(false)
        : await _db.RepositoryWebhook.AsNoTracking().AnyAsync(w => w.Id == webhookId, cancellationToken).ConfigureAwait(false);
}
