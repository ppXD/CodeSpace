using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Sandbox;

/// <summary>
/// One agent run's PROGRESS LEASE: the durable record of when the run last produced evidence it is still working
/// (<see cref="AgentProgressSignal"/>). The run's observer holds the lease and terminates the run as
/// <see cref="SandboxStatus.Stalled"/> once no signal has renewed it for the no-progress window; anything host-side
/// that KNOWS the run is progressing renews it here.
///
/// <para>It is a directory of one tiny file per signal, whose content is the UTC instant that signal last renewed. It
/// is a DURABLE, on-disk record for the same reason every other cross-boundary fact in this subsystem is (the pid file,
/// the exit marker, the log seal): the observer is not the writer, and it may be a DIFFERENT observer in a different
/// process after a restart. An in-memory registry would lose the lease exactly when it matters most — a worker restart
/// while a run is parked on a human approval would hand the fresh observer an empty lease and it would kill a run whose
/// decision was still coming. WHERE the directory sits is not this type's business: it is handed one, so the runner that
/// owns a spool layout hands out its own (<c>LocalProcessRunner.ProgressLeaseFor</c>) and this type stays reusable by a
/// second durable runner.</para>
///
/// <para><b>THIS type is layout-free; the concern root is not runner-free.</b> The reusability above is a property of
/// this class only. Two callers at the concern root resolve their lease by calling the local runner's static directly —
/// <c>AgentRunExecutor.RunDurableAsync</c> (<c>LocalProcessRunner.ProgressLeaseDirectoryFor</c>) and
/// <c>AgentMcpEndpoint</c> (<c>LocalProcessRunner.ProgressLeaseFor</c>) — so a second durable runner's lease would not
/// be reached by either until those two resolve the directory through the runner that actually launched the run. Rule
/// 18.3's boundary is the goal here, not a property the code currently has.</para>
///
/// <para>Renewal is BEST-EFFORT by construction: a lease write that fails (a full or read-only spool) is swallowed, so
/// the lease can never fail a run. The cost of a missed renewal is bounded — it can only bring the stall decision
/// forward to today's behaviour, never past the run's execution wall deadline, and never change what a stall resolves
/// to. And a renewal only ever DEFERS a stall while the run has such a deadline: an observer refuses this lease
/// entirely for a run with no wall clock, where the watchdog is the run's only bound (see
/// <see cref="AgentProgressSignal"/>). Writing to a refused lease is harmless — nobody reads it.</para>
/// </summary>
public sealed class AgentProgressLease
{
    /// <summary>How often a HELD signal (<see cref="HoldAsync{T}"/>) re-stamps its marker while the work it covers is still in flight. Comfortably under any no-progress window an operator can configure, so a hold spanning a ten-minute human approval keeps the lease continuously fresh.</summary>
    public static readonly TimeSpan RenewalHeartbeat = TimeSpan.FromSeconds(1);

    private readonly string _leaseDirectory;

    public AgentProgressLease(string leaseDirectory) { _leaseDirectory = leaseDirectory; }

    /// <summary>Where this lease's markers live — exposed so a caller can assert the writer's and the observer's directories are the same one.</summary>
    public string LeaseDirectory => _leaseDirectory;

    /// <summary>Record that <paramref name="signal"/> observed progress NOW. Best-effort: an unwritable spool is swallowed (see the type remarks).</summary>
    public void Renew(AgentProgressSignal signal)
    {
        try
        {
            Directory.CreateDirectory(_leaseDirectory);

            File.WriteAllText(MarkerPath(signal), DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A lease renewal is an optimisation on the watchdog's patience, never part of the run — swallow.
        }
    }

    /// <summary>The most recent instant ANY signal renewed this lease, or null when nothing ever has (no lease directory, no readable marker) — which the observer reads as "this signal source contributed nothing", not as "the run is stalled".</summary>
    public DateTimeOffset? LastRenewalUtc() => NewestRenewal()?.At;

    /// <summary>The newest renewal WITH the signal that made it, so the observer credits the actual evidence rather than assuming which one it was — the honest answer to "why is this run still alive?". Null when nothing ever renewed this lease.</summary>
    public (AgentProgressSignal Signal, DateTimeOffset At)? NewestRenewal()
    {
        (AgentProgressSignal Signal, DateTimeOffset At)? newest = null;

        foreach (var signal in Enum.GetValues<AgentProgressSignal>())
            if (TryReadMarker(signal) is { } renewedAt && (newest is null || renewedAt > newest.Value.At)) newest = (signal, renewedAt);

        return newest;
    }

    /// <summary>
    /// Run <paramref name="work"/> while HOLDING <paramref name="signal"/>: renew immediately, keep renewing every
    /// <see cref="RenewalHeartbeat"/> for as long as the work is in flight, and stop the instant it finishes. This is
    /// how a long platform-side wait (an MCP tool call parked on a human approval) renews the lease rather than racing
    /// it. The hold cannot outlive the work — the heartbeat is cancelled in a finally and is linked to
    /// <paramref name="cancellationToken"/>, so a torn-down run stops renewing too.
    /// </summary>
    public async Task<T> HoldAsync<T>(AgentProgressSignal signal, Func<Task<T>> work, CancellationToken cancellationToken)
    {
        Renew(signal);

        using var released = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = HeartbeatAsync(signal, released.Token);

        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            released.Cancel();

            await QuietlyAsync(heartbeat).ConfigureAwait(false);
        }
    }

    private async Task HeartbeatAsync(AgentProgressSignal signal, CancellationToken cancellationToken)
    {
        while (true)
        {
            try { await Task.Delay(RenewalHeartbeat, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            Renew(signal);
        }
    }

    private static async Task QuietlyAsync(Task heartbeat)
    {
        try { await heartbeat.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* the hold released it — expected */ }
    }

    private DateTimeOffset? TryReadMarker(AgentProgressSignal signal)
    {
        try
        {
            var path = MarkerPath(signal);

            return File.Exists(path) && DateTimeOffset.TryParse(File.ReadAllText(path), out var renewedAt) ? renewedAt : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;   // a marker being rewritten under us reads as "no renewal from this signal yet"; the next poll sees it
        }
    }

    private string MarkerPath(AgentProgressSignal signal) => Path.Combine(_leaseDirectory, signal.ToString().ToLowerInvariant());
}
