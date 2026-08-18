using System.Net;
using System.Xml.Linq;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;

/// <summary>
/// Translates an OSS failure into the provider-neutral error contract. The response body is read only to lift the
/// fixed <c>&lt;Code&gt;</c> token: a real OSS 403 echoes the access key id, the security token, and the server's own
/// StringToSign, so the body itself must never reach a message, a log, or an API response.
/// </summary>
internal static class AliyunOssErrors
{
    private const int MaxErrorBodyBytes = 8 * 1024;

    private static readonly HashSet<string> CredentialCodes = new(StringComparer.Ordinal)
    {
        "InvalidAccessKeyId", "SignatureDoesNotMatch", "InvalidSecurityToken", "SecurityTokenExpired", "MissingSecurityToken",
    };

    public static async ValueTask<ArtifactStorageError> FromResponseAsync(HttpResponseMessage response, string objectKey, CancellationToken cancellationToken)
    {
        var providerCode = await ReadProviderCodeAsync(response, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        var code = Classify(response.StatusCode, providerCode);

        return new ArtifactStorageError(code, Message(code, objectKey, status, providerCode), IsRetryable(code), providerCode);
    }

    public static ArtifactStorageError Transport(Exception exception, string objectKey) =>
        new(ArtifactStorageErrorCode.Unavailable, $"Aliyun OSS was unreachable for object '{objectKey}': {exception.GetType().Name}.", IsRetryable: true);

    private static ArtifactStorageErrorCode Classify(HttpStatusCode status, string? providerCode) => status switch
    {
        HttpStatusCode.NotFound when providerCode == "NoSuchBucket" => ArtifactStorageErrorCode.Unavailable,
        HttpStatusCode.NotFound => ArtifactStorageErrorCode.Missing,
        HttpStatusCode.Conflict => ArtifactStorageErrorCode.AlreadyExists,
        HttpStatusCode.PreconditionFailed or HttpStatusCode.NotModified => ArtifactStorageErrorCode.ConditionNotMet,
        HttpStatusCode.RequestedRangeNotSatisfiable or HttpStatusCode.BadRequest => ArtifactStorageErrorCode.InvalidRequest,
        HttpStatusCode.Unauthorized => ArtifactStorageErrorCode.Unauthorized,
        HttpStatusCode.Forbidden => providerCode != null && CredentialCodes.Contains(providerCode) ? ArtifactStorageErrorCode.Unauthorized : ArtifactStorageErrorCode.Forbidden,
        HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => ArtifactStorageErrorCode.Throttled,
        >= HttpStatusCode.InternalServerError => ArtifactStorageErrorCode.Unavailable,
        _ => ArtifactStorageErrorCode.ProviderFailure,
    };

    private static bool IsRetryable(ArtifactStorageErrorCode code) => code is ArtifactStorageErrorCode.Throttled or ArtifactStorageErrorCode.Unavailable or ArtifactStorageErrorCode.ProviderFailure;

    private static string Message(ArtifactStorageErrorCode code, string objectKey, int status, string? providerCode) => code == ArtifactStorageErrorCode.Missing
        ? $"Object '{objectKey}' does not exist."
        : $"Aliyun OSS refused object '{objectKey}' with HTTP {status} ({providerCode ?? "no provider code"}).";

    /// <summary>Reads at most a bounded prefix of the body and returns only the fixed error token inside it.</summary>
    private static async ValueTask<string?> ReadProviderCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[MaxErrorBodyBytes];
            var read = await body.ReadAtLeastAsync(buffer, MaxErrorBodyBytes, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            if (read == 0) return null;

            var code = XDocument.Parse(System.Text.Encoding.UTF8.GetString(buffer, 0, read)).Root?.Element("Code")?.Value;
            return string.IsNullOrWhiteSpace(code) || code.Length > 64 || code.Any(character => !char.IsAsciiLetterOrDigit(character)) ? null : code;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }
}
