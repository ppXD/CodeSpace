using System.Globalization;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Artifacts.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

public sealed class StorageRuntimeDriverBroker : IStorageRuntimeDriverBroker
{
    private const string DatabaseSecretStoreType = "database/v1";
    private readonly IStorageProfileSnapshotResolver _profileResolver;
    private readonly IStorageCredentialSecretResolver _credentialResolver;
    private readonly IArtifactStorageDriverFactoryCatalog _factoryCatalog;

    public StorageRuntimeDriverBroker(IStorageProfileSnapshotResolver profileResolver, IStorageCredentialSecretResolver credentialResolver, IArtifactStorageDriverFactoryCatalog factoryCatalog)
    {
        _profileResolver = profileResolver;
        _credentialResolver = credentialResolver;
        _factoryCatalog = factoryCatalog;
    }

    public async ValueTask<StorageRuntimeDriverResolution> OpenAsync(StorageRuntimeDriverRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TeamId == Guid.Empty) throw new ArgumentException("A team id is required.", nameof(request));
        if (request.ProfileId == Guid.Empty) throw new ArgumentException("A profile id is required.", nameof(request));
        if (request.ProfileRevision <= 0) throw new ArgumentOutOfRangeException(nameof(request), "A positive profile revision is required.");
        if (cancellationToken.IsCancellationRequested) return new StorageRuntimeDriverResolution.Cancelled(StorageRuntimeCancellationStage.ProfileResolution);

