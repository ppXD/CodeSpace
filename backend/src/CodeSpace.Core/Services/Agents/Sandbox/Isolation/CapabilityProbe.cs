namespace CodeSpace.Core.Services.Agents.Sandbox.Isolation;

/// <summary>
/// Keeps the answer to a question this process can only settle by trying — are <c>ip</c> and <c>nft</c> runnable
/// (<see cref="FilteredEgressNetns.IsSupported"/>), can it build a sealed namespace (<see cref="FilteredEgressNetns.CanSeal"/>).
/// A proof holds for the process. A failure may be transient — a fork that failed once, a slow first mount of
/// <c>/run/netns</c>, rtnl held by a burst of teardowns — so it stands for one retry interval and is then probed
/// again, with its reason kept for whoever reports what could not be done.
///
/// <para>The FIRST probe is waited for by every caller: a fresh worker that may well seal must not refuse its first
/// launches while it finds out. A RE-probe is made by one caller outside the lock while the others take the standing
/// failure — which they would have got anyway — instead of queueing behind a probe that can take tens of seconds on a
/// host that keeps failing slowly. Timed on a monotonic clock, so a wall-clock step cannot hold a failure forever or
/// re-probe on every call.</para>
/// </summary>
internal sealed class CapabilityProbe(Func<string?> probe, Func<TimeSpan> monotonicNow, TimeSpan retryInterval)
{
    private readonly object _lock = new();
    private bool _probed;
    private bool _proven;
    private bool _probing;
    private TimeSpan _retryAt;
    private string? _reason;

    public bool Holds
    {
        get
        {
            lock (_lock)
            {
                if (_proven) return true;
                if (!_probed) return Record(probe());
                if (_probing || monotonicNow() < _retryAt) return false;

                _probing = true;
            }

            var reason = probe();

            lock (_lock)
            {
                _probing = false;
                return Record(reason);
            }
        }
    }

    /// <summary>Why the last probe failed, or null when none has failed since the last proof.</summary>
    public string? UnavailableReason { get { lock (_lock) return _reason; } }

    private bool Record(string? reason)
    {
        _probed = true;
        _reason = reason;
        _proven = reason is null;
        _retryAt = monotonicNow() + retryInterval;

        return _proven;
    }
}
