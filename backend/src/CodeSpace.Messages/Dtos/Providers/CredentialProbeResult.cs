using CodeSpace.Messages.Exceptions;

namespace CodeSpace.Messages.Dtos.Providers;

public sealed record CredentialProbeResult
{
    public required bool IsValid { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string>? GrantedScopes { get; init; }
    public string? AuthenticatedUserExternalId { get; init; }
    public string? AuthenticatedUserName { get; init; }

    /// <summary>When a static token expires at its provider (GitLab's <c>expires_at</c>). Null when the token never expires — or when the provider did not describe the token at all, which <see cref="Active"/> being null tells apart.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>What the provider said about the token being live (GitLab's <c>active</c> and <c>revoked</c>); null when it did not describe the token, in which case <see cref="ExpiresAt"/> is unknown rather than "never".</summary>
    public bool? Active { get; init; }

    /// <summary>
    /// The provider ANSWERED and refused the credential — a 401, or a grant that can no longer be refreshed. Distinct from
    /// <see cref="IsValid"/> being false for lack of an answer (a dropped connection, a timeout, a 5xx), which says nothing
    /// about the credential: only a rejection is evidence to act on.
    /// </summary>
    public bool IsRejected { get; init; }

    /// <summary>The answer for a probe that threw: rejected when the provider refused the credential, otherwise merely not valid.</summary>
    public static CredentialProbeResult FromFailure(Exception failure) => new()
    {
        IsValid = false,
        Error = failure.Message,
        IsRejected = failure is ProviderApiException { StatusCode: 401 } or CredentialNeedsReconnectException
    };
}
