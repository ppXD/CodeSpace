using System.Diagnostics;
using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local.Legacy;

/// <summary>
/// Factory for the read-only pre-CAS local contract. The configured root is trusted operator infrastructure holding
/// bytes this plane did not place and must never move; object keys are still lexically contained beneath it.
/// </summary>
public sealed class LocalLegacyArtifactStorageDriverFactory : IArtifactStorageDriverFactory
{
    public const string TypeKey = "local-legacy/v1";

    public string ProviderTypeKey => TypeKey;

    public ValueTask<IArtifactStorageDriver> CreateAsync(ArtifactStorageDriverCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var profile = request.Profile ?? throw new ArgumentException("A storage profile snapshot is required.", nameof(request));
        if (profile.SchemaVersion != StorageProfileSnapshot.CurrentSchemaVersion)
            throw new NotSupportedException($"Storage profile schema version '{profile.SchemaVersion}' is not supported by {TypeKey}.");
        if (profile.ProfileId == Guid.Empty || profile.ProfileRevision <= 0)
            throw new ArgumentException("A persisted storage profile identity and positive revision are required.", nameof(request));
        if (!string.Equals(profile.ProviderTypeKey, TypeKey, StringComparison.Ordinal))
            throw new ArgumentException($"Storage profile provider '{profile.ProviderTypeKey}' cannot be opened by factory '{TypeKey}'.", nameof(request));

        var rootPath = LocalLegacyStorageProviderModule.RootPath(profile.Configuration)
            ?? throw new ArgumentException("Local legacy profile configuration requires a non-empty rootPath.", nameof(request));

        return ValueTask.FromResult<IArtifactStorageDriver>(new LocalLegacyArtifactStorageDriver(rootPath));
    }
}

/// <summary>
/// Answers whether a pre-CAS blob is still at its path and streams it for the phase-two re-hash.
///
/// <para>Every mutating arm refuses by construction rather than by configuration. Deleting is refused because these
/// keys carry no team segment — the same bytes are one file for every team that stored them, so an unlink here is a
/// cross-team act (<see cref="IStorageProviderTenantSharedObjectKeys"/>). Writing is refused because nothing places
/// bytes in this layout any more. Whole-object reads are the one additional arm: adoption must observe every byte
/// through the provider-neutral contract before it can mint a sidecar placement. Ordinary artifact reads still use
/// the immutable legacy row's own <c>storage_url</c>.</para>
/// </summary>
internal sealed class LocalLegacyArtifactStorageDriver : IArtifactStorageDriver
{
    private readonly string _root;

    public LocalLegacyArtifactStorageDriver(string root) => _root = Path.GetFullPath(root);

    public StorageProviderCapabilities Capabilities => StorageProviderCapabilities.HealthProbe | StorageProviderCapabilities.StreamingRead;

    public ValueTask<ArtifactStoragePutResult> PutAsync(ArtifactStoragePutRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ValueTask.FromResult(ArtifactStoragePutResult.Failed(Error(ArtifactStorageErrorCode.Unsupported, "The pre-CAS local layout is read-only; nothing places bytes there any more.")));
    }

