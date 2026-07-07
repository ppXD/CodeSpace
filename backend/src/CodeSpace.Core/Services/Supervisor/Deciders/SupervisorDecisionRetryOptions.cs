namespace CodeSpace.Core.Services.Supervisor.Deciders;

/// <summary>
/// Bounds the SCOPED retry that <see cref="RetryingSupervisorDeciderDecorator"/> puts around the supervisor brain call.
/// The per-attempt budget defaults to the full 600s the agent-RUN HttpClient uses: a slow reasoning model authoring a
/// big plan is a LEGITIMATE multi-minute call (and one decision attempt can span several HTTP round-trips under the
/// progressive structured-output degrade), so a tight budget guillotines exactly the calls the deep lane exists for.
/// A bounded attempt count lets a TRANSIENT blip self-heal in place instead of terminalizing the durable run on the
/// supervisor node's default single attempt. Both are env-overridable (air-gapped / slow-gateway operators) with the
/// var names pinned by a test; values are clamped so a fat-fingered override can never disable the bound or pin a
/// worker indefinitely (worst case ≈ MaxAttempts × PerCallTimeout for a gateway that hangs every attempt).
/// </summary>
public sealed class SupervisorDecisionRetryOptions
{
    public const string MaxAttemptsEnvVar = "CODESPACE_SUPERVISOR_DECISION_MAX_ATTEMPTS";
    public const string TimeoutSecondsEnvVar = "CODESPACE_SUPERVISOR_DECISION_TIMEOUT_SECONDS";

    /// <summary>Hard cap on a provider-supplied Retry-After the backoff will honor — a hostile / misconfigured gateway header can suggest hours and would otherwise pin a worker for its full length. Pinned by a unit test (Rule 8).</summary>
    public static readonly TimeSpan RetryAfterCeiling = TimeSpan.FromMinutes(15);

    /// <summary>Hard cap on one exponential backoff sleep between attempts — keeps the in-process wait bounded regardless of attempt count. Pinned by a unit test (Rule 8).</summary>
    public static readonly TimeSpan BackoffCeiling = TimeSpan.FromSeconds(60);

    /// <summary>How many times the brain call is attempted IN TOTAL (1 = no retry, a transparent passthrough). Clamped to [1, 10]. Default 5.</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>The per-attempt budget; a call that does not return within it is treated as a transient timeout and retried. Clamped to [10s, 900s]. Default 600s — a slow reasoning model on a big plan is a legitimate multi-minute decision, and one attempt can span several HTTP round-trips.</summary>
    public TimeSpan PerCallTimeout { get; init; } = TimeSpan.FromSeconds(600);

    /// <summary>The exponential backoff unit between attempts: attempt N waits BaseBackoff × 2^(N−1) (capped at <see cref="BackoffCeiling"/>, ±20% jitter so retries never re-storm a recovering gateway in lockstep) unless the provider supplied a Retry-After (honored verbatim up to <see cref="RetryAfterCeiling"/>). Default 2s; tests set zero so they never actually sleep.</summary>
    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Read the operator overrides from the environment, clamped to safe bounds (a missing / non-numeric / out-of-range value falls back to the default — never disables the bound).</summary>
    public static SupervisorDecisionRetryOptions FromEnvironment() => new()
    {
        MaxAttempts = ReadClampedInt(MaxAttemptsEnvVar, defaultValue: 5, min: 1, max: 10),
        PerCallTimeout = TimeSpan.FromSeconds(ReadClampedInt(TimeoutSecondsEnvVar, defaultValue: 600, min: 10, max: 900)),
    };

    private static int ReadClampedInt(string envVar, int defaultValue, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);

        if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw, out var value)) return defaultValue;

        return Math.Clamp(value, min, max);
    }
}
