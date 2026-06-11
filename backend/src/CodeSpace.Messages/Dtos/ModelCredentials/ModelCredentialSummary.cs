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

    /// <summary>Masked tail of the key, e.g. <c>····a1b2</c>. Null for a keyless provider (a local Ollama reached over <see cref="BaseUrl"/>).</summary>
    public string? KeyHint { get; init; }

    /// <summary>Non-secret base-URL override (gateway / self-hosted endpoint), shown as-is. Null = the provider default.</summary>
    public string? BaseUrl { get; init; }

    public required CredentialStatus Status { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
}
