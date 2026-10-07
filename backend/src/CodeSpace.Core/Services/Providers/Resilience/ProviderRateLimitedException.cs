using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Providers.Resilience;

/// <summary>
/// Thrown when a provider instance's local rate-limit bucket refuses the call (bucket empty
/// AND queue full), or when the agent run making it has spent its share of the instance
/// (<see cref="ForAgentRun"/>). Distinct from the SDK's 429 — this fires BEFORE the wire call. Background
/// workers can choose to retry via the failure-state-machine path; controllers should surface 429 to the user.
/// </summary>
public sealed class ProviderRateLimitedException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.Exhausted;

    public string Code => FailureCodes.RateLimited;

    public string? ClientMessage => "Too many requests to the upstream provider. Retry shortly.";

    public ProviderRateLimitedException(Guid providerInstanceId, string operationName)
        : this(providerInstanceId, operationName, $"Local rate limit denied call '{operationName}' on provider instance {providerInstanceId} — token bucket empty and queue full. Retry after backoff.")
    {
    }

    private ProviderRateLimitedException(Guid providerInstanceId, string operationName, string message) : base(message)
    {
        ProviderInstanceId = providerInstanceId;
        OperationName = operationName;
    }

    /// <summary>The refusal of a call an agent run makes once the run has spent its share of the instance's requests this minute — the connection's other users keep the rest.</summary>
    public static ProviderRateLimitedException ForAgentRun(Guid providerInstanceId, string operationName, Guid agentRunId, int share) =>
        new(providerInstanceId, operationName, $"'{operationName}' was not sent: agent run {agentRunId} has used its share of {share} requests a minute on provider instance {providerInstanceId}, so the connection's other users keep the rest. Retry after a minute, or read less (a smaller page, a narrower filter).");

    public Guid ProviderInstanceId { get; }
    public string OperationName { get; }
}
