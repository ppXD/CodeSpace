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

    // ── per-model output CEILING + streaming decision ─────────────────────────────────────────────────────────────

    /// <summary>Env var (a positive integer) overriding the token budget above which a completion is STREAMED. Pinned by test (Rule 8).</summary>
    public const string StreamingThresholdEnvVar = "CODESPACE_LLM_STREAMING_THRESHOLD_TOKENS";

    /// <summary>The output-token budget above which a completion streams: a non-streaming request whose output can exceed this risks an idle-connection HTTP timeout (Anthropic's guidance flags ~21K). At or below it, the request stays non-streaming — byte-identical to the pre-streaming path. Operator-tunable.</summary>
    public const int StreamingThresholdDefault = 21000;

    /// <summary>The resolved streaming threshold: the env override when a positive integer, else <see cref="StreamingThresholdDefault"/>.</summary>
    public static int StreamingThreshold => ResolvePositive(Environment.GetEnvironmentVariable(StreamingThresholdEnvVar), StreamingThresholdDefault);

    /// <summary>Env var extending/overriding the per-model output ceilings — comma-separated <c>prefix=tokens</c> pairs (e.g. <c>my-model=32000,house-lm=8000</c>), matched before the built-ins so an operator pins a gateway model's real max with no code change (Rule 8). Pinned by test.</summary>
    public const string OutputCeilingsEnvVar = "CODESPACE_LLM_OUTPUT_CEILINGS";

    /// <summary>The built-in per-model TRUE max output tokens — used to (a) resolve a null "let the model decide" cap to the model's real ceiling (the capability unlock), and (b) clamp an over-large explicit ask away from a 400. Only confirmed values; an unknown model returns null (no clamp / conservative default).</summary>
    private static readonly (string Prefix, int Ceiling)[] DefaultOutputCeilings =
    {
        // Anthropic 128K-output tier (Fable 5 / Mythos / Opus 4.5-4.8 / Sonnet 4.6 / Sonnet 5).
        ("claude-opus-4-8", 128_000), ("claude-opus-4-7", 128_000), ("claude-opus-4-6", 128_000), ("claude-opus-4-5", 128_000),
        ("claude-sonnet-5", 128_000), ("claude-sonnet-4-6", 128_000), ("claude-fable-5", 128_000), ("claude-mythos", 128_000),
        // Anthropic 64K-output tier (Haiku 4.5).
        ("claude-haiku-4-5", 64_000), ("claude-haiku", 64_000),
    };

    /// <summary>The model's TRUE maximum output tokens, or null when unknown. Reads the operator override from process env.</summary>
    public static int? MaxOutputCeiling(string? model) => MaxOutputCeiling(model, Environment.GetEnvironmentVariable(OutputCeilingsEnvVar));

    /// <summary>Testable core — operator override wins, then the built-ins, else null (unknown). PURE.</summary>
    internal static int? MaxOutputCeiling(string? model, string? rawOverride)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;

        var id = model.Trim();

        foreach (var (prefix, ceiling) in ParseCeilings(rawOverride))
            if (Matches(id, prefix)) return ceiling;

        foreach (var (prefix, ceiling) in DefaultOutputCeilings)
            if (Matches(id, prefix)) return ceiling;

        return null;
    }

    /// <summary>
    /// Resolve the output-token budget a text completion should carry PLUS whether the transport should STREAM it — the
    /// rule that unlocks the model's full output capability while staying strictly non-breaking:
    /// <list type="bullet">
    ///   <item>An EXPLICIT cap rides verbatim, CLAMPED to the model's ceiling (never 400 on an over-large ask), and STREAMS
    ///   when it exceeds the non-streaming-safe <see cref="StreamingThreshold"/> — so a caller UNLOCKS a large output simply
    ///   by asking for it, and it can't idle-timeout. A small explicit cap stays non-streaming, byte-identical to before.</item>
    ///   <item>A NULL cap ("let the model decide") resolves CONSERVATIVELY and NON-streaming, so every existing null-cap
    ///   caller is unchanged: the Anthropic wire (<paramref name="requiresField"/> true) sends the safe default; the OpenAI
    ///   wire omits the field (the chat model self-limits to a natural length). Push a model to its full ceiling with an
    ///   explicit large cap — that is the streamed path.</item>
    /// </list>
    /// </summary>
    public static (int? Cap, bool Stream) ResolveOutputBudget(string? model, int? requested, bool requiresField)
    {
        if (requested is { } v)
        {
            var ceiling = MaxOutputCeiling(model);
            var clamped = ceiling is { } c && v > c ? c : v;
            return (clamped, clamped > StreamingThreshold);
        }

        return (requiresField ? DefaultMaxOutputTokens : (int?)null, false);
    }

    /// <summary>The non-streaming bounded output cap for a STRUCTURED completion (small by nature — kept off the streaming path). Anthropic requires the field, so null resolves to the default; an explicit ask is clamped to the model ceiling. Returns null only for a wire that can omit it (OpenAI) when the caller left it null.</summary>
    public static int? StructuredOutputCap(string? model, int? requested, bool requiresField)
    {
        var ceiling = MaxOutputCeiling(model);

        if (requested is { } v) return ceiling is { } c && v > c ? c : v;

        return requiresField ? DefaultMaxOutputTokens : (int?)null;
    }

    private static IEnumerable<(string Prefix, int Ceiling)> ParseCeilings(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;

        foreach (var entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0) continue;

            var prefix = entry[..eq].Trim();
            if (prefix.Length > 0 && int.TryParse(entry[(eq + 1)..].Trim(), out var ceiling) && ceiling > 0)
                yield return (prefix, ceiling);
        }
    }

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
