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

    [Fact]
    public void A_host_that_cannot_hold_reservations_degrades_to_process_local_uniqueness_rather_than_failing_the_launch()
    {
        // bwrap / nft absent is the ordinary case on a dev host, and a reservation directory that cannot be created
        // (a read-only mount, no rights) is not the run's fault. The no-op path must stay exactly as it was: runs
        // inside THIS process still get distinct /30s, and nothing throws.
        var unwritable = Path.Combine(TempFile(), "cannot", "exist");
        var allocator = new EgressSubnetAllocator(unwritable);

        var cidrs = Enumerable.Range(0, 8).Select(_ => allocator.Acquire(Guid.NewGuid().ToString("N")).Cidr).ToList();

        cidrs.Distinct().Count().ShouldBe(8, "process-local uniqueness — the pre-existing behaviour — must survive a host that cannot hold reservations");
        allocator.HostReservationsUsable.ShouldBeFalse("the degradation is recorded, so 'two workers may collide here' is assertable rather than silent");
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