        StorageProfileSnapshotResolution profileResolution;
        try
        {
            profileResolution = await _profileResolver.ResolveAsync(new StorageProfileSnapshotRequest(request.TeamId, request.ProfileId, request.ProfileRevision), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new StorageRuntimeDriverResolution.Cancelled(StorageRuntimeCancellationStage.ProfileResolution);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return new StorageRuntimeDriverResolution.ProfileUnavailable(StorageRuntimeProfileFailureReason.ResolutionFailed);
        }

        if (cancellationToken.IsCancellationRequested) return new StorageRuntimeDriverResolution.Cancelled(StorageRuntimeCancellationStage.ProfileResolution);
        if (profileResolution is not StorageProfileSnapshotResolution.Ready profileReady) return MapProfileFailure(profileResolution);

        var snapshotFailure = ValidateSnapshot(request, profileReady.Snapshot);
        if (snapshotFailure != null) return snapshotFailure;
        var snapshot = profileReady.Snapshot;

        IArtifactStorageDriverFactory? factory;
        try
        {
            factory = _factoryCatalog.Get(snapshot.ProviderTypeKey);
            if (factory == null) return new StorageRuntimeDriverResolution.ProviderUnavailable(StorageRuntimeProviderFailureReason.FactoryMissing);
            if (!string.Equals(factory.ProviderTypeKey, snapshot.ProviderTypeKey, StringComparison.Ordinal))
                return new StorageRuntimeDriverResolution.ProviderUnavailable(StorageRuntimeProviderFailureReason.FactoryMismatch);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return new StorageRuntimeDriverResolution.ProviderUnavailable(StorageRuntimeProviderFailureReason.CatalogFailure);
        }

        StorageCredentialHandle? credentialHandle = null;
        if (snapshot.SecretReference != null)
        {
            if (!TryParseDatabaseReference(snapshot.SecretReference, out var credentialId, out var credentialRevision))
                return new StorageRuntimeDriverResolution.CredentialUnavailable(StorageRuntimeCredentialFailureReason.InvalidReference);

            StorageCredentialSecretResolution credentialResolution;
            try
            {
                credentialResolution = await _credentialResolver.ResolveAsync(new StorageCredentialSecretRequest(request.TeamId, credentialId, credentialRevision, snapshot.ProviderTypeKey), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new StorageRuntimeDriverResolution.Cancelled(StorageRuntimeCancellationStage.CredentialResolution);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                return new StorageRuntimeDriverResolution.CredentialUnavailable(StorageRuntimeCredentialFailureReason.ResolutionFailed);
            }

            if (cancellationToken.IsCancellationRequested) return new StorageRuntimeDriverResolution.Cancelled(StorageRuntimeCancellationStage.CredentialResolution);
            if (credentialResolution is not StorageCredentialSecretResolution.Ready credentialReady) return MapCredentialFailure(credentialResolution);
            using (credentialReady)
            {
                if (credentialReady.UseSecret(secret => secret.ValueKind) != JsonValueKind.Object)
                    return new StorageRuntimeDriverResolution.CredentialUnavailable(StorageRuntimeCredentialFailureReason.InvalidSecret);
                credentialHandle = credentialReady.UseSecret(secret => new StorageCredentialHandle(secret));
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            credentialHandle?.Dispose();
            return new StorageRuntimeDriverResolution.Cancelled(StorageRuntimeCancellationStage.DriverInitialization);
        }

        IArtifactStorageDriver? driver;
        try
        {
            driver = await factory.CreateAsync(new ArtifactStorageDriverCreateRequest(snapshot) { CredentialHandle = credentialHandle }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new StorageRuntimeDriverResolution.Cancelled(StorageRuntimeCancellationStage.DriverInitialization);
        }
        catch (OperationCanceledException)
        {
            return new StorageRuntimeDriverResolution.DriverInitializationFailed(StorageRuntimeDriverInitializationFailureReason.ProviderCanceled);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return new StorageRuntimeDriverResolution.ConfigurationInvalid(StorageRuntimeConfigurationFailureReason.FactoryRejectedConfiguration);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return new StorageRuntimeDriverResolution.DriverInitializationFailed(StorageRuntimeDriverInitializationFailureReason.ProviderFailure);
        }
        finally
        {
            credentialHandle?.Dispose();
        }

        if (driver == null) return new StorageRuntimeDriverResolution.DriverInitializationFailed(StorageRuntimeDriverInitializationFailureReason.NullDriver);
        if (cancellationToken.IsCancellationRequested) return await DisposeCancelledDriverAsync(driver).ConfigureAwait(false);
        return new StorageRuntimeDriverResolution.Ready(new StorageRuntimeDriverLease(driver));
    }

    private static StorageRuntimeDriverResolution? ValidateSnapshot(StorageRuntimeDriverRequest request, StorageProfileSnapshot? snapshot)
    {
        if (snapshot == null || snapshot.ProfileId != request.ProfileId || snapshot.ProfileRevision != request.ProfileRevision)
            return new StorageRuntimeDriverResolution.ConfigurationInvalid(StorageRuntimeConfigurationFailureReason.SnapshotIdentityMismatch);
        if (snapshot.SchemaVersion != StorageProfileSnapshot.CurrentSchemaVersion)
            return new StorageRuntimeDriverResolution.ConfigurationInvalid(StorageRuntimeConfigurationFailureReason.UnsupportedSchemaVersion);
        if (string.IsNullOrWhiteSpace(snapshot.ProviderTypeKey))
            return new StorageRuntimeDriverResolution.ConfigurationInvalid(StorageRuntimeConfigurationFailureReason.InvalidProviderTypeKey);
        if (snapshot.Configuration.ValueKind != JsonValueKind.Object)
            return new StorageRuntimeDriverResolution.ConfigurationInvalid(StorageRuntimeConfigurationFailureReason.InvalidConfiguration);
        return null;
    }

    private static StorageRuntimeDriverResolution MapProfileFailure(StorageProfileSnapshotResolution? resolution) => resolution switch
    {
        StorageProfileSnapshotResolution.Missing => new StorageRuntimeDriverResolution.ProfileUnavailable(StorageRuntimeProfileFailureReason.Missing),
        StorageProfileSnapshotResolution.NotActive => new StorageRuntimeDriverResolution.ProfileUnavailable(StorageRuntimeProfileFailureReason.NotActive),
        StorageProfileSnapshotResolution.RevisionMissing => new StorageRuntimeDriverResolution.ProfileUnavailable(StorageRuntimeProfileFailureReason.RevisionMissing),
        StorageProfileSnapshotResolution.ProviderUnavailable { Reason: StorageProfileProviderUnavailableReason.ModuleMissing } => new StorageRuntimeDriverResolution.ProviderUnavailable(StorageRuntimeProviderFailureReason.ModuleMissing),
        StorageProfileSnapshotResolution.ProviderUnavailable => new StorageRuntimeDriverResolution.ProviderUnavailable(StorageRuntimeProviderFailureReason.FactoryMissing),
        StorageProfileSnapshotResolution.Invalid => new StorageRuntimeDriverResolution.ConfigurationInvalid(StorageRuntimeConfigurationFailureReason.InvalidConfiguration),
        StorageProfileSnapshotResolution.CredentialUnavailable { Reason: StorageProfileCredentialUnavailableReason.Missing } => new StorageRuntimeDriverResolution.CredentialUnavailable(StorageRuntimeCredentialFailureReason.Missing),
        StorageProfileSnapshotResolution.CredentialUnavailable { Reason: StorageProfileCredentialUnavailableReason.NotActive } => new StorageRuntimeDriverResolution.CredentialUnavailable(StorageRuntimeCredentialFailureReason.NotActive),
        StorageProfileSnapshotResolution.CredentialUnavailable => new StorageRuntimeDriverResolution.CredentialUnavailable(StorageRuntimeCredentialFailureReason.RevisionMissing),
        StorageProfileSnapshotResolution.CredentialInvalid { Reason: StorageProfileCredentialInvalidReason.ProviderMismatch } => new StorageRuntimeDriverResolution.CredentialUnavailable(StorageRuntimeCredentialFailureReason.ProviderMismatch),
        StorageProfileSnapshotResolution.CredentialInvalid => new StorageRuntimeDriverResolution.CredentialUnavailable(StorageRuntimeCredentialFailureReason.InvalidReference),
        _ => new StorageRuntimeDriverResolution.ProfileUnavailable(StorageRuntimeProfileFailureReason.ResolutionFailed),
    };

    private static StorageRuntimeDriverResolution MapCredentialFailure(StorageCredentialSecretResolution? resolution) => resolution switch
    {
        StorageCredentialSecretResolution.Missing => new StorageRuntimeDriverResolution.CredentialUnavailable(StorageRuntimeCredentialFailureReason.Missing),
        StorageCredentialSecretResolution.NotActive => new StorageRuntimeDriverResolution.CredentialUnavailable(StorageRuntimeCredentialFailureReason.NotActive),
        StorageCredentialSecretResolution.RevisionMissing => new StorageRuntimeDriverResolution.CredentialUnavailable(StorageRuntimeCredentialFailureReason.RevisionMissing),
        StorageCredentialSecretResolution.ProviderMismatch => new StorageRuntimeDriverResolution.CredentialUnavailable(StorageRuntimeCredentialFailureReason.ProviderMismatch),
        StorageCredentialSecretResolution.ProviderUnavailable => new StorageRuntimeDriverResolution.CredentialUnavailable(StorageRuntimeCredentialFailureReason.ProviderUnavailable),
        StorageCredentialSecretResolution.InvalidEnvelope => new StorageRuntimeDriverResolution.CredentialUnavailable(StorageRuntimeCredentialFailureReason.InvalidEnvelope),
        _ => new StorageRuntimeDriverResolution.CredentialUnavailable(StorageRuntimeCredentialFailureReason.ResolutionFailed),
    };

    private static bool TryParseDatabaseReference(StorageSecretReference reference, out Guid credentialId, out int revision)
    {
        credentialId = default;
        revision = default;
        return string.Equals(reference.SecretStoreType, DatabaseSecretStoreType, StringComparison.Ordinal)
            && Guid.TryParseExact(reference.SecretId, "D", out credentialId) && credentialId != Guid.Empty
            && string.Equals(reference.SecretId, credentialId.ToString("D", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            && int.TryParse(reference.SecretVersion, NumberStyles.None, CultureInfo.InvariantCulture, out revision) && revision > 0
            && string.Equals(reference.SecretVersion, revision.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static async ValueTask<StorageRuntimeDriverResolution> DisposeCancelledDriverAsync(IArtifactStorageDriver driver)
    {
        try
        {
            await driver.DisposeAsync().ConfigureAwait(false);
            return new StorageRuntimeDriverResolution.Cancelled(StorageRuntimeCancellationStage.DriverInitialization);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return new StorageRuntimeDriverResolution.DriverInitializationFailed(StorageRuntimeDriverInitializationFailureReason.CleanupFailure);
        }
    }

    private static bool IsRecoverable(Exception exception) => exception is not OutOfMemoryException and not AccessViolationException;
}
