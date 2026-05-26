namespace CodeSpace.Core.Services.Providers.Resilience;

/// <summary>
/// Thrown when a provider instance's local rate-limit bucket refuses the call (bucket empty
/// AND queue full). Distinct from the SDK's 429 — this fires BEFORE the wire call. Outbox
/// handlers can choose to retry; controllers should surface 429 to the user.
/// </summary>
public sealed class ProviderRateLimitedException : Exception
{
    public ProviderRateLimitedException(Guid providerInstanceId, string operationName)
        : base($"Local rate limit denied call '{operationName}' on provider instance {providerInstanceId} — token bucket empty and queue full. Retry after backoff.")
    {
        ProviderInstanceId = providerInstanceId;
        OperationName = operationName;
    }

    public Guid ProviderInstanceId { get; }
    public string OperationName { get; }
}
