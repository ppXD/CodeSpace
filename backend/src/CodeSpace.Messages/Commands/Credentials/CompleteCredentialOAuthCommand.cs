using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Credentials;

/// <summary>
/// Completes a provider-credential OAuth flow. Triggered by the provider's redirect to our
/// callback endpoint. INTENTIONALLY has no authorization marker — the OAuth provider's
/// redirect doesn't carry our JWT, and the state row itself is the proof of identity
/// (CSRF token + one-time use + TTL). Do NOT add IRequireTeamMembership here.
/// </summary>
public sealed record CompleteCredentialOAuthCommand : ICommand<CompleteCredentialOAuthResult>
{
    public required string Code { get; init; }
    public required string State { get; init; }
}

public sealed record CompleteCredentialOAuthResult
{
    public required Guid CredentialId { get; init; }
    public required Guid TeamId { get; init; }
    public required string ReturnUrl { get; init; }
}
