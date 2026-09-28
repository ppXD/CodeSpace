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
        a.TeardownCommands.ShouldContain(c => c.SequenceEqual(new[] { "nft", "delete", "table", "inet", a.Namespace }), "teardown still deletes the inet table a network-off run sealed to its broker before the relay left behind — every reaper knows only the run id");
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
        var rs = ForwardTable(plan.NftRuleset);

        rs.ShouldContain("masquerade", customMessage: "the subnet is NAT'd out (interface-agnostic)");
        rs.ShouldContain("ct state established,related accept", customMessage: "return traffic is allowed");
        rs.ShouldContain("{ 1.1.1.1, 140.82.112.3 }", customMessage: "ONLY the given IPs are in the accept set");
        rs.ShouldContain($"ip saddr {plan.NsSubnetCidr} ip daddr {{ 1.1.1.1, 140.82.112.3 }} accept");
        rs.ShouldContain($"ip saddr {plan.NsSubnetCidr} drop", customMessage: "the default-drop is SCOPED to the netns subnet — never the host's own forwarding");
        rs.ShouldContain("udp dport 53 accept", customMessage: "DNS is permitted so the agent can resolve");
    }

    [Fact]
    public void The_ruleset_guards_the_host_veth_both_ways_in_the_same_transaction()
    {
        // Pinned whole and written out, because every line is load-bearing and a membership assertion is satisfied by
        // the wrong chain: the forward table, then the guard in the same transaction; inet (the veth's IPv6 link-local
        // must meet the same drop); replaced, not appended to (a table of this name an earlier teardown left behind would
        // keep its drop ahead of these); INPUT from the veth admits only the rest of the flows the namespace opened, and
        // the DNS the forward filter admits, from the run's /30; OUTPUT to the veth admits only those flows' replies and
        // errors, frag-needed among them, and rejects the rest. Each accept is bound to its direction: a flow the worker
        // opened to the peer at the run's .2 before the /30 shadowed it is ESTABLISHED wherever conntrack already ran,
        // and neither its packets nor a forged answer to them may cross.
        var plan = FilteredEgressPlan.Build("run-9aa7d001", new[] { "1.1.1.1" }, Subnet);

        plan.NftRuleset.ShouldBe(
            $"table ip {plan.Namespace} {{\n" +
            "  chain postrouting {\n" +
            "    type nat hook postrouting priority 100;\n" +
            $"    ip saddr {Subnet.Cidr} masquerade\n" +
            "  }\n" +
            "  chain forward {\n" +
            "    type filter hook forward priority 0;\n" +
            "    ct state established,related accept\n" +
            $"    ip saddr {Subnet.Cidr} udp dport 53 accept\n" +
            $"    ip saddr {Subnet.Cidr} tcp dport 53 accept\n" +
            $"    ip saddr {Subnet.Cidr} ip daddr {{ 1.1.1.1 }} accept\n" +
            $"    ip saddr {Subnet.Cidr} drop\n" +
            "  }\n" +
            "}\n" +
            $"table inet {plan.Namespace} {{}}\n" +
            $"delete table inet {plan.Namespace}\n" +
            $"table inet {plan.Namespace} {{\n" +
            "  chain input {\n" +
            "    type filter hook input priority 0;\n" +
            $"    iifname \"{plan.VethHost}\" ct direction original ct state established,related accept\n" +
            $"    iifname \"{plan.VethHost}\" ip saddr {Subnet.Cidr} udp dport 53 accept\n" +
            $"    iifname \"{plan.VethHost}\" ip saddr {Subnet.Cidr} tcp dport 53 accept\n" +
            $"    iifname \"{plan.VethHost}\" drop\n" +
            "  }\n" +
            "  chain output {\n" +
            "    type filter hook output priority 0;\n" +
            $"    oifname \"{plan.VethHost}\" ct direction reply ct state established,related accept\n" +
            $"    oifname \"{plan.VethHost}\" reject\n" +
            "  }\n" +
            "}\n");
        plan.VethHost.ShouldBe("csh-run9aa7d", "the guard is keyed on the run's own host veth, never on a subnet another run may share");
    }

    [Fact]
    public void With_no_allowed_ips_there_is_no_accept_set_only_dns_then_drop()
    {
        // A degenerate allowlist (no IPs) still produces a valid ruleset: DNS + a scoped drop, no daddr-accept rule.
        var rs = ForwardTable(FilteredEgressPlan.Build("run-dddd4444", Array.Empty<string>(), Subnet).NftRuleset);

        rs.ShouldNotContain("ip daddr {", customMessage: "no allowed IPs → no daddr accept rule");
        rs.ShouldContain($"    ip saddr {Subnet.Cidr} drop\n", customMessage: "the scoped default-drop stands with no allow set too: the executor passes an empty list when every allowlisted host failed to resolve, which must mean no egress, never full egress");
    }

    /// <summary>The forward table alone — what precedes the guard's inet table — so an assertion about the forward filter cannot be met by a line of the guard.</summary>
    private static string ForwardTable(string ruleset) => ruleset.Split("table inet ")[0];

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

    [Fact]
    public void The_route_check_asks_the_kernel_how_the_host_reaches_the_namespace_end()
    {
        FilteredEgressPlan.Build("run-ffff6666", new[] { "1.1.1.1" }, Subnet).RouteCheckArgv.ShouldBe(new[] { "ip", "route", "get", "10.5.7.18", "from", "10.5.7.17" }, "to the namespace's end, from the gateway — an allowlist run has no one port its replies come from; as text, which every iproute2 prints");
    }

    [Theory]
    [InlineData(0, "10.5.7.18 from 10.5.7.17 dev csh-runffff6 uid 0 \n    cache \n", null)]                                                            // through the run's own veth
    [InlineData(0, "10.5.7.18 from 10.5.7.17 via 192.168.1.1 dev eth0 uid 0 \n    cache \n", "through something other than the run's own veth csh-runffff6")]
    [InlineData(0, "local 10.5.7.18 from 10.5.7.17 dev lo table local src 10.5.7.18 uid 0 \n    cache <local> \n", "through something other than the run's own veth")]   // a host address shadows the namespace's end
    [InlineData(2, "RTNETLINK answers: Invalid argument\n", "exit 2: RTNETLINK answers: Invalid argument — this host cannot route replies back to 10.5.7.16/30")]   // a blackhole a policy rule consults before main
    [InlineData(2, "RTNETLINK answers: No route to host", "exit 2: RTNETLINK answers: No route to host")]                                                             // unreachable
    [InlineData(0, "", "through something other than the run's own veth")]                                                                                          // no answer at all is no proof
    [InlineData(0, "10.5.7.18 from 10.5.7.17 uid 0", "through something other than the run's own veth")]                                                           // no device named
    [InlineData(0, "10.5.7.18 from 10.5.7.17 dev", "through something other than the run's own veth")]
    [InlineData(0, "10.5.7.18 dev csh-runffff6 \n10.5.7.18 dev eth0", "through something other than the run's own veth")]                                          // two answers are not one route
    [InlineData(0, """[{"dst":"10.5.7.18","from":"10.5.7.17","dev":"csh-runffff6","uid":0,"flags":[],"cache":[]}]""", "through something other than the run's own veth")]   // JSON is not what the argv asks for
    public void Only_a_route_through_the_run_s_own_veth_passes_the_check(int exit, string output, string? failure)
    {
        // The /30 was chosen from the routes the host lists, but a policy rule consults its tables in rule order, not
        // by prefix length: a blackhole 10.0.0.0/8 in a table checked before main discards the replies to a
        // cleanly set-up namespace. Only the kernel's own lookup sees that, so anything but a route through the run's
        // own veth fails the setup instead of admitting a run that can never be answered.
        var plan = FilteredEgressPlan.Build("run-ffff6666", new[] { "1.1.1.1" }, Subnet);

        var reason = plan.RouteCheckFailure(exit, output);

        if (failure is null) reason.ShouldBeNull();
        else reason.ShouldNotBeNull().ShouldContain(failure);
    }
}
