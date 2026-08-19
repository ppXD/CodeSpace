using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Sandbox.Runners;

/// <summary>
/// The PROGRESS-LEASE half of <see cref="LocalProcessRunner"/>: the observer's answer to "is this run still working?".
///
/// <para>THREE DEADLINES, KEPT DISTINCT. A single <c>TimeoutSeconds</c> used to conflate them, and the watchdog's only
/// input used to be spool byte growth, which conflated "silent" with "stalled":</para>
/// <list type="number">
/// <item><b>The execution wall deadline</b> — <see cref="SandboxSpec.TimeoutSeconds"/>, carried as
/// <see cref="SandboxHandle.Deadline"/>. "This run has had its whole budget." Resolves to
/// <see cref="SandboxStatus.TimedOut"/>, is checked BEFORE the lease, and nothing in this file can defer it. It is also
/// OPTIONAL — <c>TimeoutSeconds</c> null or ≤0 is a supported operator choice and yields
/// <see cref="DateTimeOffset.MaxValue"/>, i.e. no wall deadline at all. That is why it cannot be the whole safety
/// argument, and why <c>ProgressWatch.LeaseFor</c> REFUSES the lease when it is absent: there, this watchdog is the
/// run's only bound.</item>
/// <item><b>The no-progress window</b> — <see cref="LocalProcessRunner.NoProgressWindow"/>. "This run has shown no
/// evidence of working for too long." Resolves to <see cref="SandboxStatus.Stalled"/>. This file changes only WHEN that
/// conclusion is reached, never what it resolves to.</item>
/// <item><b>The observer lease</b> — how long ONE observer may hold observation before another may take it over.
/// DEFERRED, deliberately unbuilt: today a re-attach simply starts observing and the pid + start-time guard prevents
/// two observers from double-killing, so there is no third deadline to introduce yet. Naming it here keeps it from
/// being collapsed back into either of the two above.</item>
/// </list>
///
/// <para>Every signal the watch reads is a LOCAL FILE read (spool lengths, lease markers). Nothing here spawns a
/// process or waits on anything external, because this code runs inside the loop that IS the run's liveness backstop —
/// a probe that can hang is worse than no probe. The two self-computed signals that would need one (consumed tree CPU
/// via <c>ps</c>, and a recursive workspace walk) are deferred for that reason and for a second one: see
/// <see cref="AgentProgressSignal"/>.</para>
/// </summary>
public sealed partial class LocalProcessRunner
{
    /// <summary>The lease sub-directory under a run's spool dir. The runner owns the spool layout, so the name lives here (as <c>AgentConfigHomeDir</c> does) and <see cref="AgentProgressLease"/> stays a layout-free reader/writer of whatever directory it is handed.</summary>
    private const string ProgressLeaseDir = "progress";

    /// <summary>The RUN-scoped progress-lease directory (never round-scoped — a revise round shares the run's lease, exactly as it shares the run's MCP socket). Single source of truth so the launch's stamp on the handle and every host-side renewer resolve the same path, exactly as <see cref="McpSocketPathFor"/> does for the socket.</summary>
    internal static string ProgressLeaseDirectoryFor(Guid runId) => Path.Combine(SpoolDirectoryFor(runId.ToString("N")), ProgressLeaseDir);

    /// <summary>The RUN-scoped progress lease a host-side renewer writes. The layout OWNER hands it out, so <see cref="AgentProgressLease"/> itself stays layout-free and a second durable runner can host the same lease type over its own spool. One expression over <see cref="ProgressLeaseDirectoryFor"/>, so a renewer and the observer cannot resolve different directories. Note that <c>AgentMcpEndpoint</c> and <c>AgentRunExecutor</c> — both at the concern root — call these two statics DIRECTLY, so Rule 18.3's boundary is not held today (see the <see cref="AgentProgressLease"/> remarks).</summary>
    internal static AgentProgressLease ProgressLeaseFor(Guid runId) => new(ProgressLeaseDirectoryFor(runId));

