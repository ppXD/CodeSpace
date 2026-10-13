using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// The pass behind <see cref="IRetiredSecretShredService"/>: 0249's backfill (<c>0249_shred_retired_secrets.sql</c>), kind
/// for kind and predicate for predicate, run as one conditional UPDATE per kind. Each predicate is the row's retirement —
/// deleted, Revoked, or no longer named — so a live secret is never a candidate, and each matches the relaxed guard that
/// admits the blanking: a revision only once its credential is Revoked, an envelope only while no template names it, a
/// Secret variable's NULL only once it is deleted. Each retirement is terminal — nothing restores a deleted row, revives a
/// Revoked one, or names an envelope again — so a row once a candidate stays one; Postgres re-checks a row's own predicate
/// when a concurrent writer changed it first, and the guards re-check theirs as each row is written.
///
/// <para>What it does not do is what 0249 does not do: a connection an older build deleted keeps its group hooks registered
/// at the provider — they are Cancelled here with their secrets blanked, refused at ingestion, and removed at the provider
/// by hand — and its OAuth grants are not revoked at the provider. Only this build's delete asks the provider, while the
/// tokens it needs still open.</para>
/// </summary>
public sealed class RetiredSecretShredService : IRetiredSecretShredService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ILogger<RetiredSecretShredService> _logger;

    public RetiredSecretShredService(CodeSpaceDbContext db, ILogger<RetiredSecretShredService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<int> ShredAsync(CancellationToken cancellationToken)
    {
        var shredded = await ShredDeletedSecretVariablesAsync(cancellationToken).ConfigureAwait(false);
        shredded += await ShredRevokedStorageCredentialRevisionsAsync(cancellationToken).ConfigureAwait(false);
        shredded += await ShredRetiredGitCredentialsAsync(cancellationToken).ConfigureAwait(false);
        shredded += await ShredDeletedConnectionClientSecretsAsync(cancellationToken).ConfigureAwait(false);
        shredded += await RetireDeletedConnectionWebhooksAsync(cancellationToken).ConfigureAwait(false);
        shredded += await ShredUnnamedStorageDefaultEnvelopesAsync(cancellationToken).ConfigureAwait(false);

        if (shredded > 0) _logger.LogInformation("Shredded {SecretCount} retired secrets that were still decryptable, retired by a build older than migration 0249", shredded);

        return shredded;
    }

    private async Task<int> ShredDeletedSecretVariablesAsync(CancellationToken cancellationToken) =>
        await _db.Variable
            .Where(v => v.DeletedDate != null && v.ValueEncrypted != null)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.ValueEncrypted, (byte[]?)null), cancellationToken)
            .ConfigureAwait(false);

    private async Task<int> ShredRevokedStorageCredentialRevisionsAsync(CancellationToken cancellationToken) =>
        await _db.StorageCredentialRevision
            .Where(r => r.EncryptedPayload != string.Empty && _db.StorageCredential.Any(c => c.TeamId == r.TeamId && c.Id == r.StorageCredentialId && c.State == StorageCredentialState.Revoked))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.EncryptedPayload, string.Empty), cancellationToken)
            .ConfigureAwait(false);

    private async Task<int> ShredRetiredGitCredentialsAsync(CancellationToken cancellationToken) =>
        await _db.Credential
            .Where(c => c.EncryptedPayload != string.Empty && (c.DeletedDate != null || c.Status == CredentialStatus.Revoked))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.EncryptedPayload, string.Empty), cancellationToken)
            .ConfigureAwait(false);

    private async Task<int> ShredDeletedConnectionClientSecretsAsync(CancellationToken cancellationToken) =>
        await _db.ProviderInstance
            .Where(p => p.DeletedDate != null && p.OauthClientSecretEnc != null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.OauthClientSecretEnc, (string?)null), cancellationToken)
            .ConfigureAwait(false);

    /// <summary>A deleted connection's group hooks: out of service and holding no secret, the provider's hook id kept as the record of what to remove there by hand.</summary>
    private async Task<int> RetireDeletedConnectionWebhooksAsync(CancellationToken cancellationToken) =>
        await _db.ConnectionWebhook
            .Where(w => (w.SecretEnc != string.Empty || w.RegistrationStatus != RepositoryWebhookRegistrationStatus.Cancelled) && _db.ProviderInstance.Any(p => p.Id == w.ProviderInstanceId && p.DeletedDate != null))
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.SecretEnc, string.Empty).SetProperty(w => w.RegistrationStatus, RepositoryWebhookRegistrationStatus.Cancelled), cancellationToken)
            .ConfigureAwait(false);

    private async Task<int> ShredUnnamedStorageDefaultEnvelopesAsync(CancellationToken cancellationToken) =>
        await _db.StorageDefaultCredential
            .Where(e => e.EncryptedPayload != string.Empty && !_db.StorageDefault.Any(t => t.CredentialId == e.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.EncryptedPayload, string.Empty), cancellationToken)
            .ConfigureAwait(false);
}
