using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;

namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// Thrown when an operation can't proceed because the credential's granted scopes don't
/// satisfy the capability's scope requirement. Two production paths feed this:
///
///   1. <b>Pre-flight</b> — IScopeChecker compares Credential.Scopes against the provider
///      module's declared CapabilityScopeRequirements BEFORE we hit the wire. Fast-fail with
///      a clear "you need scope X" message.
///   2. <b>Runtime</b> — the provider SDK returns 403/insufficient_scope DURING an operation.
///      ExternalCallResilience's error mapper translates the cryptic SDK exception into this
///      typed exception so GlobalExceptionFilter can return 422 + structured body.
///
/// The frontend branches on <c>code = "oauth_insufficient_scope"</c> to render a clear
/// remediation message naming the exact scope(s) the operator needs to add.
/// </summary>
public sealed class ProviderInsufficientScopeException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.Unprocessable;

    public string Code => FailureCodes.OAuthInsufficientScope;

    public IReadOnlyDictionary<string, object?>? Details => new Dictionary<string, object?> { ["provider"] = ProviderKind.ToString(), ["capability"] = CapabilityName, ["missingScopes"] = MissingScopes, ["grantedScopes"] = GrantedScopes, ["providerHint"] = ProviderHint };

    /// <param name="innerException">
    /// The SDK exception this was mapped from, on the runtime path. Kept because it is the only
    /// carrier of the status and response body the provider actually sent — dropping it made a 403
    /// (the most common webhook-registration failure there is) the one failure we could say least
    /// about. Null on the pre-flight path, where no call was made.
    /// </param>
    public ProviderInsufficientScopeException(ProviderKind providerKind, string capabilityName, IReadOnlyList<string> missingScopes, IReadOnlyList<string>? grantedScopes, string? providerHint = null, Exception? innerException = null)
        : base(BuildMessage(providerKind, capabilityName, missingScopes), innerException)
    {
        ProviderKind = providerKind;
        CapabilityName = capabilityName;
        MissingScopes = missingScopes;
        GrantedScopes = grantedScopes ?? Array.Empty<string>();
        ProviderHint = providerHint;
    }

    public ProviderKind ProviderKind { get; }

    /// <summary>e.g. "IRepositoryCatalogCapability". Surfaced to frontend so it can localise the message.</summary>
    public string CapabilityName { get; }

    public IReadOnlyList<string> MissingScopes { get; }

    public IReadOnlyList<string> GrantedScopes { get; }

    /// <summary>Raw error_description from the provider when available — useful for logs, hidden from end-user.</summary>
    public string? ProviderHint { get; }

    private static string BuildMessage(ProviderKind kind, string capability, IReadOnlyList<string> missingScopes)
    {
        var scopeList = string.Join(", ", missingScopes);
        return $"{kind} credential is missing scope(s) [{scopeList}] required for {capability}.";
    }
}
