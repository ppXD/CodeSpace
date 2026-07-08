namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// The per-model WIRE-CAPABILITY registry the transport consults to build a request the target model actually accepts —
/// the in-process analogue of LiteLLM's <c>get_supported_openai_params</c> + <c>drop_params</c>. It answers, for a model id:
/// <list type="bullet">
///   <item><see cref="AcceptsSampling"/> — does it accept <c>temperature</c>/<c>top_p</c>/penalties, or is it a reasoning-tier model that 400s on them?</item>
///   <item><see cref="UsesMaxCompletionTokens"/> — does the OpenAI wire want <c>max_completion_tokens</c> (o-series / gpt-5, which reject the deprecated <c>max_tokens</c>) vs the classic <c>max_tokens</c>?</item>
///   <item><see cref="DefaultMaxOutputTokens"/> — the value to send when a caller leaves the output cap UNSET and the wire REQUIRES one (Anthropic's <c>max_tokens</c> is mandatory; OpenAI's can be omitted).</item>
/// </list>
/// Each client reconciles the provider-neutral request against these, so a caller expresses INTENT (a temperature, an
/// output budget, or "let the model decide" via null) and never has to know a model's quirks, and a request never 400s on
/// a param mismatch. Every rule is env-overridable (Rule 8) + test-pinned, and DEFAULT-ALLOW for an unknown model (never
/// drop/rename on missing info) so a new model or a custom-gateway model is non-breaking by construction.
/// </summary>
public static class LlmModelCapabilities
{
    // ── sampling: temperature / top_p / penalties ─────────────────────────────────────────────────────────────────

    /// <summary>Comma-separated model-id PREFIXES whose wire rejects explicit sampling params — APPENDED to the built-in reasoning families so an operator can cover a gateway's own reasoning model without a code change (Rule 8). Pinned by test.</summary>
    public const string NoSamplingModelsEnvVar = "CODESPACE_LLM_NO_SAMPLING_MODELS";

    /// <summary>The built-in reasoning-tier id prefixes that reject explicit sampling params (temperature / top_p / penalties) with a 400.</summary>
    private static readonly string[] DefaultNoSamplingPrefixes =
    {
        // Anthropic reasoning tier — thinking always/adaptive-on; temperature / top_p / top_k removed (400 if sent).
        "claude-opus-4-7", "claude-opus-4-8", "claude-sonnet-5", "claude-fable-5", "claude-mythos",
        // OpenAI reasoning series — only the default temperature is allowed (a non-default value 400s).
        "o1", "o3", "o4", "gpt-5",
    };

    /// <summary>Whether <paramref name="model"/> accepts explicit sampling parameters (temperature / top_p / penalties). Reads the operator override from process env.</summary>
    public static bool AcceptsSampling(string? model) => AcceptsSampling(model, Environment.GetEnvironmentVariable(NoSamplingModelsEnvVar));

    /// <summary>Testable core — PURE (touches no process state). A blank model is unclassifiable ⇒ ACCEPTS (default-allow; a blank id fails downstream anyway).</summary>
    internal static bool AcceptsSampling(string? model, string? rawNoSamplingOverride) => !MatchesAny(model, DefaultNoSamplingPrefixes, rawNoSamplingOverride);

    // ── output-cap param NAME (OpenAI wire only) ──────────────────────────────────────────────────────────────────

    /// <summary>Comma-separated model-id PREFIXES whose OpenAI-wire request must carry the output cap as <c>max_completion_tokens</c> — appended to the built-in reasoning series (Rule 8). Pinned by test.</summary>
    public const string MaxCompletionTokensModelsEnvVar = "CODESPACE_LLM_MAX_COMPLETION_TOKENS_MODELS";

    /// <summary>The built-in OpenAI reasoning-series prefixes that REQUIRE <c>max_completion_tokens</c> (they 400 on the deprecated <c>max_tokens</c>).</summary>
    private static readonly string[] DefaultMaxCompletionTokensPrefixes = { "o1", "o3", "o4", "gpt-5" };

