using System.Collections.Concurrent;

namespace CodeSpace.Core.Services.Providers.Resilience;

/// <summary>
/// How many requests each agent run has sent through each connection in its current minute, so one run spends at most
/// <c>perMinute</c> of them. A run's minute starts with its first request and is whole again a minute later. Thread-safe.
/// A window a minute past its end is dropped when a new one opens, so the table holds only runs active in the last two
/// minutes.
/// </summary>
internal sealed class AgentRunProviderShare(int perMinute)
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<(Guid Instance, Guid Run), RunWindow> _windows = new();

    /// <summary>Charge one request of <paramref name="runId"/> on <paramref name="instanceId"/> at <paramref name="now"/>: true when it is within the run's share, false — charging nothing — when the share is spent.</summary>
    public bool TryCharge(Guid instanceId, Guid runId, DateTimeOffset now) =>
        _windows.GetOrAdd((instanceId, runId), _ => OpenWindow(now)).TryCharge(now, perMinute);

    private RunWindow OpenWindow(DateTimeOffset now)
    {
        foreach (var (key, window) in _windows)
            if (window.EndedBefore(now - Minute)) _windows.TryRemove(key, out _);

        return new RunWindow(now);
    }

    private sealed class RunWindow(DateTimeOffset start)
    {
        private readonly object _gate = new();
        private DateTimeOffset _start = start;
        private int _spent;

        public bool TryCharge(DateTimeOffset now, int limit)
        {
            lock (_gate)
            {
                if (now - _start >= Minute) (_start, _spent) = (now, 0);

                if (_spent >= limit) return false;

                _spent++;
                return true;
            }
        }

        public bool EndedBefore(DateTimeOffset moment)
        {
            lock (_gate) return _start + Minute < moment;
        }
    }
}
