using System.Text;
using CodeSpace.Core.Settings;

namespace CodeSpace.Core.Services.Agents.Sandbox.Isolation;

/// <summary>
/// Hands out a COLLISION-FREE /30 subnet per active filtered-egress run (B3 stability hardening). nftables forward
/// chains are host-global and keyed on <c>ip saddr &lt;subnet&gt;</c>, so two CONCURRENTLY-active netns that shared a
/// /30 would co-evaluate each other's packets — a cross-run egress WIDEN (one run reaching the other's allowlist) or a
/// setup collision (the second run's <c>ip addr add</c> fails).
///
/// <para><b>The reservation is HOST-level, not process-level.</b> An in-memory set only made two runs inside ONE
/// worker disjoint, while the nft chain they collide in is shared by every process on the host — so on a multi-worker
/// deployment (which this one is) two workers handed out the same /30 and the hazard the allocator exists to remove
/// came straight back. Each /30 is now reserved by holding an exclusive OS lock on a file in a shared runtime
/// directory: another process asking for the same /30 is refused by the kernel, not by a set it cannot see. The lock
/// is bound to the open file handle, so a worker that CRASHES releases its reservations the moment its handles close —
/// no lease timer, no stale-PID sweep, nothing to reap. The in-memory set stays as the fast path (an index this
/// process already holds is skipped without a syscall); the file lock is the authority.</para>
///
/// <para>The directory sits under the AGENT-RUN SPOOL root, which is the one place the deployment already shares
/// between the worker replicas of a host (a volume in the compose file, an RWX mount across hosts) — so a reservation
/// reaches every process that could contend for the /30, and an operator who relocates the spool relocates the
/// reservations with it. Only the SUBNET is reserved: the netns / veth / nft-table NAMES stay runId-derived, and
/// teardown (which deletes by name and never references the subnet) is reconstructable from the runId alone, so this
/// changes nothing about the crash-resume teardown contract. A host whose reservation directory cannot be written (a
/// read-only mount, no rights) degrades to the previous PROCESS-LOCAL uniqueness rather than failing every
/// filtered-egress launch.</para>
/// </summary>
public sealed class EgressSubnetAllocator
{
    /// <summary>A reserved /30 and its host (.1-of-the-block + 1) and netns (+2) addresses.</summary>
    public sealed record Lease
    {
        public required string Cidr { get; init; }     // 10.A.B.C/30
        public required string HostIp { get; init; }   // 10.A.B.(C+1)
        public required string NsIp { get; init; }      // 10.A.B.(C+2)
    }

    /// <summary>The reservation directory's name under the spool root. Dot-prefixed so it can never collide with a run's own spool directory (those are keyed by 32-hex run keys) and so it reads as infrastructure beside them.</summary>
    private const string ReservationLeaf = ".egress-subnets";

    // 254 (octet2 ∈ 1..254) × 254 (octet3 ∈ 1..254) × 64 (octet4 block ∈ {0,4,…,252}) distinct /30s in 10.0.0.0/8.
    private const int Octet2Count = 254;
    private const int Octet3Count = 254;
    private const int Block4Count = 64;

    /// <summary>How far the probe walks before failing closed. Bounds the worst-case syscall count of one acquire, and is far above any plausible per-host concurrency (the /30 space itself holds ≈4.1M).</summary>
    internal const int MaxConcurrentReservations = 4096;

    private readonly object _lock = new();
    private readonly Dictionary<string, Reservation> _byRun = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inUse = new(StringComparer.Ordinal);
    private readonly string? _directory;

    private bool _hostReservationsUsable = true;

    /// <summary>The allocator every launch on this host reserves through — the one whose reservation directory is shared by every worker PROCESS, which is what makes the reservation host-level.</summary>
    public static EgressSubnetAllocator Host { get; } = new();

    /// <summary>Pass a directory to reserve in one (a test standing in for a host); omit it to resolve the deployment's own, which is where every worker process on the host resolves too.</summary>
    internal EgressSubnetAllocator(string? reservationDirectory = null) => _directory = reservationDirectory;

    /// <summary>False once the reservation directory proved unwritable — this allocator has degraded to process-local uniqueness and two worker processes on this host CAN collide. Exposed so the degradation is assertable rather than silent.</summary>
    internal bool HostReservationsUsable { get { lock (_lock) { return _hostReservationsUsable; } } }

    /// <summary>
    /// Reserve a /30 no other run on this HOST holds — probing from the lowest index up, so a released reservation is
    /// reused rather than the space being spread thin. Idempotent: re-acquiring the same runId returns its existing
    /// lease, so a setup retry / re-entry is stable.
    /// </summary>
    public Lease Acquire(string runId)
    {
        lock (_lock)
        {
            if (_byRun.TryGetValue(runId, out var held)) return LeaseFor(held.Cidr);

            for (var index = 0; index < MaxConcurrentReservations; index++)
            {
                var cidr = CidrAt(index);

                if (_inUse.Contains(cidr)) continue;

                if (!TryReserve(cidr, runId, out var handle)) continue;

                _byRun[runId] = new Reservation(cidr, handle);
                _inUse.Add(cidr);

                return LeaseFor(cidr);
            }

            // Fail closed — abort rather than share a subnet with a live run.
            throw new InvalidOperationException($"EgressSubnetAllocator: this host already holds {MaxConcurrentReservations} filtered-egress /30s.");
        }
    }

