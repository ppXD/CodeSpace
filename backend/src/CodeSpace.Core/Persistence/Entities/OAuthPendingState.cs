namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Short-lived OAuth flow state. Created on /init, consumed on /callback, deleted after use.
/// `State` is the CSRF token + primary key — 32 bytes of CSPRNG output, base64url encoded.
/// `CodeVerifier` stays server-side; only its SHA-256 challenge goes to the provider.
/// </summary>
public class OAuthPendingState : IAuditable
{
    public string State { get; set; } = default!;

    public Guid ProviderInstanceId { get; set; }
    public Guid TeamId { get; set; }
    public Guid InitiatorUserId { get; set; }

    public string CodeVerifier { get; set; } = default!;
    public string IntendedDisplayName { get; set; } = default!;
    public Guid? IntendedOwnerUserId { get; set; }
    public string? ReturnUrl { get; set; }

    /// <summary>
    /// The credential the callback re-authorizes in place instead of creating another, or null for a plain connect. Written
    /// by a version from migration 0250 on; a row an older pod wrote reads null, and an older pod completing a newer row
    /// ignores it and creates a new credential.
    /// </summary>
    public Guid? ReplaceCredentialId { get; set; }

    /// <summary>
    /// SHA-256 of the nonce the starting browser holds in its binding cookie. A callback that presents that nonce finishes
    /// the connect on the spot; one that does not consumes nothing and is handed to the SPA, where only
    /// <see cref="InitiatorUserId"/>, signed in, can finish it. Null only on a row a version before migration 0246 wrote,
    /// which therefore only that hand-off can complete.
    /// </summary>
    public string? BrowserNonceHash { get; set; }
    public List<string>? RequestedScopes { get; set; }
    public DateTimeOffset ExpiresDate { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }

    public ProviderInstance ProviderInstance { get; set; } = default!;
    public Team Team { get; set; } = default!;
    public User Initiator { get; set; } = default!;
}
