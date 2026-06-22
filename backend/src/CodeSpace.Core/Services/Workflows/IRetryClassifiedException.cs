namespace CodeSpace.Core.Services.Workflows;

/// <summary>
/// A failure that knows whether re-attempting it could succeed — so the engine's retry loop fails FAST on a terminal
/// fault (auth, bad-request, content-length) instead of burning every attempt with backoff, and honors a provider's
/// <c>Retry-After</c> as the backoff for a rate-limit. GENERIC: the engine depends only on this interface, never on a
/// specific transport's exception type (<c>LlmApiException</c> implements it; any future provider exception can too).
/// A thrown exception that does NOT implement this is retried per the node's plan (the prior, status-blind behaviour).
/// </summary>
public interface IRetryClassifiedException
{
    /// <summary>Whether re-issuing the same call could succeed (a transient / rate-limit fault) — false for a terminal fault the retry must not waste attempts on.</summary>
    bool IsRetryable { get; }

    /// <summary>The provider-supplied backoff to wait before the next attempt (a 429/503 Retry-After), or null to use the node plan's backoff.</summary>
    TimeSpan? RetryAfter { get; }
}
