using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Credentials;

/// <summary>
/// A fresh authorization to put into a credential row that already exists, in place — the same id, so everything that
/// references it (repositories, group hooks, act-as identities) keeps working without anything being moved. Handed over by
/// the OAuth callback finishing a connect that named a credential to replace, and by a person pasting a replacement token.
/// </summary>
public sealed record CredentialReauthorization
{
    public required Guid CredentialId { get; init; }

    /// <summary>The team the request was made in. A credential of another team reads as not found.</summary>
    public required Guid TeamId { get; init; }

    /// <summary>The provider instance the authorization was obtained from — must be the credential's own.</summary>
    public required Guid ProviderInstanceId { get; init; }

    /// <summary>The new token material. Its type becomes the row's auth type: a token can replace an OAuth sign-in and the other way round.</summary>
    public required CredentialPayload Payload { get; init; }

    /// <summary>The scopes the new authorization was granted, when the provider said. Left out, the provider's own probe of the token supplies them, or the row records them as unknown.</summary>
    public IReadOnlyList<string>? Scopes { get; init; }

    /// <summary>A new display name; left out, the row keeps the one it has.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Who is re-authorizing — written as the row's last modifier, and the only person whose act-as identity the re-authorization relinks (their own).</summary>
    public required Guid ActorUserId { get; init; }
}

public sealed record CredentialReauthorizationResult
{
    public required Guid CredentialId { get; init; }

    /// <summary>The provider account the new authorization belongs to, when the provider could say.</summary>
    public string? ProviderUsername { get; init; }
}

/// <summary>
/// The columns a re-authorization writes in one statement, and the payload the row must still hold for the write to land.
/// </summary>
public sealed record CredentialReplacement
{
    public required Guid CredentialId { get; init; }

    /// <summary>The encrypted payload the row held when it was read under the refresh lock. The write lands only while the row still holds it.</summary>
    public required string ExpectedEncryptedPayload { get; init; }

    public required string EncryptedPayload { get; init; }
    public required AuthType AuthType { get; init; }

    /// <summary>Null records the scopes as unknown, which the capability check reads as "no false warning" rather than "none granted".</summary>
    public IReadOnlyList<string>? Scopes { get; init; }

    public DateTimeOffset? ExpiresDate { get; init; }

    /// <summary>Null keeps the row's name.</summary>
    public string? DisplayName { get; init; }

    public required Guid ModifiedBy { get; init; }
}
