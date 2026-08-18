using System.Text;
using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;

/// <summary>
/// Activation entry point for <c>aliyun-oss/v1</c>. It is the only place a plaintext OSS secret is ever materialized:
/// the credential handle is read once, projected into signing material, and never retained. Configuration is admitted
/// against the module's own schema so a factory rejection and a Settings rejection can never disagree.
/// </summary>
public sealed class AliyunOssArtifactStorageDriverFactory : IArtifactStorageDriverFactory
{
    public const string TypeKey = "aliyun-oss/v1";

    /// <summary>
    /// Refreshes pooled connections on the cadence <c>IHttpClientFactory</c> exists to provide. The catalog is
    /// auto-activated during container build, which constructs every factory before the ASP.NET service collection's
    /// typed clients are reachable, so this driver owns its transport instead of resolving one.
    /// </summary>
    private static readonly Lazy<SocketsHttpHandler> SharedTransport = new(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        AutomaticDecompression = System.Net.DecompressionMethods.None
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Func<HttpMessageHandler> _transport;
    private readonly TimeProvider _clock;

    public AliyunOssArtifactStorageDriverFactory() : this(() => SharedTransport.Value, TimeProvider.System) { }

    internal AliyunOssArtifactStorageDriverFactory(HttpMessageHandler transport, TimeProvider clock) : this(() => transport, clock) { }

    private AliyunOssArtifactStorageDriverFactory(Func<HttpMessageHandler> transport, TimeProvider clock)
    {
        _transport = transport;
        _clock = clock;
    }

    public string ProviderTypeKey => TypeKey;

    public ValueTask<IArtifactStorageDriver> CreateAsync(ArtifactStorageDriverCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var profile = request.Profile ?? throw new ArgumentException("A storage profile snapshot is required.", nameof(request));
        EnsureProfileIdentity(profile);

        var target = AliyunOssTarget.Parse(profile.Configuration);
        var identity = ReadIdentity(request.CredentialHandle, target.Region);

        return ValueTask.FromResult<IArtifactStorageDriver>(new AliyunOssArtifactStorageDriver(NewHttpClient(), target, identity, _clock));
    }

    private static void EnsureProfileIdentity(StorageProfileSnapshot profile)
    {
        if (profile.SchemaVersion != StorageProfileSnapshot.CurrentSchemaVersion)
            throw new NotSupportedException($"Storage profile schema version '{profile.SchemaVersion}' is not supported by {TypeKey}.");
        if (profile.ProfileId == Guid.Empty || profile.ProfileRevision <= 0)
            throw new ArgumentException("A persisted storage profile identity and positive revision are required.", nameof(profile));
        if (!string.Equals(profile.ProviderTypeKey, TypeKey, StringComparison.Ordinal))
            throw new ArgumentException($"Storage profile provider '{profile.ProviderTypeKey}' cannot be opened by factory '{TypeKey}'.", nameof(profile));
    }

    /// <summary>
    /// Projects the handle's secret into signing material inside the handle's own lease. Nothing here echoes a value:
    /// the schema validator reports property paths only, so an invalid secret cannot be reconstructed from a failure.
    /// </summary>
    private static AliyunOssSigningIdentity ReadIdentity(StorageCredentialHandle? handle, string region)
    {
        if (handle == null) throw new ArgumentException($"Storage provider '{TypeKey}' requires an AccessKey credential; OSS has no anonymous write path.", nameof(handle));

        return handle.UseSecret(secret =>
        {
            StorageProviderJson.Validate(secret, AliyunOssStorageProviderModule.SecretSchemaDocument, "Aliyun OSS credential secret", "SecretSchema");

            return new AliyunOssSigningIdentity
            {
                Region = region,
                AccessKeyId = secret.GetProperty("accessKeyId").GetString()!,
                SigningKeySeed = Encoding.UTF8.GetBytes("aliyun_v4" + secret.GetProperty("accessKeySecret").GetString()),
                SecurityToken = secret.TryGetProperty("securityToken", out var token) && token.ValueKind == JsonValueKind.String ? token.GetString() : null
            };
        });
    }

    /// <summary>Artifacts stream in both directions, so the per-request deadline belongs to the caller's token, not the client.</summary>
    private HttpClient NewHttpClient() => new(_transport(), disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
}
