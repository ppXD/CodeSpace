using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the PURE egress-netns command plan (B3.2) — the exact ip/nft/sysctl sequence that builds a deny-by-default
/// filtered network namespace. Unit-testable without root (the privileged execution + real enforcement is the CI
/// E2E). The load-bearing structure: the allow set carries ONLY the given IPs, the forward filter is scoped to the
/// netns subnet (never the host's own forwarding) and ENDS in a drop, and teardown removes the netns + the table.
/// </summary>
[Trait("Category", "Unit")]
public class FilteredEgressPlanTests
{
    // A fixed reserved /30 — the collision-FREE allocation is EgressSubnetAllocator's job (pinned separately); these
    // tests pin the plan SHAPE given a lease.
    private static readonly EgressSubnetAllocator.Lease Subnet = new() { Cidr = "10.5.7.16/30", HostIp = "10.5.7.17", NsIp = "10.5.7.18" };

    [Fact]
    public void Names_are_run_unique_and_consistent_across_the_plan()
    {
        var a = FilteredEgressPlan.Build("run-aaaa1111", new[] { "1.1.1.1" }, Subnet);
        var b = FilteredEgressPlan.Build("run-bbbb2222", new[] { "1.1.1.1" }, Subnet);

        a.Namespace.ShouldNotBe(b.Namespace, "distinct runs get distinct netns names — no collision on the shared kernel");
        a.ExecPrefix.ShouldBe(new[] { "ip", "netns", "exec", a.Namespace }, "the command runs inside this run's netns");
        a.TeardownCommands.ShouldContain(c => c.SequenceEqual(new[] { "ip", "netns", "del", a.Namespace }), "teardown deletes the netns");
        a.TeardownCommands.ShouldContain(c => c.SequenceEqual(new[] { "nft", "delete", "table", "ip", a.Namespace }), "teardown deletes the nft table");
        a.TeardownCommands.ShouldContain(c => c.SequenceEqual(new[] { "nft", "delete", "table", "inet", a.Namespace }), "teardown deletes a sealed run's inet table too — every reaper knows only the run id");
    }

    [Fact]
    public void A_sealed_plan_has_no_route_no_forwarding_and_no_nat()
    {
        var plan = FilteredEgressPlan.BuildSealed("run-5ea1ed01", 43121, Subnet);

        plan.SetupCommands.ShouldNotContain(c => c.Contains("route"), "no default route: a packet to anywhere but the /30 fails with ENETUNREACH at once");
        plan.SetupCommands.ShouldNotContain(c => c[0] == "sysctl", "nothing is forwarded, so the host's forwarding switch is left alone");
        plan.SetupCommands.ShouldContain(c => c.SequenceEqual(new[] { "ip", "netns", "exec", plan.Namespace, "ip", "addr", "add", $"{Subnet.NsIp}/30", "dev", plan.VethNs }), "the namespace still holds its /30, so the gateway is on-link");
        plan.HostIp.ShouldBe(Subnet.HostIp, "the gateway the child reaches its broker at");
        plan.ExecPrefix.ShouldBe(new[] { "ip", "netns", "exec", plan.Namespace });
        plan.TeardownCommands.Select(c => string.Join(' ', c)).ShouldBe(FilteredEgressPlan.TeardownCommandsFor("run-5ea1ed01").Select(c => string.Join(' ', c)), "a sealed namespace is torn down by the same run-id-only commands every reaper already runs");
    }

    [Fact]
    public void A_sealed_ruleset_admits_only_the_broker_port_on_the_gateway()
    {
        var plan = FilteredEgressPlan.BuildSealed("run-5ea1ed02", 43121, Subnet);
        var rs = plan.NftRuleset;

        rs.ShouldStartWith($"table inet {plan.Namespace} {{", customMessage: "inet, not ip: the veth's IPv6 link-local address must be covered by the same drop");
        rs.ShouldContain("type filter hook input priority 0;", customMessage: "the input hook is where the worker's own listeners are reached from — the allowlist plan has no such filter");
        rs.ShouldContain($"iifname \"{plan.VethHost}\" ip daddr {Subnet.HostIp} tcp dport 43121 accept", customMessage: "the broker's one port on the gateway is the only destination");
        rs.ShouldContain($"iifname \"{plan.VethHost}\" drop", customMessage: "everything else from this run's veth is dropped");
        rs.ShouldNotContain("dport 53", customMessage: "no DNS — a resolver is a tunnel");
        rs.ShouldNotContain("masquerade", customMessage: "no NAT — nothing leaves the host");
        rs.ShouldNotContain("saddr", customMessage: "keyed on the veth, never on a subnet a degraded allocator might hand another run too");
    }

