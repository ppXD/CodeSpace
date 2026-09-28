using CodeSpace.Core.Services.Agents.Sandbox.Exceptions;
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
/// <para>Also pins the three postures of a host that cannot take a reservation, which are deliberately NOT the same:
/// an unusable DIRECTORY refuses the launch by name, a lock unenforced ACROSS PROCESSES degrades to process-local
/// uniqueness, and a single unopenable <c>.lease</c> (another uid's, on a shared directory) is merely walked past.</para>
///
/// <para>And pins WHERE the /30s come from: 198.19.64.0–198.19.191.255, walked upward. Not 10/8, where pod, service
/// and peered-VPC networks sit and a run's veth route would shadow a real peer for the worker's own traffic; and not
/// the rest of 198.18.0.0/15, whose bottom fake-ip DNS proxies fill and whose top OrbStack holds.</para>
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
    public void The_first_lease_is_the_bottom_of_the_pool()
    {
        var lease = NewWorker().Acquire(Guid.NewGuid().ToString("N"));

        lease.Cidr.ShouldBe("198.19.64.0/30", "the walk starts at the bottom of 198.19.64.0–198.19.191.255");
        lease.HostIp.ShouldBe("198.19.64.1", "the host end of the veth is the block base + 1");
        lease.NsIp.ShouldBe("198.19.64.2", "the namespace end is the block base + 2");
    }

    [Fact]
    public void No_candidate_lies_where_a_worker_s_peers_or_its_own_host_already_are()
    {
        // A run's /30 becomes a connected route on the worker, more specific than anything it overlaps, so a candidate
        // inside a network the worker talks to shadows a real peer: the worker's own request bytes land in a sandbox.
        // 10/8 (where the pool used to be), 172.16/12 and 192.168/16 are pod, service, VPC and LAN space; 100.64/10 is
        // CGNAT, Tailscale and a cloud's own services; 169.254/16 is metadata and node-local DNS; 198.18.0.0/16 is where
        // fake-ip DNS proxies hand out addresses from the bottom; 198.19.192.0/18 is OrbStack's. Every /30, every one of
        // its four addresses — not a sample, which would pin only what it happened to pick.
        string[] occupied = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "100.64.0.0/10", "169.254.0.0/16", "198.18.0.0/16", "198.19.192.0/18"];

        EgressSubnetAllocator.CidrAt(0).ShouldBe("198.19.64.0/30", "the pool starts at 198.19.64.0");
        EgressSubnetAllocator.CidrAt(EgressSubnetAllocator.CandidateCount - 1).ShouldBe("198.19.191.252/30", "and its last /30 ends at 198.19.191.255");

        foreach (var address in EveryCandidateAddress())
            foreach (var range in occupied)
                Contains(range, address).ShouldBeFalse($"{Dotted(address)} is a candidate address inside {range}");
    }

    [Fact]
    public void No_candidate_is_an_address_an_allowlisted_host_can_resolve_to()
    {
        // The SSRF guard pins only globally-routable IPv4 into an allowlist. Were a candidate routable, a name resolving
        // to another run's /30 would be forwarded straight into that run's namespace; the guard's 198.18/15 rule is what
        // keeps the pool out, and this is the drift detector that says so if either side moves.
        foreach (var address in EveryCandidateAddress())
            EgressHostResolver.IsGloballyRoutableIpv4(new System.Net.IPAddress(Octets(address))).ShouldBeFalse($"{Dotted(address)} is a candidate address the egress allowlist would pin");
    }

    [Fact]
    public void A_30_the_host_already_routes_is_never_handed_out()
    {
        // The lock proves no live WORKER holds a /30; it cannot see the host's own network or a run whose namespace
        // outlived the worker that reserved it (its veth keeps the address, the lock died with the process). Here the
        // host routes the first candidate as a surviving run's /30 and the second inside its own /31 of a LAN.
        var routes = HostRoutedPrefixes.Parse("""[{"dst":"default","gateway":"172.17.0.1"},{"dst":"198.19.64.0/30","dev":"csh-survivor"},{"type":"local","dst":"198.19.64.5","dev":"eth9"}]""");

        var lease = NewWorker().Acquire(Guid.NewGuid().ToString("N"), routes);

        lease.Cidr.ShouldBe("198.19.64.8/30", "the surviving run's 198.19.64.0/30 and the /30 holding the host's own 198.19.64.5 are both skipped; the default route is no use of any one /30");
    }

    [Fact]
    public void A_broad_route_over_the_first_range_moves_the_walk_on_instead_of_refusing()
    {
        // A worker in a 198.19.64.0/18 network routes the first 4096 /30s the walk would consider — as many as the
        // concurrency bound. They must not count against it: 198.19.128.0/18 is free, and a launch refused there is a
        // refusal nothing forced.
        var routes = HostRoutedPrefixes.Parse("""[{"dst":"198.19.64.0/18","dev":"eth0"}]""");

        var lease = NewWorker().Acquire(Guid.NewGuid().ToString("N"), routes);

        lease.Cidr.ShouldBe("198.19.128.0/30");
    }

    [Fact]
    public void The_walks_jump_inverts_the_candidate_order_at_every_octet_boundary()
    {
        // The walk moves past a routed range by computing the first candidate above it, so that computation must be
        // the exact inverse of the order candidates are named in — a drift would skip free /30s or revisit routed ones.
        // Every candidate, so each /30 block, each third-octet wrap and the /18 midpoint are all covered.
        for (var index = 0; index < EgressSubnetAllocator.CandidateCount; index++)
        {
            var network = Address(EgressSubnetAllocator.CidrAt(index).Split('/')[0]);

            EgressSubnetAllocator.IndexAtOrAbove(network).ShouldBe(index, $"candidate {index} ({EgressSubnetAllocator.CidrAt(index)}) is its own first candidate");
            EgressSubnetAllocator.IndexAtOrAbove(network + 1).ShouldBe(index + 1, $"one address past candidate {index} is the next candidate");
        }

        EgressSubnetAllocator.IndexAtOrAbove(Address("198.19.63.255")).ShouldBe(0, "everything below the pool lies before its first candidate");
        EgressSubnetAllocator.IndexAtOrAbove(Address("10.254.254.253")).ShouldBe(0, "and so does the 10/8 the pool used to be");
        EgressSubnetAllocator.IndexAtOrAbove(Address("198.19.191.253")).ShouldBe(EgressSubnetAllocator.CandidateCount, "nothing lies above the last candidate");
        EgressSubnetAllocator.IndexAtOrAbove(1UL << 32).ShouldBe(EgressSubnetAllocator.CandidateCount, "nor past the end of IPv4, where a route ending at 255.255.255.255 puts the walk");
    }

    [Theory]
    [InlineData("""[{"dst":"198.19.0.0/17","dev":"eth0"}]""", "198.19.128.0/30")]                 // a broad route from below the pool over its lower half
    [InlineData("""[{"dst":"10.0.0.0/8","dev":"eth0"}]""", "198.19.64.0/30")]                     // a 10/8 pod or VPC network, where the walk used to start, is nowhere near the pool
    [InlineData("""[{"type":"blackhole","dst":"198.18.0.0/15"}]""", "198.19.64.0/30")]            // a null route discards traffic; it has no peers to shadow (a hardened host's bogon list)
    [InlineData("""[{"type":"unreachable","dst":"198.19.64.0/24"}]""", "198.19.64.0/30")]
    [InlineData("""[{"type":"unreachable","dst":"198.19.64.2"}]""", "198.19.64.4/30")]            // a banned /32 wins longest-prefix match over the run's /30 — it still occupies
    [InlineData("""[{"type":"blackhole","dst":"198.19.64.0/30"}]""", "198.19.64.4/30")]
    public void The_walk_passes_routed_ranges_whole_and_ignores_null_routes(string routesJson, string expected) =>
        NewWorker().Acquire(Guid.NewGuid().ToString("N"), HostRoutedPrefixes.Parse(routesJson)).Cidr.ShouldBe(expected);

    [Fact]
    public void A_host_that_routes_everything_this_worker_does_not_hold_blames_the_routes_not_4096_live_runs()
    {
        // A worker holding one live /30 gains a route over all of 198.18.0.0/15 (a VPN, or a fake-ip proxy's TUN taking
        // the whole block). The walk finds its own /30 and nothing else it may try; the wall is the routes, and the
        // message must say so rather than claim 4096. The live /30 sits past the first candidate, so the walk jumps
        // over it — the count must still include it.
        var allocator = NewWorker();
        var live = allocator.Acquire(Guid.NewGuid().ToString("N"), HostRoutedPrefixes.Parse("""[{"dst":"198.19.64.0/30"}]"""));

        var refusal = Should.Throw<InvalidOperationException>(() => allocator.Acquire(Guid.NewGuid().ToString("N"), HostRoutedPrefixes.Parse("""[{"dst":"198.18.0.0/15"}]""")));

        refusal.Message.ShouldContain("does not already route");
        refusal.Message.ShouldContain("this worker holds 1");
    }

    [Fact]
    public void Running_out_of_30s_is_a_failed_setup_the_caller_can_type_not_an_exception_that_escapes_it()
    {
        // A host with no /30 free fails the setup the way every other setup step does: a failed result carrying the
        // allocator's own reason, which the durable launch aborts the run with as a fail-closed setup failure
        // (LocalProcessRunner.Durable.cs) and the synchronous RunAsync hands back as its Outcome. Thrown past that
        // result, it would escape both as a bare exception instead.
        var (subnet, exhausted) = FilteredEgressNetns.Reserve(NewWorker(), Guid.NewGuid().ToString("N"), HostRoutedPrefixes.Parse("""[{"dst":"198.18.0.0/15"}]"""));

        subnet.ShouldBeNull();
        exhausted.ShouldNotBeNull().ShouldContain("does not already route", customMessage: "the setup failure carries the allocator's own account of why");
    }

    private static ulong Address(string ip) => System.Net.IPAddress.Parse(ip).GetAddressBytes().Aggregate(0UL, (value, octet) => value << 8 | octet);

    /// <summary>Every address of every /30 the allocator can name — its network, both ends of the veth and its broadcast.</summary>
    private static IEnumerable<ulong> EveryCandidateAddress() =>
        Enumerable.Range(0, EgressSubnetAllocator.CandidateCount).SelectMany(index => Enumerable.Range(0, 4).Select(offset => Address(EgressSubnetAllocator.CidrAt(index).Split('/')[0]) + (ulong)offset));

    /// <summary>Whether <paramref name="address"/> lies inside <paramref name="prefix"/> — the test's own arithmetic, so the pin does not lean on the parser it sits beside.</summary>
    private static bool Contains(string prefix, ulong address)
    {
        var length = int.Parse(prefix.Split('/')[1]);

        return address >> (32 - length) == Address(prefix.Split('/')[0]) >> (32 - length);
    }

    private static byte[] Octets(ulong address) => [(byte)(address >> 24), (byte)(address >> 16), (byte)(address >> 8), (byte)address];

    private static string Dotted(ulong address) => new System.Net.IPAddress(Octets(address)).ToString();

    [Theory]
    [InlineData("0.0.0.0/1", "10.1.1.0/30")]         // a VPN's default override — a default in all but name
    [InlineData("128.0.0.0/1", "198.19.64.0/30")]    // its other half, which covers the whole pool
    public void A_route_broader_than_a_slash_8_is_a_default_not_a_network(string routed, string inside) =>
        HostRoutedPrefixes.Parse($$"""[{"dst":"{{routed}}"}]""").Overlaps(inside).ShouldBeFalse();

    [Theory]
    [InlineData("""[{"dst":"198.18.0.0/15","dev":"vpn0"}]""", "198.18.0.0/15")]                                                            // one route over the whole block
    [InlineData("""[{"type":"local","dst":"198.19.64.1","dev":"eth0"},{"dst":"198.19.0.0/16","dev":"eth0"}]""", "198.19.0.0/16")]          // a worker network numbered from 198.19.0.0/16, listed after the worker's own address inside it: the wider route is the wall
    [InlineData("""[{"dst":"198.19.0.0/16","dev":"eth0"},{"type":"local","dst":"198.19.64.1","dev":"eth0"}]""", "198.19.0.0/16")]          // and listed before it: whichever order the host prints them in
    [InlineData("""[{"dst":"198.19.64.0/18","dev":"eth0"},{"dst":"198.19.128.0/18","dev":"eth1"}]""", "198.19.64.0/18, 198.19.128.0/18")]  // two, each the size of the concurrency bound
    public void A_host_that_routes_every_candidate_says_so_instead_of_blaming_4096_live_runs(string routesJson, string named)
    {
        var routes = HostRoutedPrefixes.Parse(routesJson);

        var refusal = Should.Throw<InvalidOperationException>(() => NewWorker().Acquire(Guid.NewGuid().ToString("N"), routes));

        refusal.Message.ShouldContain("does not already route", customMessage: "the wall is the host's own routes, not concurrency");
        refusal.Message.ShouldContain("198.19.64.0–198.19.191.255", customMessage: "and it names the range an operator has to leave unrouted");
        refusal.Message.ShouldEndWith($": {named}.", customMessage: "and the routes that cover it, as the host lists them, so an operator can find each in `ip route show table all` instead of hunting for it — the widest over each stretch the walk passed, not every address inside them");
    }

    [Theory]
    [InlineData("198.19.64.0/30", "198.19.64.0/30", true)]    // the same /30 — a survivor's
    [InlineData("198.19.0.0/16", "198.19.64.4/30", true)]     // a network containing it
    [InlineData("198.19.64.6", "198.19.64.4/30", true)]       // a single address inside it (no length ⇒ /32)
    [InlineData("198.19.64.8/30", "198.19.64.4/30", false)]   // the next /30 over
    [InlineData("192.168.0.0/16", "198.19.64.4/30", false)]
    [InlineData("10.0.0.0/8", "198.19.64.4/30", false)]       // a 10/8 network no longer shadows a run
    public void A_candidate_overlaps_any_prefix_sharing_an_address_with_it(string routed, string candidate, bool expected) =>
        HostRoutedPrefixes.Parse($$"""[{"dst":"{{routed}}"}]""").Overlaps(candidate).ShouldBe(expected);

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
        if (UnusableDirectory(directoryExists) is not { } directory) return;   // vacuous pass, not a skip: 0500 never bites root / no-POSIX-modes hosts (e.g. CI running as root)

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
    [InlineData(true, nameof(CrossProcessLockProbe.Verdict.Unenforced), null, 0)]
    [InlineData(false, nameof(CrossProcessLockProbe.Verdict.Enforced), null, 1)]
    [InlineData(false, nameof(CrossProcessLockProbe.Verdict.Unenforced), "exclusive file locking is not enforced there", 1)]
    [InlineData(false, nameof(CrossProcessLockProbe.Verdict.Unproven), "exclusive file locking could not be proven across processes", 1)]
    public void The_allocator_proves_locking_across_PROCESSES_instead_of_assuming_it(bool refusedInProcess, string fromChild, string? expectedReason, int childAsks)
    {
        // THE defect, in both halves. (1) "reserved" was inferred from an open that SUCCEEDED: .NET emulates
        // FileShare.None on Unix with an advisory flock(fd, LOCK_EX|LOCK_NB) and ignores every error but EWOULDBLOCK,
        // so wherever that call cannot lock the handle came back UNLOCKED, two workers both "reserved" the same /30,
        // and HostReservationsUsable still read true. (2) A SECOND OPEN FROM THIS PROCESS cannot settle it either: on
        // a Linux NFS client flock() is emulated with per-PROCESS fcntl byte-range locks (flock(2) NOTES), which never
        // conflict with their own process — so the in-process answer libels exactly the NFS/RWX mount this design
        // recommends, whose CROSS-process locking works. It is the free FAST PATH (row 1: refused in-process ⇒ flock
        // proper ⇒ no child at all) and nothing more; a real second process settles the rest.
        var verdict = Verdict(fromChild);
        var childAsked = 0;
        var allocator = new EgressSubnetAllocator(_reservations, refusedInProcess ? null : OpenerThatEnforcesNoLock, _ => { childAsked++; return verdict; });

        var cidrs = Enumerable.Range(0, 8).Select(_ => allocator.Acquire(Guid.NewGuid().ToString("N")).Cidr).ToList();

        allocator.HostReservationsUsable.ShouldBe(expectedReason is null, "the flag must report what a SECOND PROCESS proved, not what this one's own second open implied");
        allocator.UnusableReason.ShouldBe(expectedReason, "a mount that does not lock and a probe that could not run are the same posture but not the same operator fix, so they are not the same sentence");
        childAsked.ShouldBe(childAsks, "the child is asked ONCE per worker, and only where the in-process fast path came back unrefused");
        cidrs.Distinct().Count().ShouldBe(8, "these are the causes that degrade rather than refusing: the directory is writable and the runs are healthy, so the launch falls back to process-local uniqueness instead of taking every filtered-egress run on the host down");
    }

    [Fact]
    public void A_non_IOException_on_the_second_open_is_not_read_as_the_lock_refusing_it()
    {
        // SecondOpenIsRefused proves "refused" only for a SHARING VIOLATION (IOException) — the OS actually saying
        // the lock is held. Before this fix any exception at all was read the same way, so a second open that fails
        // for an unrelated reason (an UnauthorizedAccessException, say) declared Enforced without a second PROCESS
        // ever being asked — the exact optimism this whole probe exists to refuse.
        var childAsked = 0;
        var opener = new OpenerThatFailsTheProbesSecondOpenWithANonIOException();
        var allocator = new EgressSubnetAllocator(_reservations, opener.Open, _ => { childAsked++; return CrossProcessLockProbe.Verdict.Unproven; });

        allocator.Acquire(Guid.NewGuid().ToString("N"));

        childAsked.ShouldBe(1, "a non-IOException on the second open proves nothing — the child must still be asked rather than declaring Enforced on faith");
        allocator.UnusableReason.ShouldBe("exclusive file locking could not be proven across processes");
    }

    [Theory]
    [InlineData(true, nameof(CrossProcessLockProbe.Verdict.Enforced))]
    [InlineData(false, nameof(CrossProcessLockProbe.Verdict.Unenforced))]
    [InlineData(null, nameof(CrossProcessLockProbe.Verdict.Unproven))]   // the probed path does not exist — the child cannot even attempt the open
    public void The_cross_process_probe_really_spawns_a_second_process_and_reports_what_it_was_told(bool? heldByThisProcess, string expected)
    {
        // HIGH-fidelity (Rule 12.4): the real bundled bootstrap, resolved the way the durable launch resolves it, its
        // real argv contract, its real token, and the real kernel answer — everything the seam above can only assume.
        // All three rows matter: "enforced" has to be earned from a lock this process actually holds, not returned by
        // a child that answers the same way whatever it finds — and a path that was never created must come back
        // Unproven, not an optimistic guess in either direction.
        Directory.CreateDirectory(_reservations);

        var path = Path.Combine(_reservations, ".probe-real-" + Guid.NewGuid().ToString("N"));

        if (heldByThisProcess is not null) File.WriteAllText(path, "");

        using var held = heldByThisProcess == true ? new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;

        CrossProcessLockProbe.Ask(path).ShouldBe(Verdict(expected),
            customMessage: $"the bundled bootstrap did not answer as expected for '{path}' (held by this process: {heldByThisProcess}). Run it by hand: <output>/runner-host/codespace-runner-host --probe-lock <path> — it prints lock-refused / lock-granted / lock-unknown");
    }

    /// <summary>The verdict a row names. Carried through <c>InlineData</c> as its NAME because the seam's enum is internal to the assembly under test and cannot appear in a public test signature; a typo throws here rather than passing something else.</summary>
    private static CrossProcessLockProbe.Verdict Verdict(string name) => Enum.Parse<CrossProcessLockProbe.Verdict>(name);

    [Fact]
    public void A_reservation_file_this_worker_cannot_open_is_SKIPPED_not_read_as_a_host_that_cannot_reserve()
    {
        // A rights error on ONE .lease is neither contention (flock refuses with EWOULDBLOCK, an IOException sharing
        // violation) nor a host that cannot reserve. On a shared reservation directory whose workers run under
        // different uids — the deployment TryPrepareDirectory deliberately protects — another worker's 0600 .lease
        // fails EACCES at open(2), before flock is even reached. Refusing there took THIS worker out for its entire
        // process lifetime, on its first acquire, over a file somebody else legitimately holds. Over-holding is the
        // safe direction: walk past it. Staged through the opener seam because a second uid cannot be staged in-process.
        var opener = new OpenerThatRefusesAnotherUidsLeases();
        var allocator = new EgressSubnetAllocator(_reservations, opener.Open, NeverAskedForAChild);

        allocator.Acquire(Guid.NewGuid().ToString("N")).Cidr.ShouldBe("198.19.64.8/30", "the two unopenable candidates are walked past — the probe walks 198.19.64.0/30, 198.19.64.4/30, 198.19.64.8/30");
        allocator.Acquire(Guid.NewGuid().ToString("N")).Cidr.ShouldBe("198.19.64.12/30", "and it is not sticky: the next run reserves the next free /30 instead of being refused a host-wide reservation");

        allocator.HostReservationsUsable.ShouldBeTrue("a file THIS uid cannot open is not this HOST failing to hold reservations");
        opener.Refusals.ShouldBe(4, "the staging must actually have fired — twice per acquire, since a foreign file is re-attempted rather than remembered");
    }

    [Theory]
    // EACCES → UnauthorizedAccessException; EROFS → IOException (a remount-ro is NOT a rights error, and reading only
    // for the rights error left the misreport reachable). Each with and without the routes production always passes:
    // a worker in 198.19.64.0/24 skips those /30s as routed, and must still be told it is the mount, not its routes.
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void A_directory_that_turns_unwritable_after_the_probe_names_the_MOUNT_not_4096_live_runs(bool rightsError, bool lanRoutes)
    {
        // Every candidate lost the same way can be 4096 live /30s — or a mount that went read-only after the probe.
        // Indistinguishable from inside the loop, and guessing "contention" reported "this host already holds 4096
        // filtered-egress /30s": a true sentence about nothing, pointing an operator at concurrency instead of at the
        // mount. So the bottom of the loop re-probes with a FRESH file, which nothing else can hold, and reports what
        // that proves — for either errno, since a remount-ro arrives as EROFS rather than EACCES.
        var allocator = new EgressSubnetAllocator(_reservations, new OpenerThatBreaksAfterTheProbe(rightsError).Open, NeverAskedForAChild);

        var routes = lanRoutes ? HostRoutedPrefixes.Parse("""[{"dst":"198.19.64.0/24","dev":"eth0"},{"type":"local","dst":"198.19.64.20","dev":"eth0"}]""") : null;

        var refusal = Should.Throw<EgressSubnetReservationUnavailableException>(() => allocator.Acquire(Guid.NewGuid().ToString("N"), routes));

        refusal.Reason.ShouldBe("the reservation directory cannot be written", "the cause named must be the mount, not imaginary contention");
        refusal.ReservationDirectory.ShouldBe(_reservations, "the actionable fact is WHICH directory an operator has to fix");
        refusal.InnerException.ShouldBeOfType(rightsError ? typeof(UnauthorizedAccessException) : typeof(IOException), "the OS error an operator needs is carried, not swallowed");
        allocator.HostReservationsUsable.ShouldBeFalse("the re-probe's verdict is recorded, so a host that has genuinely gone read-only refuses immediately from here on");
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

    /// <summary>A real exclusive open — what every worker process on the host issues.</summary>
    private static FileStream Exclusive(string path) => new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    /// <summary>The cross-process probe as it must be seen by a test whose in-process second open is already refused: never spawned. Failing loudly beats a silent child.</summary>
    private static CrossProcessLockProbe.Verdict NeverAskedForAChild(string path) =>
        throw new ShouldAssertException($"the in-process fast path answered for {path}; no child process should have been spawned");

    /// <summary>Stands in for a second open that fails for a reason UNRELATED to the lock (e.g. an UnauthorizedAccessException from something else touching the probe file) rather than the sharing violation (IOException) that actually proves refusal. Only the probe's own file behaves this way, so a real reservation open still succeeds.</summary>
    private sealed class OpenerThatFailsTheProbesSecondOpenWithANonIOException
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public FileStream Open(string path)
        {
            if (!path.Contains(".probe-", StringComparison.Ordinal)) return Exclusive(path);
            if (_seen.Add(path)) return Exclusive(path);

            throw new UnauthorizedAccessException(path);
        }
    }

    /// <summary>
    /// Stands in for the multi-uid shared reservation directory: the first two <c>.lease</c> files it is asked for
    /// belong to ANOTHER uid — 0600, so <c>open(2)</c> fails EACCES before flock — and stay unopenable for the rest
    /// of this worker's life. Stateful because "whose file is this" is not a property of the path, and a mid-life
    /// second uid cannot be staged in-process.
    /// </summary>
    private sealed class OpenerThatRefusesAnotherUidsLeases
    {
        private readonly HashSet<string> _anotherUids = new(StringComparer.Ordinal);

        /// <summary>How often a foreign file was actually refused, so a green cannot come from staging that never fired.</summary>
        public int Refusals { get; private set; }

        public FileStream Open(string path)
        {
            if (!path.EndsWith(".lease", StringComparison.Ordinal)) return Exclusive(path);

            if (_anotherUids.Count < 2) _anotherUids.Add(path);

            if (!_anotherUids.Contains(path)) return Exclusive(path);

            Refusals++;

            throw new UnauthorizedAccessException(path);
        }
    }

    /// <summary>Stands in for a directory that passed the probe and then went read-only under us: the first probe file opens, and every open from the first <c>.lease</c> onwards — the later probe files included — fails for rights (EACCES) or because the mount is read-only (EROFS).</summary>
    private sealed class OpenerThatBreaksAfterTheProbe(bool rightsError)
    {
        private bool _remounted;

        public FileStream Open(string path)
        {
            if (path.EndsWith(".lease", StringComparison.Ordinal)) _remounted = true;

            if (!_remounted) return Exclusive(path);

            throw rightsError ? new UnauthorizedAccessException(path) : (Exception)new IOException(path);
        }
    }

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