    /// <summary>
    /// The observer's progress-lease watch for ONE attach: it polls every <see cref="AgentProgressSignal"/> it can
    /// reach and remembers WHEN the newest one last renewed, so the observe loop can ask one question —
    /// <see cref="NoProgress"/>. Both signals are a local file read, cheap enough to poll every pass.
    /// </summary>
    internal sealed class ProgressWatch
    {
        private readonly AgentProgressLease? _lease;
        private readonly string _stdoutPath;
        private readonly string _stderrPath;
        private readonly TimeSpan _window;

        private long _spoolBytes;
        private DateTimeOffset _renewedAt;

        internal ProgressWatch(SandboxHandle handle, TimeSpan window)
        {
            _lease = LeaseFor(handle);
            _stdoutPath = Path.Combine(handle.SpoolDirectory, StdoutFile);
            _stderrPath = Path.Combine(handle.SpoolDirectory, StderrFile);
            _window = window;

            // A fresh attach starts the window now: re-attaching is a fresh observation, never an inherited stale clock.
            _spoolBytes = SpoolBytes();
            _renewedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>The signal that last renewed the lease — the honest answer to "why is this run still alive?". Null until one does. Internal rather than private so a test pins WHICH signal kept a run alive, not merely that something did (an accidentally-renewing signal would otherwise pass unnoticed).</summary>
        internal AgentProgressSignal? RenewedBy { get; private set; }

        /// <summary>Whether NO signal has renewed the lease for the whole window — the one question the observe loop asks before concluding <see cref="SandboxStatus.Stalled"/>.</summary>
        internal bool NoProgress => DateTimeOffset.UtcNow - _renewedAt >= _window;

        internal void Observe()
        {
            ObserveSpoolOutput();
            ObserveLeaseRenewal();
        }

        /// <summary>
        /// The run's on-disk lease — but ONLY when the run HAS an execution wall deadline. A renewal is safe to grant
        /// exactly because something else stops the run regardless; with <see cref="SandboxSpec.TimeoutSeconds"/> null
        /// or ≤0 the handle's <see cref="SandboxHandle.Deadline"/> is <see cref="DateTimeOffset.MaxValue"/>, this
        /// watchdog is the run's ONLY bound, and honouring a renewal there would make a wedged run UNKILLABLE — it
        /// would pin a worker, a workspace clone, a sandbox and an injected credential forever, and the reconciler
        /// cannot collect a run whose observer is still heartbeating. So in that configuration the watch keeps exactly
        /// its pre-lease form: spool bytes only, i.e. today's bound, unchanged.
        /// </summary>
        private static AgentProgressLease? LeaseFor(SandboxHandle handle)
        {
            if (handle.Deadline == DateTimeOffset.MaxValue) return null;

            return handle.ProgressLeaseDirectory is { Length: > 0 } directory ? new AgentProgressLease(directory) : null;
        }

        /// <summary>Bytes on EITHER spool (stderr-only output and a not-yet-complete line both count — the question is silence, and an emitting run is never silent).</summary>
        private void ObserveSpoolOutput()
        {
            var bytes = SpoolBytes();

            if (bytes > _spoolBytes) Renew(AgentProgressSignal.SpoolOutput);

            _spoolBytes = bytes;
        }

        /// <summary>A renewal another host component stamped on the run's on-disk lease (today: an in-flight platform request). Its recorded instant AND signal are adopted verbatim, so a renewal that landed between two polls is not lost and is credited to the evidence that actually earned it. A marker older than the current lease instant (a leftover from a previous round or a dead observer) can never renew.</summary>
        private void ObserveLeaseRenewal()
        {
            if (_lease?.NewestRenewal() is { } renewal && renewal.At > _renewedAt) RenewAt(renewal.Signal, renewal.At);
        }

        private long SpoolBytes() => SafeFileLength(_stdoutPath) + SafeFileLength(_stderrPath);

        private void Renew(AgentProgressSignal signal) => RenewAt(signal, DateTimeOffset.UtcNow);

        private void RenewAt(AgentProgressSignal signal, DateTimeOffset renewedAt)
        {
            _renewedAt = renewedAt;
            RenewedBy = signal;
        }
    }
}