    [Fact]
    public void The_plan_uses_the_reserved_subnet_lease()
    {
        var plan = FilteredEgressPlan.Build("run-ffff6666", new[] { "1.1.1.1" }, Subnet);

        plan.NsSubnetCidr.ShouldBe(Subnet.Cidr, "the plan pins the caller-reserved /30, not a self-derived one");
        plan.HostIp.ShouldBe(Subnet.HostIp);
        plan.SetupCommands.ShouldContain(c => c.SequenceEqual(new[] { "ip", "netns", "exec", plan.Namespace, "ip", "addr", "add", $"{Subnet.NsIp}/30", "dev", plan.VethNs }), "the netns side gets the lease's ns address");
    }

    [Fact]
    public void Teardown_commands_are_reconstructable_from_the_run_id_alone()
    {
        // The reaper / crash-resume contract: teardown needs NO setup-time subnet — every name is runId-derived.
        var fromPlan = FilteredEgressPlan.Build("run-7777", new[] { "1.1.1.1" }, Subnet).TeardownCommands;
        var fromRunId = FilteredEgressPlan.TeardownCommandsFor("run-7777");

        fromRunId.Select(c => string.Join(' ', c)).ShouldBe(fromPlan.Select(c => string.Join(' ', c)),
            "TeardownCommandsFor(runId) reproduces the plan's teardown with no subnet/state");
    }

    [Fact]
    public void The_nft_ruleset_allows_only_the_given_ips_then_drops_the_subnet()
    {
        var plan = FilteredEgressPlan.Build("run-cccc3333", new[] { "1.1.1.1", "140.82.112.3" }, Subnet);
        var rs = plan.NftRuleset;

        rs.ShouldContain("masquerade", customMessage: "the subnet is NAT'd out (interface-agnostic)");
        rs.ShouldContain("ct state established,related accept", customMessage: "return traffic is allowed");
        rs.ShouldContain("{ 1.1.1.1, 140.82.112.3 }", customMessage: "ONLY the given IPs are in the accept set");
        rs.ShouldContain($"ip saddr {plan.NsSubnetCidr} ip daddr {{ 1.1.1.1, 140.82.112.3 }} accept");
        rs.ShouldContain($"ip saddr {plan.NsSubnetCidr} drop", customMessage: "the default-drop is SCOPED to the netns subnet — never the host's own forwarding");
        rs.ShouldContain("udp dport 53 accept", customMessage: "DNS is permitted so the agent can resolve");
    }

    [Fact]
    public void With_no_allowed_ips_there_is_no_accept_set_only_dns_then_drop()
    {
        // A degenerate allowlist (no IPs) still produces a valid ruleset: DNS + a scoped drop, no daddr-accept rule.
        var rs = FilteredEgressPlan.Build("run-dddd4444", Array.Empty<string>(), Subnet).NftRuleset;

        rs.ShouldNotContain("ip daddr {", customMessage: "no allowed IPs → no daddr accept rule");
        rs.ShouldContain("drop");
    }

    [Fact]
    public void Setup_creates_the_netns_and_veth_and_default_route()
    {
        var plan = FilteredEgressPlan.Build("run-eeee5555", new[] { "1.1.1.1" }, Subnet);

        plan.SetupCommands[0].ShouldBe(new[] { "ip", "netns", "add", plan.Namespace }, "the netns is created first");
        plan.SetupCommands.ShouldContain(c => c.Count >= 2 && c[0] == "ip" && c[1] == "link" && c.Contains("veth"), "a veth pair is created");
        plan.SetupCommands.ShouldContain(c => c.SequenceEqual(new[] { "ip", "netns", "exec", plan.Namespace, "ip", "route", "add", "default", "via", plan.HostIp }), "the netns default route points at the host veth end");
        plan.SetupCommands.ShouldContain(c => c.SequenceEqual(new[] { "sysctl", "-w", "net.ipv4.ip_forward=1" }), "forwarding is enabled so the host NATs the netns out");
        plan.NftApplyArgv.ShouldBe(new[] { "nft", "-f", "-" }, "the ruleset is applied on stdin");
    }
}