    public ValueTask<ArtifactStorageHeadResult> HeadAsync(ArtifactStorageHeadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryResolveObjectPath(request.ObjectKey, out var path, out var pathError)) return ValueTask.FromResult(ArtifactStorageHeadResult.Failed(pathError!));

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return ValueTask.FromResult(ArtifactStorageHeadResult.Found(Metadata(request.ObjectKey, stream.Length, File.GetLastWriteTimeUtc(path))));
        }
        catch (FileNotFoundException)
        {
            return ValueTask.FromResult(ArtifactStorageHeadResult.Failed(MissingOrRootFailure(request.ObjectKey)));
        }
        catch (DirectoryNotFoundException)
        {
            return ValueTask.FromResult(ArtifactStorageHeadResult.Failed(MissingOrRootFailure(request.ObjectKey)));
        }
        catch (UnauthorizedAccessException ex)
        {
            return ValueTask.FromResult(ArtifactStorageHeadResult.Failed(Error(ArtifactStorageErrorCode.Forbidden, ex.Message)));
        }
        catch (IOException ex)
        {
            return ValueTask.FromResult(ArtifactStorageHeadResult.Failed(Error(ArtifactStorageErrorCode.ProviderFailure, ex.Message, isRetryable: true)));
        }
    }

    public ValueTask<ArtifactStorageReadResult> OpenReadAsync(ArtifactStorageReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Range != null)
            return ValueTask.FromResult(ArtifactStorageReadResult.Failed(Error(ArtifactStorageErrorCode.Unsupported, "The pre-CAS local layout supports whole-object reads only.")));
        if (request.ExpectedETag != null || request.ExpectedVersion != null)
            return ValueTask.FromResult(ArtifactStorageReadResult.Failed(Error(ArtifactStorageErrorCode.ConditionNotMet, "The pre-CAS local layout reports no conditional object token.")));
        if (!TryResolveObjectPath(request.ObjectKey, out var path, out var pathError)) return ValueTask.FromResult(ArtifactStorageReadResult.Failed(pathError!));

        FileStream? stream = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var metadata = new ArtifactStorageObjectMetadata
            {
                ObjectKey = request.ObjectKey, Length = stream.Length, Sha256 = null, ETag = null, Version = null,
                LastModifiedAt = File.GetLastWriteTimeUtc(path),
            };
            return ValueTask.FromResult(ArtifactStorageReadResult.Opened(stream, stream.Length, stream.Length, metadata));
        }
        catch (FileNotFoundException)
        {
            stream?.Dispose();
            return ValueTask.FromResult(ArtifactStorageReadResult.Failed(MissingOrRootFailure(request.ObjectKey)));
        }
        catch (DirectoryNotFoundException)
        {
            stream?.Dispose();
            return ValueTask.FromResult(ArtifactStorageReadResult.Failed(MissingOrRootFailure(request.ObjectKey)));
        }
        catch (UnauthorizedAccessException ex)
        {
            stream?.Dispose();
            return ValueTask.FromResult(ArtifactStorageReadResult.Failed(Error(ArtifactStorageErrorCode.Forbidden, ex.Message)));
        }
        catch (IOException ex)
        {
            stream?.Dispose();
            return ValueTask.FromResult(ArtifactStorageReadResult.Failed(Error(ArtifactStorageErrorCode.ProviderFailure, ex.Message, isRetryable: true)));
        }
    }

    public ValueTask<ArtifactStorageDeleteResult> DeleteAsync(ArtifactStorageDeleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ValueTask.FromResult(ArtifactStorageDeleteResult.Failed(Error(ArtifactStorageErrorCode.Unsupported, "The pre-CAS local layout keys its objects by digest alone, so one key is shared by every team that stored those bytes; removing one is a cross-team act this plane cannot authorize.")));
    }

    public ValueTask<ArtifactStorageProbeResult> ProbeAsync(ArtifactStorageProbeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();

        var rootFailure = ProbeRoot();
        if (rootFailure != null)
        {
            return ValueTask.FromResult(new ArtifactStorageProbeResult
            {
                Status = ArtifactStorageProbeStatus.Unavailable, Latency = stopwatch.Elapsed,
                Error = rootFailure,
            });
        }

        // Never provisioned, whatever the caller asked for. A root this plane would CREATE is a root the deployment
        // never wrote into, and answering "reachable" for it would tell the verifier that every legacy blob is gone.
        if (request.VerifyWriteAccess)
        {
            return ValueTask.FromResult(new ArtifactStorageProbeResult
            {
                Status = ArtifactStorageProbeStatus.ReadOnly, Latency = stopwatch.Elapsed,
                Error = Error(ArtifactStorageErrorCode.Unsupported, "The pre-CAS local layout is read-only by declaration."),
            });
        }

        return ValueTask.FromResult(new ArtifactStorageProbeResult { Status = ArtifactStorageProbeStatus.Available, Latency = stopwatch.Elapsed });
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private bool TryResolveObjectPath(string objectKey, out string path, out ArtifactStorageError? error)
    {
        path = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(objectKey) || objectKey.IndexOf('\0') >= 0 || Path.IsPathRooted(objectKey))
        {
            error = Error(ArtifactStorageErrorCode.InvalidRequest, "ObjectKey must be a non-empty relative key.");
            return false;
        }

        var segments = objectKey.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            error = Error(ArtifactStorageErrorCode.InvalidRequest, "ObjectKey cannot contain traversal segments.");
            return false;
        }

        path = Path.GetFullPath(Path.Combine([_root, .. segments]));
        var rootPrefix = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            error = Error(ArtifactStorageErrorCode.InvalidRequest, "ObjectKey resolves outside the legacy artifact root.");
            return false;
        }

        return true;
    }

    private static ArtifactStorageObjectMetadata Metadata(string objectKey, long length, DateTimeOffset lastModifiedAt) => new()
    {
        ObjectKey = objectKey,
        Length = length,
        Sha256 = null,
        ETag = null,
        Version = null,
        LastModifiedAt = lastModifiedAt,
    };

    private ArtifactStorageError MissingOrRootFailure(string objectKey) => ProbeRoot() ?? Missing(objectKey);

    private ArtifactStorageError? ProbeRoot()
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(_root).GetEnumerator();
            _ = entries.MoveNext();
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return Error(ArtifactStorageErrorCode.Unavailable, $"Legacy artifact root '{_root}' does not exist.", isRetryable: true);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Error(ArtifactStorageErrorCode.Forbidden, ex.Message);
        }
        catch (IOException ex)
        {
            return Error(ArtifactStorageErrorCode.Unavailable, ex.Message, isRetryable: true);
        }
    }

    private static ArtifactStorageError Missing(string objectKey) => Error(ArtifactStorageErrorCode.Missing, $"Object '{objectKey}' does not exist.");
    private static ArtifactStorageError Error(ArtifactStorageErrorCode code, string message, bool isRetryable = false) => new(code, message, isRetryable);
}
