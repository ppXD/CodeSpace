using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.OAuth;

/// <summary>
/// Thrown when an OAuth provider rejects a code-exchange or refresh request. Wraps the
/// provider's RFC 6749 error code + description for diagnostics. NEVER includes raw secrets
/// (client_secret, refresh_token, etc.) in the message.
///
/// <para>A refusal of a REFRESH that names the grant or the client (<see cref="IsFinalRefusal"/>) is a verdict, not a
/// hiccup: the refresher turns it into <c>CredentialNeedsReconnectException</c> and marks the credential. Anything else — an
/// HTTP error with no OAuth body, a provider that is down, a malformed answer — says nothing about the grant and is left as
/// it is.</para>
/// </summary>
public sealed class OAuthExchangeException : Exception, IFailure
{
    /// <summary>
    /// The RFC 6749 §5.2 error codes (and GitHub's own spellings of them) that refuse THIS grant or THIS client — the ones a
    /// retry can never turn into a success: the refresh token is unknown, used, revoked or expired, or the app's id or secret
    /// is not the one the grant was issued to. Everything else (<c>http_503</c>, <c>temporarily_unavailable</c>,
    /// <c>server_error</c>, <c>invalid_request</c>, <c>malformed_response</c>, an unknown code) is not a judgement of the
    /// grant: a malformed or unavailable exchange is not a dead credential. Pinned by test.
    /// </summary>
    internal static readonly IReadOnlySet<string> FinalRefusalErrors = new HashSet<string>(StringComparer.Ordinal)
    {
        "invalid_grant", "invalid_client", "unauthorized_client", "bad_refresh_token", "incorrect_client_credentials"
    };

    public FailureKind Kind => FailureKind.Invalid;

    public string Code => FailureCodes.OAuthExchangeFailed;


    public OAuthExchangeException(string error, string? description, string? providerBody)
        : base($"OAuth exchange rejected: {error}{(description != null ? $" — {description}" : string.Empty)}")
    {
        Error = error;
        Description = description;
        ProviderBody = providerBody;
    }

    private OAuthExchangeException(OAuthExchangeException original, Guid credentialId)
        : base($"{original.Message} (credential {credentialId})", original)
    {
        Error = original.Error;
        Description = original.Description;
        ProviderBody = original.ProviderBody;
    }

    public string Error { get; }
    public string? Description { get; }
    public string? ProviderBody { get; }

    /// <summary>True when the provider's answer refuses the grant or the client for good (<see cref="FinalRefusalErrors"/>).</summary>
    public bool IsFinalRefusal => FinalRefusalErrors.Contains(Error);

    /// <summary>The same refusal, its text naming the credential whose refresh it answered — the provider's answer carries no credential, and an operator reading a run's error cannot tell which row to look at without one.</summary>
    public OAuthExchangeException ForCredential(Guid credentialId) => new(this, credentialId);
}
