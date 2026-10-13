using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Credentials;

/// <summary>
/// Completes a provider-credential OAuth flow. Triggered by the provider's redirect to our
/// callback endpoint. INTENTIONALLY has no authorization marker — the OAuth provider's
/// redirect doesn't carry our JWT. The proof is the state row (one-time use + TTL) TOGETHER
/// with <see cref="BrowserNonce"/>, which only the browser that started the flow holds.
/// Without it nothing is consumed: the callback is handed to the SPA, where only the person
/// who started the connect can finish it (<see cref="CompleteCredentialOAuthAsInitiatorCommand"/>).
/// Do NOT add IRequireTeamMembership here.
/// </summary>
public sealed record CompleteCredentialOAuthCommand : ICommand<CompleteCredentialOAuthResult>
{
    public required string Code { get; init; }
    public required string State { get; init; }

    /// <summary>
    /// The nonce from the starting browser's binding cookie, read by the controller. Null when the browser sent
    /// none — a browser that could not keep the cookie (the API on another site), or any browser but the one that
    /// started the flow — and then nothing is consumed.
    /// </summary>
    public string? BrowserNonce { get; init; }
}

public sealed record CompleteCredentialOAuthResult
{
    /// <summary>
    /// The credential the connect produced. Null when the callback carried no binding for its state and was handed off
    /// instead: nothing was consumed, and <see cref="ReturnUrl"/> is the SPA's completion page, where the signed-in
    /// person who started the connect finishes it.
    /// </summary>
    public Guid? CredentialId { get; init; }

    public Guid? TeamId { get; init; }

    /// <summary>Where the browser goes next: the connect's own return address once it completed, or the SPA's completion page when it was handed off.</summary>
    public required string ReturnUrl { get; init; }

    /// <summary>The path the spent binding cookie was set on, which clearing it must name — see <c>InitCredentialOAuthResult</c>.</summary>
    public required string BrowserNonceCookiePath { get; init; }
}
