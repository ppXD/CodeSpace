using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Credentials;

/// <summary>
/// Completes a provider-credential OAuth flow. Triggered by the provider's redirect to our
/// callback endpoint. INTENTIONALLY has no authorization marker — the OAuth provider's
/// redirect doesn't carry our JWT. The proof is the state row (one-time use + TTL) TOGETHER
/// with <see cref="BrowserNonce"/>, which only the browser that started the flow holds.
/// Do NOT add IRequireTeamMembership here.
/// </summary>
public sealed record CompleteCredentialOAuthCommand : ICommand<CompleteCredentialOAuthResult>
{
    public required string Code { get; init; }
    public required string State { get; init; }

    /// <summary>
    /// The nonce from the starting browser's binding cookie, read by the controller. Null when the browser sent
    /// none — a callback from any browser but the one that started the flow — and then nothing is consumed.
    /// </summary>
    public string? BrowserNonce { get; init; }
}

public sealed record CompleteCredentialOAuthResult
{
    public required Guid CredentialId { get; init; }
    public required Guid TeamId { get; init; }
    public required string ReturnUrl { get; init; }

    /// <summary>The path the spent binding cookie was set on, which clearing it must name — see <c>InitCredentialOAuthResult</c>.</summary>
    public required string BrowserNonceCookiePath { get; init; }
}
