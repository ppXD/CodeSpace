using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Workflows.ExternalTriggers;

/// <summary>
/// A budget on authenticated deliveries that can actually reach RunStarter. Request-rate limiting happens before
/// authentication and is keyed by the sender's address and target; it cannot express a leaked key repeatedly starting
/// runs. This limiter is process-local, so its effective ceiling scales with the number of API replicas.
/// </summary>
public sealed class ExternalTriggerRunAdmission : ISingletonDependency, IDisposable
{
    public const int PermitsPerWindow = 60;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<Guid, FixedWindowRateLimiter> _limiters = new();

    public bool TryAcquire(Guid triggerId)
    {
        var limiter = _limiters.GetOrAdd(triggerId, _ => new FixedWindowRateLimiter(
            new FixedWindowRateLimiterOptions { PermitLimit = PermitsPerWindow, Window = Window, QueueLimit = 0 }));
        return limiter.AttemptAcquire(1).IsAcquired;
    }

    public void Dispose()
    {
        foreach (var limiter in _limiters.Values) limiter.Dispose();
        _limiters.Clear();
    }
}
