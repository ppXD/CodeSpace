using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Settings;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the collision-FREE /30 allocation (B3 stability). Two concurrently-active runs must NEVER share a subnet (a
/// host-global nft-chain hazard that would cross-widen or fail setup); the lease is well-formed; release frees it; and
/// re-acquiring the same runId is idempotent.
///
/// <para>The reservation is HOST-level, so "two runs" here means two runs in ANY worker process on the host, not two
/// inside one. Two allocator instances over one directory stand in for two worker processes — the OS file lock they
/// contend on is the same one two real processes would, which is the whole point: an in-memory set only made runs
/// inside ONE worker disjoint while the nft chain they collide in is shared by every process on the host.</para>
///
/// <para>Also pins the two postures of a host that holds no reservations, which are deliberately NOT the same: an
/// unusable DIRECTORY refuses the launch by name, an unenforced LOCK degrades to process-local uniqueness.</para>
/// </summary>
[Trait("Category", "Unit")]
public class EgressSubnetAllocatorTests : IDisposable
{
    private readonly string _reservations = Path.Combine(Path.GetTempPath(), "cs-egress-res-" + Guid.NewGuid().ToString("N"));

    private EgressSubnetAllocator NewWorker() => new(_reservations);

    public void Dispose()
    {
        try { Directory.Delete(_reservations, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void A_lease_is_a_well_formed_30_with_consecutive_host_and_ns_addresses()
    {
        var allocator = NewWorker();

        var lease = allocator.Acquire(Guid.NewGuid().ToString("N"));

        lease.Cidr.ShouldEndWith("/30");
        var baseOctet = int.Parse(lease.Cidr.Split('.')[3].Split('/')[0]);
        (baseOctet % 4).ShouldBe(0, "the /30 starts on a 4-aligned boundary");
        lease.HostIp.ShouldBe(lease.Cidr.Replace($".{baseOctet}/30", $".{baseOctet + 1}"), "host = block base + 1");
        lease.NsIp.ShouldBe(lease.Cidr.Replace($".{baseOctet}/30", $".{baseOctet + 2}"), "ns = block base + 2");
    }

    [Fact]
    public void Concurrent_runs_never_share_a_subnet_even_when_many_are_active()
    {
        // 500 simultaneously-held leases — the old 64k hash space birthday-collided at ~52% by 300; the allocator must
        // hand out 500 DISTINCT /30s with zero collision.
        var allocator = NewWorker();
        var runIds = Enumerable.Range(0, 500).Select(_ => Guid.NewGuid().ToString("N")).ToList();

        var cidrs = runIds.Select(id => allocator.Acquire(id).Cidr).ToList();

        cidrs.Distinct().Count().ShouldBe(500, "every concurrently-active run gets a unique /30 — no collision");
    }

    [Fact]
    public void Two_worker_processes_on_one_host_never_hand_out_the_same_subnet()
    {
        // THE defect this closes. Uniqueness used to live in one process's memory while the nftables forward chain it
        // protects is host-global — so on a multi-worker deployment two workers could hand out the same /30, and the
        // two netns then co-evaluated each other's packets (a cross-run egress WIDEN, or a failed `ip addr add`).
        var workerA = NewWorker();
        var workerB = NewWorker();

        var fromA = Enumerable.Range(0, 40).Select(_ => workerA.Acquire(Guid.NewGuid().ToString("N")).Cidr).ToList();
        var fromB = Enumerable.Range(0, 40).Select(_ => workerB.Acquire(Guid.NewGuid().ToString("N")).Cidr).ToList();

        workerA.HostReservationsUsable.ShouldBeTrue("this host CAN hold reservations, so the test must be exercising the real lock rather than the degraded in-process path");
        fromA.Intersect(fromB).ShouldBeEmpty("a /30 held by one worker process must be refused to every other worker process on the host");
        fromA.Concat(fromB).Distinct().Count().ShouldBe(80, "80 concurrently-held reservations across two processes are 80 distinct /30s");
    }

    [Fact]
    public void A_finished_reservation_leaves_its_file_behind_and_the_next_worker_reclaims_it()
    {
        var holder = NewWorker();
        var runId = Guid.NewGuid().ToString("N");
        var held = holder.Acquire(runId).Cidr;

        Directory.GetFiles(_reservations).ShouldHaveSingleItem("one held /30 is one reservation file");

        NewWorker().Acquire(Guid.NewGuid().ToString("N")).Cidr.ShouldNotBe(held, "while it is held, another worker process must not get it");

        var files = Directory.GetFiles(_reservations);

        holder.Release(runId);

        Directory.GetFiles(_reservations).ShouldBe(files, ignoreOrder: true,
            customMessage: "the file is deliberately NOT unlinked: deleting it would let a third process create a fresh inode for the same /30 and lock THAT while a second still held the old one");

        // The reclaim path is the SAME one a crashed worker exercises — the kernel closes a dead process's handles,
        // dropping its locks, and leaves exactly this file behind. No lease timer, no stale-PID sweep.
        NewWorker().Acquire(Guid.NewGuid().ToString("N")).Cidr.ShouldBe(held,
            "the freed /30 is reclaimed by the next worker rather than stranded behind the file its dead owner left");
    }

    [Fact]
    public void Releasing_is_idempotent_and_a_run_that_never_held_one_is_a_no_op()
    {
        var allocator = NewWorker();
        var runId = Guid.NewGuid().ToString("N");

        allocator.Acquire(runId);
        allocator.Release(runId);
        allocator.ActiveCount.ShouldBe(0, "release returns the /30 to the pool");

        // A second release (e.g. a reaper after the terminal path already freed it), and a release by a restarted
        // worker that never acquired it at all, are both no-ops.
        Should.NotThrow(() => allocator.Release(runId));
        Should.NotThrow(() => allocator.Release(Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void Re_acquiring_the_same_run_id_returns_the_same_lease()
    {
        var allocator = NewWorker();
        var runId = Guid.NewGuid().ToString("N");

        var first = allocator.Acquire(runId);
        var second = allocator.Acquire(runId);

        second.Cidr.ShouldBe(first.Cidr, "a setup retry / re-entry for the same run is stable, not a second reservation");
        allocator.ActiveCount.ShouldBe(1, "re-acquiring the same run reserves no second /30");
    }

    [Theory]
    [InlineData(false, "the reservation directory cannot be created")]   // its parent is a file — mkdir can never succeed
    [InlineData(true, "the reservation directory cannot be written")]    // it EXISTS and is 0500 — the case mkdir reported as success
    public void A_host_whose_reservation_directory_is_unusable_REFUSES_the_launch_rather_than_blaming_4096_live_runs(bool directoryExists, string expectedReason)
    {
        // FAIL-CLOSED, and it is the read-only case that forced the decision: Directory.CreateDirectory is a NO-OP on a
        // directory that already exists, so it reported success and every /30 probe then failed on the open — read as
        // "held by another live process". All 4096 candidates lost, and Acquire threw "this host already holds 4096
        // filtered-egress /30s": a true statement about nothing, pointing an operator at concurrency instead of at the
        // mount. Degrading instead would be no better: the directory sits under the SAME spool root this run's out.log
        // and exit marker need, so the run was already doomed — and the whole point of the host-level reservation is
        // that two runs must not co-evaluate each other's packets. So the launch is refused, by name.
        if (UnusableDirectory(directoryExists) is not { } directory) return;   // 0500 does not bite here (root / no POSIX modes) — unstageable

        var allocator = new EgressSubnetAllocator(directory);

        var refusal = Should.Throw<EgressSubnetReservationUnavailableException>(() => allocator.Acquire(Guid.NewGuid().ToString("N")));

        refusal.Reason.ShouldBe(expectedReason, "the cause named must be the mount, not imaginary contention");
        refusal.ReservationDirectory.ShouldBe(directory, "the actionable fact is WHICH directory an operator has to fix");
        allocator.HostReservationsUsable.ShouldBeFalse("asking the question must not answer an optimistic true the host has not earned");
        allocator.UnusableReason.ShouldBe(expectedReason);

        Should.Throw<EgressSubnetReservationUnavailableException>(() => allocator.Acquire(Guid.NewGuid().ToString("N")))
            .Reason.ShouldBe(expectedReason, "the refusal is sticky: a later acquire refuses with the SAME named cause rather than re-discovering it");
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "exclusive file locking is not enforced there")]
    public void The_allocator_probes_whether_this_host_enforces_the_lock_instead_of_assuming_it(bool locksAreEnforced, string? expectedReason)
    {
        // THE defect: "reserved" was inferred from an open that SUCCEEDED. .NET emulates FileShare.None on Unix with
        // an advisory flock(fd, LOCK_EX|LOCK_NB) and ignores every error but EWOULDBLOCK — so on a filesystem whose
        // flock answers ENOTSUP/EACCES (an NFS/RWX mount is exactly the shape this design recommends), or in a process
        // where .NET's file-locking switch is off, the handle came back UNLOCKED, two workers both "reserved" the same
        // /30, and HostReservationsUsable still read true. The allocator must find that out itself, at first use.
        var allocator = new EgressSubnetAllocator(_reservations, locksAreEnforced ? null : OpenerThatEnforcesNoLock);

        var cidrs = Enumerable.Range(0, 8).Select(_ => allocator.Acquire(Guid.NewGuid().ToString("N")).Cidr).ToList();

        allocator.HostReservationsUsable.ShouldBe(locksAreEnforced, "the flag must report what the host PROVED under a second open, not what the first open implied");
        allocator.UnusableReason.ShouldBe(expectedReason);
        cidrs.Distinct().Count().ShouldBe(8, "this is the ONE cause that degrades rather than refusing: the directory is writable and the runs are healthy, so the launch falls back to process-local uniqueness instead of taking every filtered-egress run on an NFS/RWX host down");
    }

    [Fact]
    public void A_rights_error_on_a_reservation_file_is_never_counted_as_another_workers_contention()
    {
        // flock(2) refuses with EWOULDBLOCK, which surfaces as an IOException sharing violation — never as a rights
        // error. So a UnauthorizedAccessException on a .lease means the directory turned unwritable under us (a
        // remount AFTER the probe), and counting it as "held" would again lose all 4096 candidates and report the
        // wrong cause. Staged through the opener seam because a mid-life remount cannot be staged in-process.
        var allocator = new EgressSubnetAllocator(_reservations, OpenerThatRefusesReservations);

        var refusal = Should.Throw<EgressSubnetReservationUnavailableException>(() => allocator.Acquire(Guid.NewGuid().ToString("N")));

        refusal.Reason.ShouldBe("the reservation directory cannot be written");
        refusal.InnerException.ShouldBeOfType<UnauthorizedAccessException>("the OS error an operator needs is carried, not swallowed");
        allocator.HostReservationsUsable.ShouldBeFalse();
    }

    [Fact]
    public void The_reservation_directory_is_0700_and_a_reservation_file_is_0600()
    {
        // A reservation file names the pid + runId holding a /30, and the directory lists every /30 this host holds.
        // Neither is another local user's business, so neither is left at whatever the umask happened to be.
        if (OperatingSystem.IsWindows()) return;

        NewWorker().Acquire(Guid.NewGuid().ToString("N"));

        File.GetUnixFileMode(_reservations).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, "the reservation directory must be 0700, not whatever the umask happened to be");
        File.GetUnixFileMode(Directory.GetFiles(_reservations).ShouldHaveSingleItem()).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite, "a reservation file must be 0600");
    }

    /// <summary>Stands in for a filesystem whose <c>flock(2)</c> refuses nobody — the share mode is simply not exclusive, which is what .NET silently leaves behind when flock answers anything but EWOULDBLOCK.</summary>
    private static FileStream OpenerThatEnforcesNoLock(string path) => new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

    /// <summary>Stands in for a directory that passed the probe and then turned unwritable: the probe file opens, every <c>.lease</c> is refused for RIGHTS.</summary>
    private static FileStream OpenerThatRefusesReservations(string path) =>
        path.EndsWith(".lease", StringComparison.Ordinal) ? throw new UnauthorizedAccessException(path) : new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    /// <summary>
    /// A reservation directory this process cannot hold reservations in, in one of the two shapes an operator can
    /// produce: one that can never be CREATED (its parent is a file), and one that already EXISTS and cannot be
    /// WRITTEN (0500 — the read-only mount, and the shape <c>Directory.CreateDirectory</c> reports success for). Null
    /// for the second shape on a host where the mode does not bite (running as root, a filesystem without POSIX
    /// modes), where it is unstageable and the test skips.
    /// </summary>
    private string? UnusableDirectory(bool exists)
    {
        if (!exists) return Path.Combine(TempFile(), "cannot", "exist");
        if (OperatingSystem.IsWindows()) return null;

        var directory = Path.Combine(_reservations, "read-only");
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try { File.WriteAllText(Path.Combine(directory, "probe"), ""); }
        catch (UnauthorizedAccessException) { return directory; }

        return null;
    }

    [Fact]
    public void The_hosts_reservations_live_under_the_shared_spool_root()
    {
        // The whole "host-level" claim rests on WHERE the reservation files are: the spool root is the one location a
        // deployment already shares between the worker replicas of a host (a volume in the compose file, an RWX mount
        // across hosts). Anywhere private to one container — the image's own filesystem, a temp dir — and two workers
        // would be locking files they cannot see each other's, which is the process-local uniqueness this replaced.
        using var settings = RuntimeSettings.Override(s => s with { AgentRunSpoolDirectory = "/tmp/cs-spool" });

        var directory = EgressSubnetAllocator.Host.ReservationDirectory;

        Path.GetDirectoryName(directory).ShouldBe("/tmp/cs-spool", customMessage: "the reservations must sit directly under the spool root the settings name — an operator who relocates the spool relocates them");
        Path.GetFileName(directory).ShouldStartWith(".", customMessage: "the leaf is dot-prefixed so it can never be mistaken for (or collide with) a run's own 32-hex spool directory");
    }

    /// <summary>A path that IS a file, so creating a directory under it cannot succeed on any platform.</summary>
    private static string TempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "cs-egress-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "");
        return path;
    }
}
