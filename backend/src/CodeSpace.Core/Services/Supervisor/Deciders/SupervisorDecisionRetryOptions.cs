namespace CodeSpace.Core.Services.Supervisor.Deciders;

/// <summary>
/// Bounds the SCOPED retry that <see cref="RetryingSupervisorDeciderDecorator"/> puts around the supervisor brain call.
/// A short per-attempt timeout (tighter than the shared 600s HttpClient budget that agent RUNS use) so a wedged gateway
/// fails fast, and a small bounded attempt count so a TRANSIENT blip self-heals in place instead of terminalizing the
/// durable run on the supervisor node's default single attempt. Both are env-overridable (air-gapped / slow-gateway
/// operators) with the var names pinned by a test; values are clamped so a fat-fingered override can never disable the
/// bound or pin a worker indefinitely.
/// </summary>
public sealed class SupervisorDecisionRetryOptions
{
    public const string MaxAttemptsEnvVar = "CODESPACE_SUPERVISOR_DECISION_MAX_ATTEMPTS";
    public const string TimeoutSecondsEnvVar = "CODESPACE_SUPERVISOR_DECISION_TIMEOUT_SECONDS";

    /// <summary>How many times the brain call is attempted IN TOTAL (1 = no retry, a transparent passthrough). Clamped to [1, 5]. Default 3.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>The per-attempt budget; a call that does not return within it is treated as a transient timeout and retried. Clamped to [10s, 600s]. Default 90s — a decision should be fast (600s is the agent-RUN budget, not the brain's).</summary>
    public TimeSpan PerCallTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>The linear backoff unit between attempts (attempt N waits BaseBackoff × N) unless the provider supplied a Retry-After. Default 1s; tests set zero so they never actually sleep.</summary>
    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Read the operator overrides from the environment, clamped to safe bounds (a missing / non-numeric / out-of-range value falls back to the default — never disables the bound).</summary>
    public static SupervisorDecisionRetryOptions FromEnvironment() => new()
    {
        MaxAttempts = ReadClampedInt(MaxAttemptsEnvVar, defaultValue: 3, min: 1, max: 5),
        PerCallTimeout = TimeSpan.FromSeconds(ReadClampedInt(TimeoutSecondsEnvVar, defaultValue: 90, min: 10, max: 600)),
    };

    private static int ReadClampedInt(string envVar, int defaultValue, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);

        if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw, out var value)) return defaultValue;

        return Math.Clamp(value, min, max);
    }
}
