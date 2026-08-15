using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Credentials;

/// <summary>
/// Trusted runtime boundary for resolving one exact immutable storage-credential revision. Expected readiness failures
/// are values; implementations must never return encrypted envelopes or silently fall forward to a current revision.
/// </summary>
public interface IStorageCredentialSecretResolver : IScopedDependency
{
    Task<StorageCredentialSecretResolution> ResolveAsync(StorageCredentialSecretRequest request, CancellationToken cancellationToken);
}

/// <summary>An explicit team/credential/revision/provider pin. There is deliberately no implicit current sentinel.</summary>
public sealed record StorageCredentialSecretRequest(Guid TeamId, Guid CredentialId, int Revision, string ExpectedProviderTypeKey);

/// <summary>Closed, fail-closed result vocabulary for runtime secret readiness.</summary>
public abstract record StorageCredentialSecretResolution
{
    private StorageCredentialSecretResolution() { }

    /// <summary>
    /// The decrypted, schema-validated object. The result owns an independent immutable JSON clone and the trusted
    /// caller must dispose it as soon as runtime credential materialization completes.
    /// </summary>
    public sealed record Ready : StorageCredentialSecretResolution, IDisposable
    {
        private readonly object _gate = new();
        private JsonElement _secret;
        private bool _disposed;

        public Ready(JsonElement secret) => _secret = secret.Clone();

        public T UseSecret<T>(Func<JsonElement, T> materialize)
        {
            ArgumentNullException.ThrowIfNull(materialize);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return materialize(_secret);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _secret = default;
                _disposed = true;
            }
        }

        /// <summary>Prevent accidental plaintext disclosure when a caller logs the resolution value.</summary>
        public override string ToString() => "Ready { Secret = [REDACTED] }";
    }
    public sealed record Missing : StorageCredentialSecretResolution;
    public sealed record NotActive(StorageCredentialState State) : StorageCredentialSecretResolution;
    public sealed record RevisionMissing : StorageCredentialSecretResolution;
    public sealed record ProviderMismatch : StorageCredentialSecretResolution;
    public sealed record ProviderUnavailable(StorageCredentialProviderUnavailableReason Reason) : StorageCredentialSecretResolution;
    public sealed record InvalidEnvelope(StorageCredentialEnvelopeInvalidReason Reason) : StorageCredentialSecretResolution;
}

public enum StorageCredentialProviderUnavailableReason
{
    ModuleMissing,
    SecretSchemaInvalid,
}

public enum StorageCredentialEnvelopeInvalidReason
{
    Decryption,
    Json,
    SchemaMismatch,
}
