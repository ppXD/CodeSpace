using System.Text;
using CodeSpace.Core.Settings;
using Serilog;

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
/// changes nothing about the crash-resume teardown contract.</para>
///
/// <para><b>A host whose reservation DIRECTORY is unusable REFUSES the launch; one that cannot ENFORCE the lock
/// degrades to process-local uniqueness.</b> Two different facts, two different postures. A directory that can be
/// neither created nor written (a read-only mount, no rights) makes every acquire throw
/// <see cref="EgressSubnetReservationUnavailableException"/> naming it and the OS cause — fail-closed, like everything
/// else on this path: <c>FilteredEgressNetns</c> already aborts a run rather than launch it unfiltered, and
/// <see cref="Acquire"/> already aborted rather than share a /30 with a live run. It costs no availability that was
/// not already gone: the directory sits under the same spool root the run's own <c>out.log</c> / <c>exit</c> marker
/// need, so that run was going to fail regardless — and the alternative was worse than a failure. A rights error read
/// as another worker's contention (which <c>flock</c> NEVER expresses that way) lost every candidate /30 the same way
/// and then reported "this host already holds 4096 filtered-egress /30s": a true sentence about nothing, pointing an
/// operator at concurrency instead of at the mount. The refusal is sticky — a host that proved unusable stays refused
/// until the worker restarts, as the degradation already was.</para>
///
/// <para>The one cause that DEGRADES instead is a filesystem that does not enforce the lock: .NET emulates
/// <see cref="FileShare.None"/> on Unix with an advisory <c>flock(fd, LOCK_EX|LOCK_NB)</c> and IGNORES every error but
/// <c>EWOULDBLOCK</c>, so on a mount whose <c>flock</c> answers <c>ENOTSUP</c> — an NFS/RWX volume, which is exactly
/// the shape this design recommends — the handle comes back UNLOCKED. There the directory IS writable and the runs
/// themselves are healthy, so the allocator falls back to the PROCESS-LOCAL uniqueness it had before the reservation
/// existed and says so at Warning level, rather than taking every filtered-egress launch on the host down over a
/// property of the mount. What must never happen is <see cref="HostReservationsUsable"/> reading <c>true</c> while
/// nothing is reserved, so the lock is SELF-PROBED once at first use — two opens of one file from this process, which
/// is exactly what two workers do — instead of assumed.</para>
///
/// <para>One reservation can OVER-hold: the lock lives on the acquiring worker's fd, so when a run is torn down by a
/// DIFFERENT worker (a reaper after a crash/resume) that worker's <see cref="Release"/> is a no-op and the /30 stays
/// reserved until the original holder's process exits. That is the SAFE direction — a /30 held slightly too long is a
/// /30 nobody double-hands-out — and needs no reaper: the kernel drops the lock when the holder dies.</para>
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

    /// <summary>A reservation directory this process creates is 0700 — the list of /30s this host holds is not another local user's business.</summary>
    private const UnixFileMode OwnerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>A reservation file carries the holder's pid + runId, so it is 0600 rather than whatever the umask happened to be.</summary>
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Why this host holds no reservations. Fixed literals — one per cause — so the refusal, the Warning and the tests name the same thing. The first two REFUSE every acquire; the third degrades.</summary>
    private const string DirectoryUncreatable = "the reservation directory cannot be created";
    private const string DirectoryUnwritable = "the reservation directory cannot be written";
    private const string LockingUnenforced = "exclusive file locking is not enforced there";

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
    private readonly Func<string, FileStream> _open;

    private bool _probed;
    private string? _degradation;
    private (string Reason, Exception? Cause)? _refusal;

    /// <summary>The allocator every launch on this host reserves through — the one whose reservation directory is shared by every worker PROCESS, which is what makes the reservation host-level.</summary>
    public static EgressSubnetAllocator Host { get; } = new();

    /// <summary>Pass a directory to reserve in one (a test standing in for a host) and/or an opener that stands in for a filesystem this process cannot stage (one that does not enforce the lock, one that refuses the write); omit both to resolve the deployment's own directory and take a real exclusive lock, which is what every worker process on the host does.</summary>
    internal EgressSubnetAllocator(string? reservationDirectory = null, Func<string, FileStream>? opener = null)
    {
        _directory = reservationDirectory;
        _open = opener ?? OpenExclusive;
    }

    /// <summary>False once this host proved it cannot hold host-level reservations — either because the directory is unusable (every acquire then REFUSES) or because the lock is not enforced there (the allocator then degrades to process-local uniqueness and two worker processes on this host CAN collide). Exposed so both are assertable rather than silent. Reading it PROBES if nothing has yet, so it never answers an optimistic <c>true</c> the host has not earned.</summary>
    internal bool HostReservationsUsable { get { lock (_lock) { EnsureProbed(); return _degradation is null && _refusal is null; } } }

    /// <summary>Which of the three causes it was — in the same words the refusal and the Warning use — or null while this host still reserves host-wide.</summary>
    internal string? UnusableReason { get { lock (_lock) { EnsureProbed(); return _degradation ?? _refusal?.Reason; } } }

    /// <summary>How a reservation is actually held: an open handle whose <see cref="FileShare.None"/> share mode the OS turns into an exclusive lock (an advisory <c>flock(2)</c> on Unix). The lock dies with the handle, which is what makes a crashed worker's reservations self-releasing.</summary>
    private static FileStream OpenExclusive(string path) => new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    /// <summary>
    /// Reserve a /30 no other run on this HOST holds — probing from the lowest index up, so a released reservation is
    /// reused rather than the space being spread thin. Idempotent: re-acquiring the same runId returns its existing
    /// lease, so a setup retry / re-entry is stable — and it answers BEFORE the gate below, so a run already holding a
    /// /30 keeps it even after the directory turns unusable under it.
    ///
    /// <para>Throws <see cref="EgressSubnetReservationUnavailableException"/> when this host cannot hold reservations
    /// at all, from the gate rather than from inside the loop, so a refusal can never be reported as the exhaustion at
    /// the bottom.</para>
    /// </summary>
    public Lease Acquire(string runId)
    {
        lock (_lock)
        {
            if (_byRun.TryGetValue(runId, out var held)) return LeaseFor(held.Cidr);

            EnsureHostCanReserve();

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
    /// it; false ONLY when another LIVE process does (its handle still holds the lock, so the open is refused). A file
    /// left by a crashed or finished owner opens cleanly, which is what makes a released reservation immediately
    /// reusable. True with a NULL handle on a degraded host: the in-process set alone decides there, as it did before
    /// the reservation existed. Throws when the directory turns unwritable between the probe and here.
    /// </summary>
    private bool TryReserve(string cidr, string runId, out FileStream? handle)
    {
        handle = null;

        if (_degradation is not null) return true;   // degraded: the in-process set alone decides, as it did before

        var path = ReservationPathFor(ReservationDirectory, cidr);

        try { handle = _open(path); }
        catch (IOException) { return false; }        // refused: another LIVE process's handle holds this /30's lock
        catch (UnauthorizedAccessException ex)
        {
            // NOT contention. A lock refusal is EWOULDBLOCK, which surfaces as an IOException sharing violation —
            // never as a rights error — so this is the directory having turned unwritable under us (a remount after
            // the probe). Read as "held", it would lose EVERY candidate and then blame 4096 imaginary live runs.
            Refuse(DirectoryUnwritable, ex);

            throw Refusal();
        }

        RestrictToOwner(path, OwnerOnlyFile);
        StampOwner(handle, runId);

        return true;
    }

    /// <summary>
    /// FAIL-CLOSED gate, once per acquire: a host whose reservation directory cannot hold reservations refuses the
    /// launch — naming the directory and the OS cause — rather than handing out a /30 nothing reserved. Probes this
    /// host first if nothing has yet.
    /// </summary>
    private void EnsureHostCanReserve()
    {
        EnsureProbed();

        if (_refusal is not null) throw Refusal();
    }

    /// <summary>
    /// Establish ONCE, at first use, whether this host can hold host-level reservations — and record WHICH of the three
    /// ways it cannot. Assuming it can is what made the flag lie: .NET emulates <see cref="FileShare.None"/> on Unix
    /// with an advisory <c>flock(fd, LOCK_EX|LOCK_NB)</c> and IGNORES every error but <c>EWOULDBLOCK</c>, so on a
    /// filesystem that answers <c>ENOTSUP</c>/<c>EACCES</c> — or in a process where .NET's file-locking switch is off —
    /// the handle comes back UNLOCKED and two workers both "reserve" the same /30 while the flag still reads true. It
    /// only RECORDS: the refusal is thrown by <see cref="EnsureHostCanReserve"/>, so reading
    /// <see cref="HostReservationsUsable"/> can probe without throwing at a caller that only asked a question.
    /// </summary>
    private void EnsureProbed()
    {
        if (_probed) return;

        _probed = true;

        var directory = ReservationDirectory;

        if (TryPrepareDirectory(directory)) ProbeExclusiveLocking(directory);
    }

    /// <summary>Create the reservation directory (0700 when this process is the one creating it). Its absence and its unwritability are the same fact to an operator, so both refuse.</summary>
    private bool TryPrepareDirectory(string directory)
    {
        var existed = Directory.Exists(directory);

        try { Directory.CreateDirectory(directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Refuse(DirectoryUncreatable, ex);

            return false;
        }

        // Only what this process CREATED. Re-restricting a directory that was already there would take a deployment
        // whose workers run under different uids from working to degraded, and would buy only listing privacy — a
        // reservation's own bytes are 0600 either way.
        if (!existed) RestrictToOwner(directory, OwnerOnlyDirectory);

        return true;
    }

    /// <summary>
    /// Open ONE probe file TWICE from this process. Two separate <c>open()</c>s are exactly what two worker processes
    /// issue and exactly what <c>flock(2)</c> conflicts on, so a second open that is REFUSED proves the reservation
    /// means what it says, and one that SUCCEEDS proves nothing is enforcing it. The probe file is named per-probe and
    /// unlinked afterwards, so a concurrent worker's probe can neither be mistaken for a reservation nor delete ours
    /// between the two opens. The FIRST open is also how an existing-but-unwritable directory is caught: nothing else
    /// can hold a freshly-named file, so a failure there is the mount, never contention — whatever errno it wore
    /// (<c>EACCES</c> arrives as <see cref="UnauthorizedAccessException"/>, <c>EROFS</c> as <see cref="IOException"/>).
    /// </summary>
    private void ProbeExclusiveLocking(string directory)
    {
        var path = Path.Combine(directory, ".probe-" + Guid.NewGuid().ToString("N"));

        FileStream held;

        try { held = _open(path); }
        catch (Exception ex) { Refuse(DirectoryUnwritable, ex); return; }

        using (held)
        {
            if (!SecondOpenIsRefused(path)) DegradeToProcessLocal(directory);
        }

        try { File.Delete(path); } catch { /* best-effort: a stray probe file is inert — it is not a .lease */ }
    }

    private bool SecondOpenIsRefused(string path)
    {
        try { using var second = _open(path); }
        catch { return true; }

        return false;
    }

    /// <summary>Record + announce the ONE cause that degrades rather than refusing — this filesystem does not enforce the lock, so the allocator falls back to the PROCESS-LOCAL uniqueness it had before the reservation existed and two worker processes on this host can hand out the same /30. Nothing throws here, so the Warning is the only way it becomes visible.</summary>
    private void DegradeToProcessLocal(string directory)
    {
        _degradation = LockingUnenforced;

        Log.Warning("Filtered-egress /30 reservations under {ReservationDirectory} degraded to process-local uniqueness: {DegradationReason}. Two worker processes on this host can now hand out the same subnet", directory, LockingUnenforced);
    }

    /// <summary>Remember the fail-closed cause, so every later acquire refuses with the SAME named reason instead of re-discovering it — as 4096 imaginary live runs, which is what reading a rights error as contention did.</summary>
    private void Refuse(string reason, Exception? cause) => _refusal = (reason, cause);

    /// <summary>The refusal to throw: it names the directory an operator has to fix and carries the OS error underneath it.</summary>
    private EgressSubnetReservationUnavailableException Refusal() => new(ReservationDirectory, _refusal!.Value.Reason, _refusal!.Value.Cause);

    /// <summary>Restrict a reservation file/directory to this uid. BEST-EFFORT: the lock, not the mode, is the reservation, and a path created by a sibling worker under a different uid must not fail this one's launch.</summary>
    private static void RestrictToOwner(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows()) return;

        try { File.SetUnixFileMode(path, mode); } catch { /* best-effort — see summary */ }
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
