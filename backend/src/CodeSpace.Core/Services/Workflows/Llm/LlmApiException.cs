namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// The machine-actionable failure category of an LLM transport call. The node / engine / decider branch on this to
/// decide retry-vs-fail-vs-route (e.g. retry <see cref="Transient"/>/<see cref="RateLimited"/>, fail-fast
/// <see cref="AuthFailed"/>/<see cref="BadRequest"/>) instead of string-sniffing a message. Generic across providers
/// — every OpenAI-compatible / Anthropic gateway maps onto these.
/// </summary>
public enum LlmErrorCategory
{
    /// <summary>A 5xx / 408 / connection-reset / client-side request timeout — the gateway was reachable-but-unhealthy or slow. Safe to retry (no billable completion was produced).</summary>
    Transient,

    /// <summary>A 429 — rate / quota limited. Retry after the <see cref="LlmApiException.RetryAfter"/> backoff.</summary>
    RateLimited,

    /// <summary>The prompt + max_tokens exceed the model's context window. NOT retryable as-is — the caller must shrink the input.</summary>
    ContextLengthExceeded,

    /// <summary>A 401 / 403 — the key is missing / invalid / lacks access. NOT retryable; the operator must fix the credential.</summary>
    AuthFailed,

    /// <summary>A 400 / 422 the gateway rejected for a request-shape reason (e.g. an unsupported feature like forced tool-use). NOT retryable as-is; it is also the trigger to DEGRADE the structured path to its prompt-only floor.</summary>
    BadRequest,

    /// <summary>The provider blocked the request/response on a content policy. NOT retryable as-is.</summary>
    ContentFiltered,

    /// <summary>A 2xx whose body could not be parsed into the expected wire shape (empty / non-JSON / wrong shape). NOT auto-retryable (it may have billed); surfaced for diagnosis.</summary>
    Malformed,
}

/// <summary>
/// A typed LLM transport failure carrying the HTTP status, a machine-actionable <see cref="Category"/>, and any
/// parsed <c>Retry-After</c> — so the structured-output degrade, the engine <c>RetryPlan</c>, and the supervisor
/// decider can each branch on the FAILURE KIND instead of sniffing a prose message (the old behaviour collapsed every
/// failure into one untyped <see cref="InvalidOperationException"/>). Mirrors the Git layer's
/// <c>ProviderApiException{StatusCode}</c> pattern. Thrown by the LLM clients' transport on a non-2xx, a client-side
/// timeout, or an unparseable body — NEVER by pre-flight config checks (a missing key stays an
/// <see cref="InvalidOperationException"/>), and NEVER for a 2xx-with-no-structured-output content failure.
/// </summary>
public sealed class LlmApiException : Exception
{
    public LlmApiException(string provider, int? statusCode, LlmErrorCategory category, string providerMessage, TimeSpan? retryAfter = null, Exception? inner = null)
        : base(BuildMessage(provider, statusCode, category, providerMessage), inner)
    {
        Provider = provider;
        StatusCode = statusCode;
        Category = category;
        ProviderMessage = providerMessage;
        RetryAfter = retryAfter;
    }

    /// <summary>The provider tag (e.g. "Anthropic", "OpenAI") — names which wire failed.</summary>
    public string Provider { get; }

    /// <summary>The HTTP status, or null for a transport failure with no response (timeout / connection reset).</summary>
    public int? StatusCode { get; }

    /// <summary>The machine-actionable failure category callers branch on.</summary>
    public LlmErrorCategory Category { get; }

    /// <summary>The raw provider error body (the rate-limit / context-length / auth message), shown verbatim to operators.</summary>
    public string ProviderMessage { get; }

    /// <summary>The honored <c>Retry-After</c> delay when the provider supplied one (a 429/503), else null.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Whether re-issuing the SAME call could succeed — only a transient/rate-limit fault. Auth/bad-request/context/content/malformed are terminal as-is.</summary>
    public bool IsRetryable => Category is LlmErrorCategory.Transient or LlmErrorCategory.RateLimited;

    /// <summary>Classify an HTTP status + error body into a <see cref="LlmErrorCategory"/> — generic across providers. The body keyword checks (context-length, content-filter) are heuristic refinements of a 400/422 and fail safe to <see cref="LlmErrorCategory.BadRequest"/>.</summary>
    public static LlmErrorCategory Classify(int statusCode, string? body) => statusCode switch
    {
        401 or 403 => LlmErrorCategory.AuthFailed,
        429 => LlmErrorCategory.RateLimited,
        408 => LlmErrorCategory.Transient,
        >= 500 => LlmErrorCategory.Transient,
        400 or 413 or 422 when MentionsContextLength(body) => LlmErrorCategory.ContextLengthExceeded,
        400 or 422 when MentionsContentFilter(body) => LlmErrorCategory.ContentFiltered,
        _ => LlmErrorCategory.BadRequest,
    };

    private static bool MentionsContextLength(string? body) =>
        ContainsAny(body, "context length", "context_length", "context window", "maximum context", "too long", "maximum_tokens", "reduce the length", "string too long");

    private static bool MentionsContentFilter(string? body) =>
        ContainsAny(body, "content filter", "content_filter", "content policy", "content_policy", "safety", "flagged");

    private static bool ContainsAny(string? body, params string[] needles)
    {
        if (string.IsNullOrEmpty(body)) return false;

        foreach (var n in needles)
            if (body.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private static string BuildMessage(string provider, int? statusCode, LlmErrorCategory category, string providerMessage) =>
        $"{provider} API error ({(statusCode is { } s ? $"HTTP {s}" : "no-status")}, {category}): {providerMessage}";
}