    /// <summary>Free <paramref name="runId"/>'s reservation. A no-op when it never held one (e.g. teardown by a restarted worker that never acquired it) — safe + idempotent.</summary>
    public void Release(string runId)
    {
        lock (_lock)
        {
            if (!_byRun.Remove(runId, out var reservation)) return;

            _inUse.Remove(reservation.Cidr);

            // Closing the handle drops the OS lock. The file is deliberately LEFT in place: unlinking it would let a
            // third process create a fresh inode for the same /30 and lock THAT while a second still held the old one.
            try { reservation.Handle?.Dispose(); } catch { /* best-effort release — the handle dies with the process anyway */ }
        }
    }

    /// <summary>Test-only: the number of /30s this allocator currently holds.</summary>
    internal int ActiveCount { get { lock (_lock) { return _inUse.Count; } } }

    /// <summary>What this process holds for one run: the /30 and the open handle whose OS lock IS the host-level reservation (null only on a host degraded to process-local uniqueness).</summary>
    private sealed record Reservation(string Cidr, FileStream? Handle);

    /// <summary>
    /// Take the host-level lock on <paramref name="cidr"/>. True — with a non-null handle — when this process now owns
    /// it; false when another LIVE process does (its handle still holds the lock, so the open is refused). A file left
    /// by a crashed or finished owner opens cleanly, which is what makes a released reservation immediately reusable.
    /// </summary>
    private bool TryReserve(string cidr, string runId, out FileStream? handle)
    {
        handle = null;

        var directory = ReservationDirectory;

        if (!HostLockingAvailable(directory)) return true;   // degraded: the in-process set alone decides, as it did before

        try { handle = new FileStream(ReservationPathFor(directory, cidr), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }

        StampOwner(handle, runId);

        return true;
    }

    /// <summary>Ensure the reservation directory exists. A host that cannot host it degrades to PROCESS-LOCAL uniqueness — the pre-existing behaviour — rather than failing every filtered-egress launch on a directory that is not the run's fault.</summary>
    private bool HostLockingAvailable(string directory)
    {
        if (!_hostReservationsUsable) return false;

        try { Directory.CreateDirectory(directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _hostReservationsUsable = false; }

        return _hostReservationsUsable;
    }

    /// <summary>Record who holds this /30 so an operator reading the directory can attribute it. DIAGNOSTIC ONLY — the OS lock, not the content, is the reservation, so a failed write must never fail the launch.</summary>
    private static void StampOwner(FileStream handle, string runId)
    {
        try
        {
            handle.SetLength(0);
            handle.Write(Encoding.UTF8.GetBytes($"pid={Environment.ProcessId} run={runId} at={DateTimeOffset.UtcNow:O}\n"));
            handle.Flush();
        }
        catch (IOException) { /* the lock is held regardless of what the file says */ }
    }

    private static string ReservationPathFor(string directory, string cidr) => Path.Combine(directory, cidr.Replace('/', '_').Replace('.', '-') + ".lease");

    /// <summary>Where <see cref="Host"/> reserves: a fixed leaf under the agent-run spool root, resolved through the SAME <c>DurableRoots</c> the spool itself uses, so every worker process sharing that root resolves the same directory and an operator who relocates the spool relocates the reservations. Resolved per acquire rather than at type init, so it reads the settings the deployment bound rather than whatever was current when this class was first touched.</summary>
    internal string ReservationDirectory => _directory ?? Path.Combine(DurableRoots.AgentRunSpool(RuntimeSettings.Current.AgentRunSpoolDirectory), ReservationLeaf);

    private static string CidrAt(int index)
    {
        var block4 = index % Block4Count;                                 // 0..63  → octet4 = block4*4
        var octet3 = index / Block4Count % Octet3Count;                   // 0..253 → +1
        var octet2 = index / Block4Count / Octet3Count % Octet2Count;     // 0..253 → +1
        return $"10.{octet2 + 1}.{octet3 + 1}.{block4 * 4}/30";
    }

    private static Lease LeaseFor(string cidr)
    {
        var slash = cidr.IndexOf('/');
        var lastDot = cidr.LastIndexOf('.');
        var prefix = cidr[..lastDot];                                     // 10.A.B
        var baseOctet = int.Parse(cidr[(lastDot + 1)..slash]);            // C (the /30 block base)
        return new Lease { Cidr = cidr, HostIp = $"{prefix}.{baseOctet + 1}", NsIp = $"{prefix}.{baseOctet + 2}" };
    }
}
