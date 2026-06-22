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
    [Fact]
    public void Names_are_run_unique_and_consistent_across_the_plan()
    {
        var a = FilteredEgressPlan.Build("run-aaaa1111", new[] { "1.1.1.1" });
        var b = FilteredEgressPlan.Build("run-bbbb2222", new[] { "1.1.1.1" });

        a.Namespace.ShouldNotBe(b.Namespace, "distinct runs get distinct netns names — no collision on the shared kernel");
        a.NsSubnetCidr.ShouldNotBe(b.NsSubnetCidr, "and distinct subnets");
        a.ExecPrefix.ShouldBe(new[] { "ip", "netns", "exec", a.Namespace }, "the command runs inside this run's netns");
        a.TeardownCommands.ShouldContain(c => c.SequenceEqual(new[] { "ip", "netns", "del", a.Namespace }), "teardown deletes the netns");
        a.TeardownCommands.ShouldContain(c => c.SequenceEqual(new[] { "nft", "delete", "table", "ip", a.Namespace }), "teardown deletes the nft table");
    }

    [Fact]
    public void The_nft_ruleset_allows_only_the_given_ips_then_drops_the_subnet()
    {
        var plan = FilteredEgressPlan.Build("run-cccc3333", new[] { "1.1.1.1", "140.82.112.3" });
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
        var rs = FilteredEgressPlan.Build("run-dddd4444", Array.Empty<string>()).NftRuleset;

        rs.ShouldNotContain("ip daddr {", customMessage: "no allowed IPs → no daddr accept rule");
        rs.ShouldContain("drop");
    }

    [Fact]
    public void Setup_creates_the_netns_and_veth_and_default_route()
    {
        var plan = FilteredEgressPlan.Build("run-eeee5555", new[] { "1.1.1.1" });

        plan.SetupCommands[0].ShouldBe(new[] { "ip", "netns", "add", plan.Namespace }, "the netns is created first");
        plan.SetupCommands.ShouldContain(c => c.Count >= 2 && c[0] == "ip" && c[1] == "link" && c.Contains("veth"), "a veth pair is created");
        plan.SetupCommands.ShouldContain(c => c.SequenceEqual(new[] { "ip", "netns", "exec", plan.Namespace, "ip", "route", "add", "default", "via", plan.HostIp }), "the netns default route points at the host veth end");
        plan.SetupCommands.ShouldContain(c => c.SequenceEqual(new[] { "sysctl", "-w", "net.ipv4.ip_forward=1" }), "forwarding is enabled so the host NATs the netns out");
        plan.NftApplyArgv.ShouldBe(new[] { "nft", "-f", "-" }, "the ruleset is applied on stdin");
    }
}
