using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.ModelCredentials;

/// <summary>
/// A team's model credential as shown in the management UI — NEVER carries the secret. The key is
/// surfaced only as a masked hint (last 4 chars, derived transiently on read, then discarded), exactly
/// like the OpenAI / Anthropic consoles. <see cref="BaseUrl"/> is non-secret config and is shown verbatim.
/// </summary>
public sealed record ModelCredentialSummary
{
    public required Guid Id { get; init; }
    public required Guid TeamId { get; init; }

    /// <summary>Stable provider tag ("Anthropic", "OpenAI", "OpenRouter", "Ollama", "Custom").</summary>
    public required string Provider { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Masked tail of the key, e.g. <c>····a1b2</c>. Null for a keyless provider (a local Ollama reached over <see cref="BaseUrl"/>) OR a key that can no longer be decrypted — disambiguate with <see cref="KeyUnreadable"/>.</summary>
    public string? KeyHint { get; init; }

    /// <summary>
    /// True when the credential HAS a stored key that can no longer be decrypted (the Data Protection key-ring was
    /// rotated / lost / migrated). Distinguishes a DEAD key from a genuinely keyless provider — both carry a null
    /// <see cref="KeyHint"/>, but only this one needs the operator to re-enter the secret. The UI flags it as such.
    /// </summary>
    public bool KeyUnreadable { get; init; }

    /// <summary>Non-secret base-URL override (gateway / self-hosted endpoint), shown as-is. Null = the provider default.</summary>
    public string? BaseUrl { get; init; }

    public required CredentialStatus Status { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
}
