namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// Transport defaults for the in-process LLM HTTP clients. The request timeout is the OVERALL wall budget for one LLM
/// call (across resilience retries) — deliberately GENEROUS (a slow reasoning / long-generation model must not be
/// guillotined at the framework's 100s default), and operator-tunable via an env var for an air-gapped / slow-gateway
/// deployment. A per-node / per-call budget (a request-scoped <c>CancellationTokenSource.CancelAfter</c>) refines this
/// downward where an author wants a tighter ceiling; this const is only the safety wall.
/// </summary>
public static class LlmHttpDefaults
{
    /// <summary>Env var (seconds) overriding the LLM HTTP request timeout. Pinned by a test (Rule 8) — a rename is a deployment break for any operator who tuned a slow gateway.</summary>
    public const string RequestTimeoutSecondsEnvVar = "CODESPACE_LLM_HTTP_TIMEOUT_SECONDS";

    /// <summary>The default overall request budget when the env var is unset/invalid — 600s (10 min), generous enough for slow reasoning models yet finite so a wedged connection can't pin a worker forever.</summary>
    public const int DefaultRequestTimeoutSeconds = 600;

    /// <summary>The resolved request timeout: the env override when a positive integer, else <see cref="DefaultRequestTimeoutSeconds"/>.</summary>
    public static TimeSpan RequestTimeout => TimeSpan.FromSeconds(ResolveSeconds(Environment.GetEnvironmentVariable(RequestTimeoutSecondsEnvVar)));

    /// <summary>Pure resolver (testable without env mutation): a positive integer wins, anything else falls to the default.</summary>
    public static int ResolveSeconds(string? raw) =>
        int.TryParse(raw, out var s) && s > 0 ? s : DefaultRequestTimeoutSeconds;
}
