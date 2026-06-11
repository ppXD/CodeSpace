namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The single source for the agent-run liveness contract, shared by the executor (which heartbeats while
/// a run is live — <see cref="HeartbeatLoop"/>) and <see cref="AgentRunReconcilerService"/> (which abandons
/// a Running run gone quiet). A live worker pings every <see cref="HeartbeatInterval"/>; the reconciler
/// abandons only after <see cref="Window"/> of no heartbeat AND no events. Deriving the interval FROM the
/// window keeps the two halves from drifting — lowering the window via the env var automatically tightens
/// the heartbeat, so the "window shorter than the heartbeat cadence" failure mode can't reappear.
/// </summary>
public static class AgentRunLiveness
{
    /// <summary>Operators tune reclaim aggressiveness via this env var (a TimeSpan, e.g. "00:05:00"); default 5 min. Pinned by a test (Rule 8).</summary>
    public const string WindowEnvVar = "CODESPACE_AGENT_RUN_LIVENESS_WINDOW";

    private static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(5);

    /// <summary>Floor on the cadence so a tiny / zero window (e.g. a test forcing immediate abandonment) can't turn the heartbeat into a busy-wait.</summary>
    private static readonly TimeSpan MinHeartbeatInterval = TimeSpan.FromSeconds(5);

    /// <summary>A Running run with no heartbeat AND no event activity within this window is treated as abandoned by the reconciler.</summary>
    public static TimeSpan Window =>
        TimeSpan.TryParse(Environment.GetEnvironmentVariable(WindowEnvVar), out var window) ? window : DefaultWindow;

    /// <summary>
    /// How long a claim's lease lasts before the reconciler may reclaim the run — equal to the
    /// <see cref="Window"/>. The heartbeat renews it every <see cref="HeartbeatInterval"/> (Window/3), so a
    /// live worker's lease never lapses. A single source so lowering the window (once restart re-attach makes
    /// a faster reclaim safe) tightens the lease, the renew cadence, AND the reclaim floor together.
    /// </summary>
    public static TimeSpan LeaseDuration => Window;

    /// <summary>The lease expiry to stamp NOW — <c>UtcNow + <see cref="LeaseDuration"/></c>. Stamped at claim and refreshed on every heartbeat.</summary>
    public static DateTimeOffset NextLeaseExpiry() => DateTimeOffset.UtcNow + LeaseDuration;

    /// <summary>
    /// How often a live worker pings its heartbeat — a third of the <see cref="Window"/>, so two pings can
    /// be lost before the reconciler would abandon. Floored at <see cref="MinHeartbeatInterval"/>.
    /// </summary>
    public static TimeSpan HeartbeatInterval
    {
        get
        {
            var third = TimeSpan.FromTicks(Window.Ticks / 3);
            return third < MinHeartbeatInterval ? MinHeartbeatInterval : third;
        }
    }
}
