namespace CodeSpace.Messages.Agents;

/// <summary>
/// A model credential resolved + decrypted just-in-time for ONE run — the transient form a harness projects
/// into the sandbox environment. Never persisted: it lives only in memory between the executor's resolve step
/// and the harness building its invocation, then is discarded. (The durable form is the encrypted
/// <c>ModelCredential</c> row; the run only ever freezes a reference to it.)
/// </summary>
public sealed record ResolvedModelCredential
{
    /// <summary>The model-provider tag (<c>ILLMProviderModule.Provider</c>: "Anthropic", "OpenAI", "OpenRouter", …) — selects how a harness projects it to env.</summary>
    public required string Provider { get; init; }

    /// <summary>
    /// The <c>ModelCredential</c> ROW this came from — the identity of the key now in the sandbox's environment.
    /// Null ONLY for the operator-global single-tenant key, which has no row. Carried because "which key is the
    /// run holding" is a fact callers need and could not otherwise recover: a team can credential the same provider
    /// twice (a direct vendor key and a gateway), so a provider tag does NOT identify the key. D3's escalation
    /// bounds its candidate pool to exactly this row's models, which is what keeps the resolver's own guarantee —
    /// the model id and the key come from the SAME row — true after the model is swapped.
    /// </summary>
    public Guid? CredentialId { get; init; }

    /// <summary>The decrypted API key / gateway auth token, or null for a keyless provider (e.g. a local Ollama reached over <see cref="BaseUrl"/>).</summary>
    public string? ApiKey { get; init; }

    /// <summary>Non-secret base-URL override (OpenRouter / self-hosted gateway / Ollama), or null for the provider's default endpoint.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// A model id to fall back to when the run pinned NO model (an "auto" launch) — the credential's first enabled
    /// model. Lets a custom-gateway team run on one of ITS models instead of the CLI's built-in default (e.g. codex's
    /// <c>gpt-5.5</c>), which a gateway that hosts only its own family rejects. Null when the credential carries no
    /// registered models (e.g. an operator-global key) — then the CLI default stands, correct for the official vendor.
    /// </summary>
    public string? DefaultModel { get; init; }
}
