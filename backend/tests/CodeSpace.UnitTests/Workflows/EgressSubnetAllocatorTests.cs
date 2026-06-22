using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the collision-FREE /30 allocation (B3 stability). Two concurrently-active runs must NEVER share a subnet (a
/// host-global nft-chain hazard that would cross-widen or fail setup); the lease is well-formed; release frees it; and
/// re-acquiring the same runId is idempotent.
/// </summary>
[Trait("Category", "Unit")]
public class EgressSubnetAllocatorTests
{
    [Fact]
    public void A_lease_is_a_well_formed_30_with_consecutive_host_and_ns_addresses()
    {
        var runId = Guid.NewGuid().ToString("N");
        try
        {
            var lease = EgressSubnetAllocator.Acquire(runId);

            lease.Cidr.ShouldEndWith("/30");
            var baseOctet = int.Parse(lease.Cidr.Split('.')[3].Split('/')[0]);
            (baseOctet % 4).ShouldBe(0, "the /30 starts on a 4-aligned boundary");
            lease.HostIp.ShouldBe(lease.Cidr.Replace($".{baseOctet}/30", $".{baseOctet + 1}"), "host = block base + 1");
            lease.NsIp.ShouldBe(lease.Cidr.Replace($".{baseOctet}/30", $".{baseOctet + 2}"), "ns = block base + 2");
        }
        finally { EgressSubnetAllocator.Release(runId); }
    }

    [Fact]
    public void Concurrent_runs_never_share_a_subnet_even_when_many_are_active()
    {
        // 500 simultaneously-held leases — the old 64k hash space birthday-collides at ~52% by 300; the allocator must
        // hand out 500 DISTINCT /30s with zero collision.
        var runIds = Enumerable.Range(0, 500).Select(_ => Guid.NewGuid().ToString("N")).ToList();
        try
        {
            var cidrs = runIds.Select(EgressSubnetAllocator.Acquire).Select(l => l.Cidr).ToList();

            cidrs.Distinct().Count().ShouldBe(500, "every concurrently-active run gets a unique /30 — no collision");
        }
        finally { foreach (var id in runIds) EgressSubnetAllocator.Release(id); }
    }

    [Fact]
    public void Releasing_frees_the_subnet_for_reuse()
    {
        var a = Guid.NewGuid().ToString("N");
        var before = EgressSubnetAllocator.ActiveCount;

        var lease = EgressSubnetAllocator.Acquire(a);
        EgressSubnetAllocator.ActiveCount.ShouldBe(before + 1);

        EgressSubnetAllocator.Release(a);
        EgressSubnetAllocator.ActiveCount.ShouldBe(before, "release returns the /30 to the pool");

        // Release is idempotent — a second release (e.g. a reaper after the terminal path already freed it) is a no-op.
        Should.NotThrow(() => EgressSubnetAllocator.Release(a));
    }

    [Fact]
    public void Re_acquiring_the_same_run_id_returns_the_same_lease()
    {
        var runId = Guid.NewGuid().ToString("N");
        try
        {
            var first = EgressSubnetAllocator.Acquire(runId);
            var after = EgressSubnetAllocator.ActiveCount;
            var second = EgressSubnetAllocator.Acquire(runId);

            second.Cidr.ShouldBe(first.Cidr, "a setup retry / re-entry for the same run is stable, not a second reservation");
            EgressSubnetAllocator.ActiveCount.ShouldBe(after, "re-acquiring the same run reserves no second /30");
        }
        finally { EgressSubnetAllocator.Release(runId); }
    }
}
