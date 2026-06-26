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