    /// <summary>
    /// Whether the OpenAI-wire request must carry the output cap as <c>max_completion_tokens</c> (the reasoning-model
    /// requirement — those models 400 on the deprecated <c>max_tokens</c>) rather than the classic <c>max_tokens</c>.
    /// Consulted ONLY by the OpenAI / Custom client; the non-reasoning + custom-gateway path keeps the widely-understood
    /// <c>max_tokens</c> (an older OpenAI-compatible gateway may not know the newer name), so this is the narrow rename.
    /// </summary>
    public static bool UsesMaxCompletionTokens(string? model) => UsesMaxCompletionTokens(model, Environment.GetEnvironmentVariable(MaxCompletionTokensModelsEnvVar));

    /// <summary>Testable core — PURE. A blank/unknown model ⇒ FALSE (default to the classic, universally-understood <c>max_tokens</c>).</summary>
    internal static bool UsesMaxCompletionTokens(string? model, string? rawOverride) => MatchesAny(model, DefaultMaxCompletionTokensPrefixes, rawOverride);

    // ── output-cap DEFAULT (for a wire that REQUIRES the field, i.e. Anthropic) ────────────────────────────────────

    /// <summary>Env var (a positive integer) overriding the fallback output cap sent when a caller leaves it unset on a wire that requires the field. Pinned by test (Rule 8).</summary>
    public const string DefaultMaxOutputTokensEnvVar = "CODESPACE_LLM_DEFAULT_MAX_OUTPUT_TOKENS";

    /// <summary>
    /// The output cap to send when a caller leaves it UNSET and the wire REQUIRES the field. Anthropic's <c>max_tokens</c>
    /// is mandatory (unlike OpenAI's, which can be omitted so the model runs to its context limit), so a "let the model
    /// decide" null still needs a concrete number there. 8192 is generous vs the old 2048 record default yet NON-streaming
    /// -safe (Anthropic's guidance flags an HTTP-timeout risk above ~16-21K on a non-streaming request, and this transport
    /// is non-streaming) — it can rise to the model's true ceiling once the client gains streaming. Operator-tunable.
    /// </summary>
    public const int DefaultMaxOutputTokensFallback = 8192;

    /// <summary>The resolved default output cap: the env override when a positive integer, else <see cref="DefaultMaxOutputTokensFallback"/>.</summary>
    public static int DefaultMaxOutputTokens => ResolvePositive(Environment.GetEnvironmentVariable(DefaultMaxOutputTokensEnvVar), DefaultMaxOutputTokensFallback);

    /// <summary>Pure resolver (testable without env mutation): a positive integer wins, anything else falls to the default.</summary>
    internal static int ResolvePositive(string? raw, int fallback) => int.TryParse(raw, out var n) && n > 0 ? n : fallback;

    // ── matching ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Whether <paramref name="model"/> matches any built-in OR operator-added prefix. Blank ⇒ no match (default-allow / classic). PURE.</summary>
    private static bool MatchesAny(string? model, IReadOnlyList<string> defaults, string? rawOverride)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;

        var id = model.Trim();

        foreach (var prefix in defaults)
            if (Matches(id, prefix)) return true;

        if (string.IsNullOrWhiteSpace(rawOverride)) return false;

        foreach (var extra in rawOverride.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Matches(id, extra)) return true;

        return false;
    }

    /// <summary>A prefix matches when the id STARTS with it (bare <c>claude-opus-4-8</c>) or carries it right after a provider dot (<c>anthropic.claude-opus-4-8</c>) — prefix-anchored so a short token like <c>o1</c> can never false-match an embedded substring.</summary>
    private static bool Matches(string id, string prefix) =>
        id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
        id.Contains("." + prefix, StringComparison.OrdinalIgnoreCase);
}
