using System.Globalization;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Profiles;

/// <summary>
/// Reads immutable ledger rows into the driver-facing snapshot contract. Queries select only readiness metadata,
/// non-secret configuration and opaque credential coordinates; encrypted credential payloads never cross this seam.
/// </summary>
public sealed class StorageProfileSnapshotResolver : IStorageProfileSnapshotResolver
{
    private readonly CodeSpaceDbContext _db;
    private readonly IStorageProviderModuleCatalog _moduleCatalog;
    private readonly IArtifactStorageDriverFactoryCatalog _factoryCatalog;

    public StorageProfileSnapshotResolver(CodeSpaceDbContext db, IStorageProviderModuleCatalog moduleCatalog, IArtifactStorageDriverFactoryCatalog factoryCatalog)
    {
        _db = db;
        _moduleCatalog = moduleCatalog;
        _factoryCatalog = factoryCatalog;
    }

    public async Task<StorageProfileSnapshotResolution> ResolveAsync(StorageProfileSnapshotRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TeamId == Guid.Empty) throw new ArgumentException("A team id is required.", nameof(request));
        if (request.ProfileId == Guid.Empty) throw new ArgumentException("A profile id is required.", nameof(request));
        if (request.ProfileRevision <= 0) throw new ArgumentOutOfRangeException(nameof(request), "A positive profile revision is required.");

        var row = await ReadProfileRevisionAsync(request, cancellationToken).ConfigureAwait(false);
        if (row == null) return new StorageProfileSnapshotResolution.Missing();
        if (row.State != StorageProfileState.Active) return new StorageProfileSnapshotResolution.NotActive(row.State);
        if (row.RevisionId == null) return new StorageProfileSnapshotResolution.RevisionMissing();

        var providerTypeKey = row.ProviderTypeKey!;
        var module = _moduleCatalog.Get(providerTypeKey);
        if (module == null || !string.Equals(module.TypeKey, providerTypeKey, StringComparison.Ordinal))
            return new StorageProfileSnapshotResolution.ProviderUnavailable(providerTypeKey, StorageProfileProviderUnavailableReason.ModuleMissing);

        var factory = _factoryCatalog.Get(providerTypeKey);
        if (factory == null || !string.Equals(factory.ProviderTypeKey, providerTypeKey, StringComparison.Ordinal))
            return new StorageProfileSnapshotResolution.ProviderUnavailable(providerTypeKey, StorageProfileProviderUnavailableReason.FactoryMissing);

        if (!StorageProfileSnapshotProjection.TryParseCanonicalConfiguration(row.NonSecretConfigJson!, out var configuration) || !StorageProfileSnapshotProjection.IsValidConfiguration(configuration, module))
            return new StorageProfileSnapshotResolution.Invalid(StorageProfileSnapshotInvalidReason.Configuration);

        StorageSecretReference? secretReference = null;
        if (row.CredentialRef != null)
        {
            if (!StorageProfileRules.TryParseCredentialRef(row.CredentialRef, out var reference))
                return new StorageProfileSnapshotResolution.CredentialInvalid(StorageProfileCredentialInvalidReason.MalformedReference);

            var credential = await ReadCredentialRevisionAsync(request.TeamId, reference, cancellationToken).ConfigureAwait(false);
            if (credential == null) return new StorageProfileSnapshotResolution.CredentialUnavailable(StorageProfileCredentialUnavailableReason.Missing);
            if (credential.State != StorageCredentialState.Active)
                return new StorageProfileSnapshotResolution.CredentialUnavailable(StorageProfileCredentialUnavailableReason.NotActive);
            if (credential.RevisionId == null)
                return new StorageProfileSnapshotResolution.CredentialUnavailable(StorageProfileCredentialUnavailableReason.RevisionMissing);
            if (!string.Equals(credential.ProviderTypeKey, providerTypeKey, StringComparison.Ordinal))
                return new StorageProfileSnapshotResolution.CredentialInvalid(StorageProfileCredentialInvalidReason.ProviderMismatch);

            secretReference = StorageProfileSnapshotProjection.DatabaseSecretReference(reference);
        }

        return new StorageProfileSnapshotResolution.Ready(new StorageProfileSnapshot
        {
            ProfileId = request.ProfileId,
            ProfileRevision = request.ProfileRevision,
            ProviderTypeKey = providerTypeKey,
            Configuration = configuration,
            SecretReference = secretReference,
        });
    }

    private Task<ProfileRevisionRow?> ReadProfileRevisionAsync(StorageProfileSnapshotRequest request, CancellationToken cancellationToken) =>
        (from profile in _db.StorageProfile.AsNoTracking()
         join revision in _db.StorageProfileRevision.AsNoTracking().Where(value => value.Revision == request.ProfileRevision)
             on new { profile.TeamId, StorageProfileId = profile.Id }
             equals new { revision.TeamId, revision.StorageProfileId } into exactRevisions
         from revision in exactRevisions.DefaultIfEmpty()
         where profile.TeamId == request.TeamId && profile.Id == request.ProfileId
         select new ProfileRevisionRow(
             profile.State,
             revision == null ? null : revision.Id,
             revision == null ? null : revision.ProviderTypeKey,
             revision == null ? null : revision.NonSecretConfigJson,
             revision == null ? null : revision.CredentialRef))
        .SingleOrDefaultAsync(cancellationToken);

    private Task<CredentialRevisionRow?> ReadCredentialRevisionAsync(Guid teamId, StorageProfileCredentialReference reference, CancellationToken cancellationToken) =>
        (from credential in _db.StorageCredential.AsNoTracking()
         join revision in _db.StorageCredentialRevision.AsNoTracking().Where(value => value.Revision == reference.Revision)
             on new { credential.TeamId, StorageCredentialId = credential.Id }
             equals new { revision.TeamId, revision.StorageCredentialId } into exactRevisions
         from revision in exactRevisions.DefaultIfEmpty()
         where credential.TeamId == teamId && credential.Id == reference.Id
         select new CredentialRevisionRow(
             credential.State,
             revision == null ? null : revision.Id,
             revision == null ? null : revision.ProviderTypeKey))
        .SingleOrDefaultAsync(cancellationToken);

    private sealed record ProfileRevisionRow(StorageProfileState State, Guid? RevisionId, string? ProviderTypeKey, string? NonSecretConfigJson, string? CredentialRef);
    private sealed record CredentialRevisionRow(StorageCredentialState State, Guid? RevisionId, string? ProviderTypeKey);
}

internal static class StorageProfileSnapshotProjection
{
    public const string DatabaseSecretStoreType = "database/v1";

    public static bool TryParseCanonicalConfiguration(string json, out JsonElement configuration)
    {
        configuration = default;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var canonical = StorageProfileRules.CanonicalJson(document.RootElement);
            using var canonicalDocument = JsonDocument.Parse(canonical);
            configuration = canonicalDocument.RootElement.Clone();
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return false;
        }
    }

    public static bool IsValidConfiguration(JsonElement configuration, IStorageProviderModule module)
    {
        try
        {
            StorageProfileRules.ValidateConfig(configuration, module.ConfigSchema, module.SecretSchema);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static StorageSecretReference DatabaseSecretReference(StorageProfileCredentialReference reference) =>
        new(DatabaseSecretStoreType, reference.Id.ToString("D", CultureInfo.InvariantCulture), reference.Revision.ToString(CultureInfo.InvariantCulture));
}
