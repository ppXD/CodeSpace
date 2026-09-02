using System.Net;
using AlibabaCloud.OSS.V2;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;

/// <summary>
/// Translates official-SDK exceptions into the provider-neutral error contract. Only the SDK's bounded status,
/// error-code, and request-id fields are considered; exception messages, response bodies, StringToSign, access-key
/// echoes, and snapshots never cross this boundary.
/// </summary>
internal static class AliyunOssErrors
{
    private static readonly HashSet<string> CredentialCodes = new(StringComparer.Ordinal)
    {
        "InvalidAccessKeyId", "SignatureDoesNotMatch", "InvalidSecurityToken", "SecurityTokenExpired", "MissingSecurityToken",
    };

    public static ArtifactStorageError FromException(Exception exception, string objectKey)
    {
        var service = FindServiceException(exception);
        if (service == null) return NonService(exception, objectKey);

        var providerCode = SafeProviderCode(service.ErrorCode);
        var status = (HttpStatusCode)service.StatusCode;
        var code = Classify(status, providerCode);
        return WithReason(new ArtifactStorageError(code, Message(code, objectKey, service.StatusCode, providerCode), IsRetryable(code, providerCode), providerCode), Reason(providerCode));
    }

    public static bool IsOperational(Exception exception) => Find(exception, candidate => candidate is ServiceException or RequestFailedException or RequestTimeoutException or InconsistentException or NoRetryableInconsistentException) != null;

    public static bool IsCallerCancellation(Exception exception, CancellationToken cancellationToken) => cancellationToken.IsCancellationRequested
        && Find(exception, candidate => candidate is OperationCanceledException) != null
        && Find(exception, candidate => candidate is RequestTimeoutException) == null;

    /// <summary>
    /// Re-runs an unattributed HEAD verdict with the fixed token returned by a body-carrying request against the same
    /// bucket. It only sharpens the one durable namespace answer; all other corroborating responses leave the
    /// object's own verdict untouched.
    /// </summary>
    public static ArtifactStorageError Reclassify(ArtifactStorageError error, string objectKey, string? providerCode)
    {
        if (providerCode == "NoSuchBucket" && error.Code == ArtifactStorageErrorCode.Missing)
            return WithReason(new ArtifactStorageError(ArtifactStorageErrorCode.Unavailable, Message(ArtifactStorageErrorCode.Unavailable, objectKey, 404, providerCode), false, providerCode), ArtifactStorageFailureReason.DestinationMissing);
        if (providerCode != null && CredentialCodes.Contains(providerCode) && error.Code == ArtifactStorageErrorCode.Forbidden)
            return WithReason(new ArtifactStorageError(ArtifactStorageErrorCode.Unauthorized, Message(ArtifactStorageErrorCode.Unauthorized, objectKey, 403, providerCode), false, providerCode), Reason(providerCode));

        return error;
    }

    private static ServiceException? FindServiceException(Exception? exception)
    {
        while (exception != null)
        {
            if (exception is ServiceException service) return service;
            exception = exception.InnerException;
        }

        return null;
    }

    private static ArtifactStorageError NonService(Exception exception, string objectKey)
    {
        var inconsistent = Find(exception, candidate => candidate is InconsistentException or NoRetryableInconsistentException);
        if (inconsistent != null)
            return new ArtifactStorageError(ArtifactStorageErrorCode.IntegrityMismatch, $"Aliyun OSS reported an integrity mismatch for object '{objectKey}'.", inconsistent is InconsistentException);

        return WithReason(new ArtifactStorageError(ArtifactStorageErrorCode.Unavailable, $"Aliyun OSS was unreachable for object '{objectKey}'.", IsRetryable: true), ArtifactStorageFailureReason.NetworkUnavailable);
    }

    private static Exception? Find(Exception? exception, Func<Exception, bool> predicate)
    {
        while (exception != null)
        {
            if (predicate(exception)) return exception;
            exception = exception.InnerException;
        }

        return null;
    }

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

    private static bool IsRetryable(ArtifactStorageErrorCode code, string? providerCode) =>
        providerCode != "NoSuchBucket" && code is ArtifactStorageErrorCode.Throttled or ArtifactStorageErrorCode.Unavailable or ArtifactStorageErrorCode.ProviderFailure;

    private static ArtifactStorageFailureReason? Reason(string? providerCode) => providerCode switch
    {
        "InvalidAccessKeyId" => ArtifactStorageFailureReason.CredentialInvalid,
        "SignatureDoesNotMatch" => ArtifactStorageFailureReason.SignatureMismatch,
        "InvalidSecurityToken" => ArtifactStorageFailureReason.SecurityTokenInvalid,
        "SecurityTokenExpired" => ArtifactStorageFailureReason.SecurityTokenExpired,
        "MissingSecurityToken" => ArtifactStorageFailureReason.SecurityTokenMissing,
        "RequestTimeTooSkewed" or "RequestNotYetValid" => ArtifactStorageFailureReason.ClockSkew,
        "NoSuchBucket" => ArtifactStorageFailureReason.DestinationMissing,
        "AccessDenied" => ArtifactStorageFailureReason.PermissionDenied,
        _ => null,
    };

    private static ArtifactStorageError WithReason(ArtifactStorageError error, ArtifactStorageFailureReason? reason) => error with { Reason = reason };

    private static string Message(ArtifactStorageErrorCode code, string objectKey, int status, string? providerCode) => code == ArtifactStorageErrorCode.Missing
        ? $"Object '{objectKey}' does not exist."
        : $"Aliyun OSS refused object '{objectKey}' with HTTP {status} ({providerCode ?? "no provider code"}).";

    private static string? SafeProviderCode(string? value) => value != "BadErrorResponse" && !string.IsNullOrWhiteSpace(value) && value.Length <= 64 && value.All(char.IsAsciiLetterOrDigit) ? value : null;
}
