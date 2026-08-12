using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;

namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// Wraps a provider SDK exception (Octokit.ApiException, NGitLab.GitLabException) with
/// the original HTTP status code preserved, so <c>GlobalExceptionFilter</c> can return
/// a real 4xx to the SPA instead of an opaque 500.
///
/// Translated by <c>ExternalCallResilience</c> after the insufficient-scope translation —
/// every non-transient SDK exception that exposes a duck-typed <c>StatusCode</c> gets
/// re-thrown as this type. Handlers and capabilities never raise it directly.
/// </summary>
public sealed class ProviderApiException : Exception, IFailure, IUpstreamStatus
{
    /// <summary>
    /// A provider REJECTING the credential we picked is our configuration problem, not the caller's
    /// request — Unprocessable, so the operator is told to fix the binding rather than shown a 401
    /// that would look like their own session expired. Anything else the provider said is the
    /// provider's answer, and is mirrored.
    /// </summary>
    public FailureKind Kind => IsCredentialRejection ? FailureKind.Unprocessable : FailureKind.Unavailable;

    public string Code => IsCredentialRejection ? FailureCodes.ProviderUnauthorized : FailureCodes.ProviderError;

    public IReadOnlyDictionary<string, object?>? Details => new Dictionary<string, object?> { ["provider"] = ProviderKind.ToString(), ["operation"] = OperationName, ["providerStatus"] = StatusCode };

    int IUpstreamStatus.UpstreamStatus => StatusCode;

    private bool IsCredentialRejection => StatusCode is 401 or 403;

    public ProviderApiException(ProviderKind providerKind, int statusCode, string operationName, string providerMessage, Exception inner)
        : base(BuildMessage(providerKind, statusCode, operationName, providerMessage), inner)
    {
        ProviderKind = providerKind;
        StatusCode = statusCode;
        OperationName = operationName;
        ProviderMessage = providerMessage;
    }

    public ProviderKind ProviderKind { get; }

    /// <summary>Original HTTP status code from the provider — passed straight through to the SPA.</summary>
    public int StatusCode { get; }

    /// <summary>The capability method that failed (e.g. "ListPullRequestsAsync"). Surfaced to the operator for context.</summary>
    public string OperationName { get; }

    /// <summary>Raw provider error string (e.g. Octokit's "Not Found"). Stripped from logs / shown verbatim to operators.</summary>
    public string ProviderMessage { get; }

    private static string BuildMessage(ProviderKind kind, int statusCode, string operation, string providerMessage)
    {
        return $"{kind} returned HTTP {statusCode} for {operation}: {providerMessage}";
    }
}
