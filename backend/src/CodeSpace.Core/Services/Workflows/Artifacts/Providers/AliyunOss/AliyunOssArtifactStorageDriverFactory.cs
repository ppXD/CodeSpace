using System.Text.Json;
using AlibabaCloud.OSS.V2;
using AlibabaCloud.OSS.V2.Credentials;
using AlibabaCloud.OSS.V2.Transport;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;

/// <summary>
/// Activation entry point for <c>aliyun-oss/v1</c>. It is the only place a plaintext OSS secret is materialized and
/// handed to Alibaba Cloud's official SDK. Configuration is admitted against the module's own schema so a factory
/// rejection and a Settings rejection can never disagree.
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
    public AliyunOssArtifactStorageDriverFactory() : this(() => SharedTransport.Value) { }

    internal AliyunOssArtifactStorageDriverFactory(HttpMessageHandler transport) : this(() => transport) { }

    private AliyunOssArtifactStorageDriverFactory(Func<HttpMessageHandler> transport) => _transport = transport;

    public string ProviderTypeKey => TypeKey;

    public ValueTask<IArtifactStorageDriver> CreateAsync(ArtifactStorageDriverCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var profile = request.Profile ?? throw new ArgumentException("A storage profile snapshot is required.", nameof(request));
        EnsureProfileIdentity(profile);

        var target = AliyunOssTarget.Parse(profile.Configuration);
        var credential = ReadCredential(request.CredentialHandle);
        var client = CreateClient(target, credential);

        return ValueTask.FromResult<IArtifactStorageDriver>(new AliyunOssArtifactStorageDriver(client, target));
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
    /// Copies the handle's secret inside the handle's own lease. Nothing here echoes a value: the schema validator
    /// reports property paths only, so an invalid secret cannot be reconstructed from a failure.
    /// </summary>
    private static AliyunOssSdkCredential ReadCredential(StorageCredentialHandle? handle)
    {
        if (handle == null) throw new ArgumentException($"Storage provider '{TypeKey}' requires an AccessKey credential; OSS has no anonymous write path.", nameof(handle));

        return handle.UseSecret(secret =>
        {
            StorageProviderJson.Validate(secret, AliyunOssStorageProviderModule.SecretSchemaDocument, "Aliyun OSS credential secret", "SecretSchema");
            var accessKeyId = secret.GetProperty("accessKeyId").GetString()!;
            var accessKeySecret = secret.GetProperty("accessKeySecret").GetString()!;
            var securityToken = secret.TryGetProperty("securityToken", out var token) && token.ValueKind == JsonValueKind.String ? token.GetString() : null;
            EnsureNoBoundaryWhitespace(accessKeyId, "accessKeyId");
            EnsureNoBoundaryWhitespace(accessKeySecret, "accessKeySecret");
            if (securityToken != null) EnsureNoBoundaryWhitespace(securityToken, "securityToken");

            return new AliyunOssSdkCredential(accessKeyId, accessKeySecret, securityToken);
        });
    }

    private Client CreateClient(AliyunOssTarget target, AliyunOssSdkCredential credential)
    {
        var http = new HttpClient(_transport(), disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var credentials = credential.SecurityToken == null
            ? new StaticCredentialsProvider(credential.AccessKeyId, credential.AccessKeySecret)
            : new StaticCredentialsProvider(credential.AccessKeyId, credential.AccessKeySecret, credential.SecurityToken);

        return new Client(new Configuration
        {
            Region = target.Region,
            Endpoint = target.Endpoint.AbsoluteUri.TrimEnd('/'),
            CredentialsProvider = credentials,
            HttpTransport = new HttpTransport(http),
            SignatureVersion = "v4",
            RetryMaxAttempts = 3,
            UsePathStyle = false,
            UseCName = false,
            DisableClockSkewCorrection = false,
            DisableAutoDetectMimeType = true
        });
    }

    private static void EnsureNoBoundaryWhitespace(string value, string propertyName)
    {
        if (value.Length != value.Trim().Length)
            throw new ArgumentException($"Aliyun OSS credential property '{propertyName}' cannot start or end with whitespace.", propertyName);
    }

    private sealed record AliyunOssSdkCredential(string AccessKeyId, string AccessKeySecret, string? SecurityToken);
}
