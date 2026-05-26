namespace CodeSpace.Core.Services.OAuth;

/// <summary>
/// Thrown when an OAuth provider rejects a code-exchange or refresh request. Wraps the
/// provider's RFC 6749 error code + description for diagnostics. NEVER includes raw secrets
/// (client_secret, refresh_token, etc.) in the message.
/// </summary>
public sealed class OAuthExchangeException : Exception
{
    public OAuthExchangeException(string error, string? description, string? providerBody)
        : base($"OAuth exchange rejected: {error}{(description != null ? $" — {description}" : string.Empty)}")
    {
        Error = error;
        Description = description;
        ProviderBody = providerBody;
    }

    public string Error { get; }
    public string? Description { get; }
    public string? ProviderBody { get; }
}
