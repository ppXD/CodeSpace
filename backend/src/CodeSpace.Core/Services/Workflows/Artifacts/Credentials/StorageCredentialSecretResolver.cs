using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Credentials;

/// <summary>
/// Resolves a pinned team-owned encrypted revision for trusted runtime consumers. The database projection is scoped by
/// team before any envelope reaches this process; foreign identities are indistinguishable from missing identities.
/// </summary>
public sealed class StorageCredentialSecretResolver : IStorageCredentialSecretResolver
{
    private readonly CodeSpaceDbContext _db;
    private readonly IStorageProviderModuleCatalog _catalog;
    private readonly IPayloadEncryptor _encryptor;

    public StorageCredentialSecretResolver(CodeSpaceDbContext db, IStorageProviderModuleCatalog catalog, IPayloadEncryptor encryptor)
    {
        _db = db;
        _catalog = catalog;
        _encryptor = encryptor;
    }

    public async Task<StorageCredentialSecretResolution> ResolveAsync(StorageCredentialSecretRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TeamId == Guid.Empty) throw new ArgumentException("A team id is required.", nameof(request));
        if (request.CredentialId == Guid.Empty) throw new ArgumentException("A credential id is required.", nameof(request));
        if (request.Revision <= 0) throw new ArgumentOutOfRangeException(nameof(request), "A positive credential revision is required.");
        if (string.IsNullOrWhiteSpace(request.ExpectedProviderTypeKey)) throw new ArgumentException("An expected provider type key is required.", nameof(request));
        cancellationToken.ThrowIfCancellationRequested();

        var row = await ReadRevisionAsync(request, cancellationToken).ConfigureAwait(false);
        if (row == null) return new StorageCredentialSecretResolution.Missing();
        if (row.State != StorageCredentialState.Active) return new StorageCredentialSecretResolution.NotActive(row.State);
        if (row.RevisionId == null) return new StorageCredentialSecretResolution.RevisionMissing();
        if (!string.Equals(row.ProviderTypeKey, request.ExpectedProviderTypeKey, StringComparison.Ordinal)) return new StorageCredentialSecretResolution.ProviderMismatch();

        var module = GetModule(request.ExpectedProviderTypeKey);
        if (module == null || !string.Equals(module.TypeKey, request.ExpectedProviderTypeKey, StringComparison.Ordinal))
            return new StorageCredentialSecretResolution.ProviderUnavailable(StorageCredentialProviderUnavailableReason.ModuleMissing);
        if (!TryGetValidSecretSchema(module, out var secretSchema))
            return new StorageCredentialSecretResolution.ProviderUnavailable(StorageCredentialProviderUnavailableReason.SecretSchemaInvalid);

        cancellationToken.ThrowIfCancellationRequested();
        if (!TryDecrypt(row.EncryptedPayload!, out var plaintext))
            return new StorageCredentialSecretResolution.InvalidEnvelope(StorageCredentialEnvelopeInvalidReason.Decryption);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryParseObject(plaintext, out var secret))
            return new StorageCredentialSecretResolution.InvalidEnvelope(StorageCredentialEnvelopeInvalidReason.Json);
        if (!MatchesSecretSchema(secret, secretSchema))
            return new StorageCredentialSecretResolution.InvalidEnvelope(StorageCredentialEnvelopeInvalidReason.SchemaMismatch);
        cancellationToken.ThrowIfCancellationRequested();

        return new StorageCredentialSecretResolution.Ready(secret);
    }

    private Task<CredentialRevisionRow?> ReadRevisionAsync(StorageCredentialSecretRequest request, CancellationToken cancellationToken) =>
        (from credential in _db.StorageCredential.AsNoTracking()
         join revision in _db.StorageCredentialRevision.AsNoTracking().Where(value => value.Revision == request.Revision)
             on new { credential.TeamId, StorageCredentialId = credential.Id }
             equals new { revision.TeamId, revision.StorageCredentialId } into exactRevisions
         from revision in exactRevisions.DefaultIfEmpty()
         where credential.TeamId == request.TeamId && credential.Id == request.CredentialId
         select new CredentialRevisionRow(
             credential.State,
             revision == null ? null : revision.Id,
             revision == null ? null : revision.ProviderTypeKey,
             revision == null ? null : revision.EncryptedPayload))
        .SingleOrDefaultAsync(cancellationToken);

    private IStorageProviderModule? GetModule(string providerTypeKey)
    {
        try { return _catalog.Get(providerTypeKey); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException) { return null; }
    }

    private static bool TryGetValidSecretSchema(IStorageProviderModule module, out JsonElement secretSchema)
    {
        secretSchema = default;
        try
        {
            var candidate = module.SecretSchema;
            if (candidate.ValueKind != JsonValueKind.Object) return false;
            StorageProviderJson.ValidateSchema(candidate, nameof(IStorageProviderModule.SecretSchema));
            secretSchema = candidate.Clone();
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private bool TryDecrypt(string encryptedPayload, out string plaintext)
    {
        try
        {
            plaintext = _encryptor.Decrypt(encryptedPayload);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            plaintext = string.Empty;
            return false;
        }
    }

    private static bool TryParseObject(string plaintext, out JsonElement secret)
    {
        secret = default;
        try
        {
            using var document = JsonDocument.Parse(plaintext);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            secret = document.RootElement.Clone();
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static bool MatchesSecretSchema(JsonElement secret, JsonElement schema)
    {
        try
        {
            StorageProviderJson.Validate(secret, schema, "Secret");
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed record CredentialRevisionRow(StorageCredentialState State, Guid? RevisionId, string? ProviderTypeKey, string? EncryptedPayload);
}
