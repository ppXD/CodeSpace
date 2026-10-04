using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using Shouldly;
using Xunit.Abstractions;

namespace CodeSpace.SandboxTests;

/// <summary>
/// 🟢 Sandbox isolation E2E (high fidelity, Rule 12): the REAL deny-by-default egress allowlist (B3.2) against a live
/// kernel — sets up a filtered network namespace whose only egress is the allowlisted IP, runs a real <c>curl</c>
/// inside it, and proves an ALLOWED host is reachable while a NON-allowed host is dropped. Needs ip + nft +
/// CAP_NET_ADMIN, so it runs for real ONLY in the privileged sandbox-isolation CI job (which installs iproute2 +
/// nftables); elsewhere (no nft / not privileged) <see cref="FilteredEgressNetns.IsSupported"/> is false and it
/// degrade-skips. Uses raw IPs (no DNS) so the assertion is purely the egress filter, not name resolution.
///
/// <para>Class-level <c>[Trait("Category", "Sandbox")]</c> — runs in the same privileged gate as the bwrap
/// confinement tests. The teardown is the executor's own best-effort netns/table cleanup (no leak between runs).</para>
///
/// <para>The two arms about the host's own routing (a /30 still held by a namespace that outlived its worker, a policy
/// rule that discards the run's replies) print <see cref="RanMarker"/>, which the lane requires, and so do the arms
/// about the guard on the run's host veth: the namespace cannot reach the worker itself (at its gateway, its own
/// address or the veth's IPv6 link-local) while DNS on the worker and the allowlist still answer; a peer the worker
/// reaches at an address the run's /30 shadows is refused and the sandbox receives nothing, a flow the worker opened to
/// that peer before the run included; an upload across a narrower uplink still completes; and a guard an earlier round
/// left behind is replaced, not added to. So do the one about forwarding a root worker may not turn on, the durable
/// teardown by name, after which the kernel lists nothing of the run (no namespace, no forward table, no guard), and the
/// four about DNS: port 53 is open only at the resolver the run's resolv.conf names; the namespace the production setup
/// builds reads the worker's own resolv.conf, whose resolvers alone its tables admit; and a resolver address the
/// worker's own NAT rewrites — DNATed before the forward table, REDIRECTed to the worker before the guard — still
/// answers the run.</para>
///
/// <para>An arm that needs a resolver of its own names it in a resolv.conf staged at <c>/etc/netns/&lt;ns&gt;/</c>,
/// which <c>ip netns exec</c> binds over the namespace's <c>/etc/resolv.conf</c>, and builds the rules from that same
/// file (<c>NamespaceResolvConf</c>): the worker's own is the job's, and is not the test's to rewrite while sibling
/// classes resolve through it.</para>
/// </summary>
[Trait("Category", "Sandbox")]
public sealed class FilteredEgressNetnsE2ETests(ITestOutputHelper output)
{
    /// <summary>Printed by the arms that must not pass by returning early; the sandbox lane requires one per arm.</summary>
    public const string RanMarker = "[filtered-egress-e2e] ran";

    // Cloudflare 1.1.1.1 + Google 8.8.8.8 both serve HTTPS on the open internet — so if the filter did NOT enforce,
    // BOTH would be reachable. The allowlist permits ONLY 1.1.1.1, so 8.8.8.8 being unreachable proves the drop.
    private const string Allowed = "1.1.1.1";
    private const string Denied = "8.8.8.8";

    [Fact]
    public async Task An_allowed_host_is_reachable_and_a_non_allowed_host_is_dropped()
    {
        if (!FilteredEgressNetns.IsSupported) return;   // no ip/nft (macOS dev / non-privileged) → the privileged CI job is authoritative

        var reachAllowed = await CurlInFilteredNetnsAsync(allow: Allowed, target: Allowed);
        reachAllowed.SetupOk.ShouldBeTrue($"the filtered netns must set up cleanly; setup error: {reachAllowed.SetupError}");
        reachAllowed.ExitCode.ShouldBe(0, $"the ALLOWED host {Allowed} must be reachable through the egress allowlist. Output: {reachAllowed.Output}");

        var reachDenied = await CurlInFilteredNetnsAsync(allow: Allowed, target: Denied);
        reachDenied.SetupOk.ShouldBeTrue($"the filtered netns must set up cleanly; setup error: {reachDenied.SetupError}");
        reachDenied.ExitCode.ShouldNotBe(0, $"a NON-allowed host ({Denied}) must be DROPPED — the deny-by-default egress filter is the whole point. If this reaches it, the filter is not enforcing. Output: {reachDenied.Output}");
    }

    [Fact]
    public async Task The_durable_setup_teardown_split_enforces_the_filter_and_teardown_is_reconstructable_from_runId()
    {
        // The contract the DURABLE launch (B3.2b) relies on: SetupAsync builds the netns + returns the ExecPrefix the
        // detached process runs behind, and TeardownAsync — reconstructed PURELY from the runId — tears it down at reap
        // (possibly on a different worker after a crash). Proves the split is usable + leak-free, not just RunAsync.
        if (!FilteredEgressNetns.IsSupported) return;

        var runId = Guid.NewGuid().ToString("N");
        var names = NamesOf(runId);
        var setup = await FilteredEgressNetns.SetupAsync(runId, new[] { Allowed }, timeoutSeconds: 20, CancellationToken.None);

        try
        {
            setup.SetupOk.ShouldBeTrue($"setup must succeed; error: {setup.SetupError}");
            setup.ExecPrefix.ShouldNotBeEmpty("the ExecPrefix is what the durable launch prepends to run its process inside the netns");

            // Run curl INSIDE the netns via the ExecPrefix — exactly how the durable launch will prefix its command chain.
            (await RunViaPrefixAsync(setup.ExecPrefix, Allowed)).ShouldBe(0, "the ALLOWED host is reachable through the set-up netns");
            (await RunViaPrefixAsync(setup.ExecPrefix, Denied)).ShouldNotBe(0, "the DENIED host is dropped — SetupAsync's netns enforces the filter");
            (await RunHostExitAsync(["nft", "list", "table", "inet", names.Namespace])).ShouldBe(0, "control: the guard on the run's veth is there before its run ends");
        }
        finally
        {
            await FilteredEgressNetns.TeardownAsync(runId, CancellationToken.None);   // reconstructed from runId alone — the reap/crash-resume contract
        }

        // The kernel's own listing is the evidence the guard went, not the re-setup below: the plan's ruleset replaces a
        // guard table of the same name, so a re-setup succeeds over one a teardown left behind.
        (await RunHostExitAsync(["nft", "list", "table", "inet", names.Namespace])).ShouldNotBe(0, $"the guard on the run's veth must go with its run, or every allowlist run leaks an nft table on the worker — check `nft list tables | grep {names.Namespace}`");
        (await RunHostExitAsync(["nft", "list", "table", "ip", names.Namespace])).ShouldNotBe(0, "and so must its forward table");
        (await RunHostExitAsync(["ip", "netns", "pids", names.Namespace])).ShouldNotBe(0, $"and its namespace — check `ip netns list | grep {names.Namespace}`");

        output.WriteLine($"{RanMarker} teardown-by-name table={names.Namespace}");

        // Teardown actually freed the runId-derived names: a second SetupAsync with the SAME runId succeeds (it would
        // collide on the still-present ns/table otherwise). This is the leak-free guarantee.
        var resetup = await FilteredEgressNetns.SetupAsync(runId, new[] { Allowed }, timeoutSeconds: 20, CancellationToken.None);
        resetup.SetupOk.ShouldBeTrue("teardown freed the runId-derived ns/table so a re-setup succeeds — proving teardown cleaned up, no leak");
        await FilteredEgressNetns.TeardownAsync(runId, CancellationToken.None);
    }

    [Fact]
    public async Task A_30_still_held_by_a_run_that_outlived_its_worker_is_not_handed_to_the_next_run()
    {
        if (!BuildsNamespaces()) return;

        // A run survives its worker by design, and so do its namespace and veth; the reservation lock does not. A fresh
        // worker that trusted the lock alone would hand the survivor's /30 to its next allowlist launch, and the kernel
        // would split the two runs' replies between two veths. Releasing the survivor's reservation without tearing its
        // namespace down is exactly what the restart leaves behind.
        var survivor = Guid.NewGuid().ToString("N");
        var next = Guid.NewGuid().ToString("N");
        var first = await FilteredEgressNetns.SetupAsync(survivor, new[] { Allowed }, timeoutSeconds: 20, CancellationToken.None);

        try
        {
            first.SetupOk.ShouldBeTrue($"the survivor's allowlist namespace must set up on this host: {first.SetupError}");
            EgressSubnetAllocator.Host.Release(survivor);

            var second = await FilteredEgressNetns.SetupAsync(next, new[] { Allowed }, timeoutSeconds: 20, CancellationToken.None);

            try
            {
                second.SetupOk.ShouldBeTrue($"the next run's allowlist namespace must set up: {second.SetupError}");

                var survivorGateway = await GatewayOfAsync(survivor);
                var nextGateway = await GatewayOfAsync(next);

                nextGateway.ShouldNotBe(survivorGateway, "the survivor's /30 is still on its veth; handing it out again routes one run's replies into the other's namespace");

                output.WriteLine($"{RanMarker} restart-reissue survivor={survivorGateway} next={nextGateway}");
            }
            finally { await FilteredEgressNetns.TeardownAsync(next, CancellationToken.None); }
        }
        finally { await FilteredEgressNetns.TeardownAsync(survivor, CancellationToken.None); }
    }

    [Fact]
    public async Task A_host_whose_policy_rule_discards_the_run_s_replies_fails_the_setup_and_leaks_nothing()
    {
        if (!BuildsNamespaces()) return;

        // The allocator skips what the host's route listing covers, but a null route in a table that a policy rule
        // consults before main wins by rule ORDER, not prefix length: every step of the setup succeeds, and then every
        // reply to the namespace is discarded. The /30 here is from TEST-NET-1 (RFC 5737), which the allocator never
        // hands out, and the rule covers only that /30, so it cannot reach another run on this host.
        var third = RandomNumberGenerator.GetInt32(0, 64) * 4;
        var lease = new EgressSubnetAllocator.Lease { Cidr = $"192.0.2.{third}/30", HostIp = $"192.0.2.{third + 1}", NsIp = $"192.0.2.{third + 2}" };
        var table = RandomNumberGenerator.GetInt32(10_000, 1_000_000).ToString(CultureInfo.InvariantCulture);
        string[] rule = ["pref", "100", "to", lease.Cidr, "lookup", table];
        var runId = Guid.NewGuid().ToString("N");
        var plan = FilteredEgressPlan.Build(runId, new[] { Allowed }, lease, []);

        // A run of this test killed between its rule add and its cleanup leaves a rule for its /30 in a table this run
        // cannot name; left there, it would fail this run's control and blame the check.
        for (var stale = 0; stale < 8 && await RunHostExitAsync(["ip", "rule", "del", "pref", "100", "to", lease.Cidr]) == 0; stale++) { }

        try
        {
            var control = await FilteredEgressNetns.ApplyAsync(runId, plan, timeoutSeconds: 20, CancellationToken.None);
            control.SetupOk.ShouldBeTrue($"control: the same plan must set up on a host with no such rule, or the check refuses what it should admit: {control.SetupError}");
            await FilteredEgressNetns.TeardownAsync(runId, CancellationToken.None);

            (await RunHostExitAsync(["ip", "route", "add", "unreachable", "192.0.2.0/24", "table", table])).ShouldBe(0, "setup: the null route — broader than a /30, which the route listing ignores — must be installable in its own table");
            (await RunHostExitAsync(["ip", "rule", "add", .. rule])).ShouldBe(0, "setup: the rule that consults it before main must be installable");

            var refused = await FilteredEgressNetns.ApplyAsync(runId, plan, timeoutSeconds: 20, CancellationToken.None);

            refused.SetupOk.ShouldBeFalse("a namespace the host can never answer must fail its setup, not admit a run that spends its timeout unanswered");
            refused.SetupError.ShouldNotBeNull().ShouldContain(string.Join(' ', plan.RouteCheckArgv), customMessage: "the refusal names the lookup that found it, so an operator can rerun it");
            (await RunHostAsync(["ip", "netns", "list"])).Split('\n').ShouldNotContain(line => line.Trim().Split(' ')[0] == plan.Namespace, "a setup that failed its route check tears its namespace down");
            (await RunHostExitAsync(["ip", "link", "show", plan.VethHost])).ShouldNotBe(0, "and the host end of its veth, with the address on it");

            output.WriteLine($"{RanMarker} policy-route-discard-dst {refused.SetupError}");
        }
        finally
        {
            await RunHostExitAsync(["ip", "rule", "del", .. rule]);
            await RunHostExitAsync(["ip", "route", "flush", "table", table]);
            await FilteredEgressNetns.TeardownAsync(runId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task An_allowlist_run_reaches_neither_the_worker_s_gateway_nor_its_address_while_dns_on_the_worker_and_the_allowlist_still_answer()
    {
        if (!BuildsNamespaces()) return;

        // Before the relay, an allowlist run reached its broker at its namespace's gateway, so that door stood open onto
        // every listener the worker has — its API, every other run's lease — at the gateway and at the worker's own
        // address. The relay carries the broker over a socket now, and the guard on the veth drops what the namespace
        // sends the worker itself, save DNS to a resolver the worker serves on its own address and the run's resolv.conf
        // names. Port 53 at any other address of the worker — here a listener at the gateway — stays shut. The control
        // deletes the guard and asks again: both listeners must answer then, or their silence proved nothing about it.
        using var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        var workerIp = SealedEgressE2ETests.WorkerIpv4();
        using var resolver = new WorkerResolver(workerIp);
        var runId = Guid.NewGuid().ToString("N");
        using var view = new NamespaceResolvConf(runId, $"nameserver {workerIp}\n");
        var setup = await FilteredEgressNetns.SetupAsync(runId, new[] { Allowed }, view.Path, timeoutSeconds: 20, CancellationToken.None);

        try
        {
            setup.SetupOk.ShouldBeTrue($"the allowlist namespace must set up on this host: {setup.SetupError}");

            var gateway = await GatewayOfAsync(runId);
            using var unlisted = new WorkerResolver(gateway);
            var guarded = await ProbeTheWorkerAsync(setup, gateway, workerIp, listener);

            guarded["gateway"].ShouldNotBe("open", $"a listener on the worker must not be reachable at the run's gateway {gateway} (its API, every other run's lease); probe: {Describe(guarded)}");
            guarded["worker"].ShouldNotBe("open", $"nor at the worker's own address {workerIp}; probe: {Describe(guarded)}");
            guarded["dns_udp"].ShouldBe("answered", $"the resolver the run's resolv.conf names, which the worker serves on its own address, still answers over UDP; probe: {Describe(guarded)}");
            guarded["dns_tcp"].ShouldBe("answered", $"and over TCP; probe: {Describe(guarded)}");
            guarded["gateway_53"].ShouldNotBe("open", $"but port 53 at the gateway {gateway}, which the resolv.conf does not name, is shut — check `nft list table inet {FilteredEgressPlan.NamespaceFor(runId)}` names {workerIp} on each port-53 accept; probe: {Describe(guarded)}");
            guarded["allowed"].ShouldBe("open", $"the allowlisted {Allowed} is still reachable through the namespace's NAT; probe: {Describe(guarded)}");

            (await RunHostExitAsync(["nft", "delete", "table", "inet", FilteredEgressPlan.NamespaceFor(runId)])).ShouldBe(0, "control setup: the guard must be there to delete");
            var unguarded = await ProbeTheWorkerAsync(setup, gateway, workerIp, listener);

            unguarded["gateway"].ShouldBe("open", $"control: with the guard gone the listener answers at the gateway, or the refusal above proved nothing about the guard; probe: {Describe(unguarded)}");
            unguarded["worker"].ShouldBe("open", $"control: and at the worker's own address; probe: {Describe(unguarded)}");
            unguarded["gateway_53"].ShouldBe("open", $"control: and port 53 at the gateway, or its refusal proved nothing about the pin; probe: {Describe(unguarded)}");

            output.WriteLine($"{RanMarker} worker-shut {Describe(guarded)} control: {Describe(unguarded)}");
        }
        finally { await FilteredEgressNetns.TeardownAsync(runId, CancellationToken.None); }
    }

    [Fact]
    public async Task An_allowlist_run_has_no_path_to_the_worker_over_the_veth_s_ipv6_link_local_either()
    {
        if (!BuildsNamespaces()) return;

        // Only the host knows its veth's link-local address, so this arm drives the namespace directly. A v4-only guard
        // would let this through; the guard is inet. Three things keep the arm honest: it waits out duplicate-address
        // detection (a tentative address refuses everything, guard or not); it pins each end's neighbour entry for the
        // other, since the kernel does not track neighbour discovery and the guard's OUTPUT reject would otherwise stop
        // the host's advertisement first — so the SYN itself must be what the INPUT drop refuses; and it then deletes
        // the guard and connects again — the probe must get through without it, or its refusal proved nothing about it.
        var runId = Guid.NewGuid().ToString("N");
        var names = NamesOf(runId);
        var setup = await FilteredEgressNetns.SetupAsync(runId, new[] { Allowed }, timeoutSeconds: 20, CancellationToken.None);

        try
        {
            setup.SetupOk.ShouldBeTrue($"the allowlist namespace must set up on this host: {setup.SetupError}");

            if (await SettledLinkLocalAsync(["ip", "-6", "-o", "addr", "show", "dev", names.VethHost, "scope", "link"]) is not { } linkLocal)
            {
                output.WriteLine("[filtered-egress-e2e] skipped ipv6-link-local (IPv6 is disabled on this host, so there is no v6 path to close)");
                return;
            }

            var nsLinkLocal = (await SettledLinkLocalAsync(setup.ExecPrefix.Concat(["ip", "-6", "-o", "addr", "show", "dev", names.VethNs, "scope", "link"]).ToList())).ShouldNotBeNull("the namespace side's link-local must settle too, or it cannot send");
            var hostMac = LinkAddress(await RunHostAsync(["ip", "-o", "link", "show", "dev", names.VethHost]));
            var nsMac = LinkAddress(await RunHostAsync(setup.ExecPrefix.Concat(["ip", "-o", "link", "show", "dev", names.VethNs]).ToList()));

            (await RunHostExitAsync(["ip", "-6", "neigh", "replace", nsLinkLocal, "lladdr", nsMac, "dev", names.VethHost, "nud", "permanent"])).ShouldBe(0, "setup: the host's neighbour entry for the namespace end must pin");
            (await RunHostExitAsync(setup.ExecPrefix.Concat(["ip", "-6", "neigh", "replace", linkLocal, "lladdr", hostMac, "dev", names.VethNs, "nud", "permanent"]).ToList())).ShouldBe(0, "setup: the namespace's neighbour entry for the host end must pin");

            using var listener = new TcpListener(IPAddress.IPv6Any, 0);
            listener.Start();

            var connect = setup.ExecPrefix.Concat(["python3", "-c", ConnectScript, $"{linkLocal}%{names.VethNs}", PortOf(listener)]).ToList();
            var guarded = (await RunHostAsync(connect)).Trim();

            (await RunHostExitAsync(["nft", "delete", "table", "inet", names.Namespace])).ShouldBe(0, "control setup: the guard must be there to delete");
            var control = (await RunHostAsync(connect)).Trim();

            control.ShouldBe("open", $"control: with the guard gone the same probe must reach the listener at {linkLocal}, or its refusal proved nothing about the guard (got {control})");
            guarded.ShouldNotBe("open", $"the worker's listener must not be reachable over the host veth's IPv6 link-local address {linkLocal} while the guard stands");

            output.WriteLine($"{RanMarker} ipv6-link-local outcome={guarded} control={control}");
        }
        finally { await FilteredEgressNetns.TeardownAsync(runId, CancellationToken.None); }
    }

    [Fact]
    public async Task A_peer_the_worker_reaches_at_the_run_s_address_is_refused_and_the_sandbox_receives_nothing()
    {
        if (!BuildsNamespaces()) return;

        // A /30 laid over a subnet the worker reaches through its uplink shadows the peer there that holds the run's end:
        // the connected route is the more specific, so the worker's own connection to that peer goes into the run's veth
        // — request bytes and all — to whatever the sandbox listens with. The guard cannot give the worker its peer back,
        // but it makes the collision fail closed: refused at once, and nothing delivered into the sandbox.
        await using var world = await UplinkWorld.StartAsync();

        (await AskAsync(world.ShadowedIp, UplinkWorld.PeerPort, 4)).ShouldBe("PEER got 4 bytes", "control: before the run exists the worker reaches the peer at the run's .2 through its uplink, or there is no collision to guard");

        var runId = Guid.NewGuid().ToString("N");
        var setup = await FilteredEgressNetns.ApplyAsync(runId, FilteredEgressPlan.Build(runId, new[] { world.AllowedIp }, world.RunLease, []), timeoutSeconds: 20, CancellationToken.None);

        try
        {
            setup.SetupOk.ShouldBeTrue($"the run's namespace must set up over the shadowed /30: {setup.SetupError}");

            using var sandbox = await NamespaceProcess.StartAsync(setup.ExecPrefix, SandboxListenerScript, UplinkWorld.PeerPort.ToString(CultureInfo.InvariantCulture));

            var tcp = await AskAsync(world.ShadowedIp, UplinkWorld.PeerPort, 4);
            var udp = await SendDatagramAsync(world.ShadowedIp, UplinkWorld.PeerPort);
            await Task.Delay(300);
            var received = await sandbox.StopAsync();

            received.ShouldBe("SANDBOX accepted=0 tcp=0 udp=0", $"the sandbox must receive nothing the worker sent to the peer at {world.ShadowedIp}, which the run's /30 shadows (the worker's connection: '{tcp}', its datagram's send: {udp}); check `nft list table inet {FilteredEgressPlan.NamespaceFor(runId)}`");
            tcp.ShouldBe(nameof(SocketError.ConnectionRefused), $"and the worker's connection must be refused at once, not left to time out; the sandbox saw: {received}");

            output.WriteLine($"{RanMarker} shadowed-peer-refused tcp={tcp} udp={udp} {received}");
        }
        finally { await FilteredEgressNetns.TeardownAsync(runId, CancellationToken.None); }
    }

    [Fact]
    public async Task A_flow_the_worker_opened_to_the_shadowed_peer_before_the_run_is_neither_handed_to_the_sandbox_nor_answered_from_it()
    {
        if (!BuildsNamespaces()) return;

        // Wherever another allowlist run is up, conntrack already runs in the worker's namespace, so a flow the worker
        // opened to the peer at the run's .2 before the run's /30 shadowed it is ESTABLISHED when its next packet takes
        // the run's veth. Admitted as established in either direction, that packet would reach the sandbox, and the
        // sandbox's datagram from the peer's address and port would reach the worker as the peer's answer. A sibling run
        // keeps conntrack running, and conntrack's own table shows both flows tracked before the run appears.
        await using var world = await UplinkWorld.StartAsync();
        var sibling = Guid.NewGuid().ToString("N");
        var runId = Guid.NewGuid().ToString("N");

        try
        {
            var siblingSetup = await FilteredEgressNetns.SetupAsync(sibling, new[] { Allowed }, timeoutSeconds: 20, CancellationToken.None);
            siblingSetup.SetupOk.ShouldBeTrue($"setup: a sibling allowlist run, which keeps conntrack running in the worker's namespace, must set up: {siblingSetup.SetupError}");

            using var udp = new UdpClient();
            udp.Connect(IPAddress.Parse(world.ShadowedIp), UplinkWorld.PeerPort);
            (await SendOnAsync(udp, "hello")).ShouldBe("sent", "control: the worker's datagram to the peer must leave through the uplink");
            (await ReceiveOnAsync(udp, 5)).ShouldBe("PEER:hello", "control: before the run exists the peer at the run's .2 answers the worker's datagram flow");

            using var tcp = new TcpClient(AddressFamily.InterNetwork);
            await tcp.ConnectAsync(IPAddress.Parse(world.ShadowedIp), UplinkWorld.PeerPort);   // a pooled connection, held open across the run's setup

            var udpLocal = (IPEndPoint)udp.Client.LocalEndPoint!;
            var tcpLocal = (IPEndPoint)tcp.Client.LocalEndPoint!;

            var tracked = await ConntrackEntriesAsync();
            tracked.ShouldNotBeEmpty("fixture: conntrack -L listed nothing, so this arm cannot show the worker's flows were tracked before the run — its precondition");

            tracked.ShouldContain(line => line.Contains(" udp ") && line.Contains($"src={udpLocal.Address} dst={world.ShadowedIp} sport={udpLocal.Port} dport={UplinkWorld.PeerPort} ") && !line.Contains("[UNREPLIED]"), $"fixture: conntrack must track the worker's answered datagram flow before the run exists, or its next packet is NEW and this arm proves nothing; {ConntrackTable}: {string.Join(" | ", tracked)}");
            tracked.ShouldContain(line => line.Contains(" ESTABLISHED ") && line.Contains($"src={tcpLocal.Address} dst={world.ShadowedIp} sport={tcpLocal.Port} dport={UplinkWorld.PeerPort} "), $"fixture: and its pooled connection as ESTABLISHED; {ConntrackTable}: {string.Join(" | ", tracked)}");

            var setup = await FilteredEgressNetns.ApplyAsync(runId, FilteredEgressPlan.Build(runId, new[] { world.AllowedIp }, world.RunLease, []), timeoutSeconds: 20, CancellationToken.None);
            setup.SetupOk.ShouldBeTrue($"the run's namespace must set up over the shadowed /30: {setup.SetupError}");

            using var sandbox = await NamespaceProcess.StartAsync(setup.ExecPrefix, SandboxListenerScript, UplinkWorld.PeerPort.ToString(CultureInfo.InvariantCulture), udpLocal.Address.ToString(), udpLocal.Port.ToString(CultureInfo.InvariantCulture));
            var forged = await ReceiveOnAsync(udp, 2);

            var segmentsBefore = await TcpSegmentsInAsync(setup.ExecPrefix);
            var udpSend = await SendOnAsync(udp, "SECRET-FROM-WORKER");
            await tcp.GetStream().WriteAsync(new byte[100]);
            await Task.Delay(1000);
            var segmentsAfter = await TcpSegmentsInAsync(setup.ExecPrefix);
            var received = await sandbox.StopAsync();

            forged.ShouldBe("timeout", $"the sandbox's datagram from the peer's address and port must not reach the worker's flow as the peer's answer; check `nft list table inet {FilteredEgressPlan.NamespaceFor(runId)}`");
            received.ShouldBe("SANDBOX accepted=0 tcp=0 udp=0", $"the sandbox must receive nothing on the worker's datagram flow to the peer at {world.ShadowedIp} (its send: {udpSend}); check `nft list table inet {FilteredEgressPlan.NamespaceFor(runId)}`");
            segmentsAfter.ShouldBe(segmentsBefore, $"and the namespace's TCP stack none of the worker's pooled connection's segments (its InSegs counter in `ip netns exec {setup.ExecPrefix[^1]} cat /proc/net/snmp`)");

            output.WriteLine($"{RanMarker} established-flow-refused forged={forged} udp={udpSend} in_segs={segmentsBefore}->{segmentsAfter} {received}");
        }
        finally
        {
            await FilteredEgressNetns.TeardownAsync(runId, CancellationToken.None);
            await FilteredEgressNetns.TeardownAsync(sibling, CancellationToken.None);
        }
    }

    [Fact]
    public async Task An_upload_across_a_narrower_uplink_completes_because_the_worker_s_frag_needed_reaches_the_run()
    {
        if (!BuildsNamespaces()) return;

        // The run's veth carries 1500-byte packets and the worker's uplink only 1400, so path-MTU discovery is the only
        // way a large upload gets through: the worker answers each oversized packet with ICMP fragmentation-needed, sent
        // into the run's veth through the guard's OUTPUT chain, which admits it as related to the run's own connection.
        // Without that the upload stalls. The path MTU the run learned is read back, so a path that never needed the
        // ICMP cannot pass for one that did.
        await using var world = await UplinkWorld.StartAsync();
        var runId = Guid.NewGuid().ToString("N");
        var setup = await FilteredEgressNetns.ApplyAsync(runId, FilteredEgressPlan.Build(runId, new[] { world.AllowedIp }, world.RunLease, []), timeoutSeconds: 20, CancellationToken.None);

        try
        {
            setup.SetupOk.ShouldBeTrue($"the run's namespace must set up: {setup.SetupError}");

            var upload = (await RunHostAsync(setup.ExecPrefix.Concat(["python3", "-c", UploadScript, world.AllowedIp, UplinkWorld.PeerPort.ToString(CultureInfo.InvariantCulture), "400000"]).ToList())).Trim();
            var learned = (await RunHostAsync(setup.ExecPrefix.Concat(["ip", "route", "get", world.AllowedIp]).ToList())).Trim();

            upload.ShouldBe("PEER got 400000 bytes", $"a 400 KB upload to the allowlisted {world.AllowedIp} must complete; if it stalls, the guard's OUTPUT chain is dropping the worker's frag-needed ICMP — check `ip netns exec {FilteredEgressPlan.NamespaceFor(runId)} ip route get {world.AllowedIp}` for a learned mtu 1400");
            learned.ShouldContain("mtu 1400", customMessage: $"the run must have learned the uplink's MTU from the worker's frag-needed, or this upload never needed it: {learned}");

            output.WriteLine($"{RanMarker} pmtu-upload {upload} route={learned.Replace('\n', ' ')}");
        }
        finally { await FilteredEgressNetns.TeardownAsync(runId, CancellationToken.None); }
    }

    [Fact]
    public async Task A_guard_an_earlier_teardown_left_behind_is_replaced_not_added_to()
    {
        if (!BuildsNamespaces()) return;

        // Every name is the run key's alone, so a table an earlier teardown failed to delete is there when the same names
        // are next set up — its guard for its own /30, drop and all. Added to rather than replaced, this setup's DNS rule
        // would sit behind that drop.
        var runId = Guid.NewGuid().ToString("N");
        var names = NamesOf(runId);
        using var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        var workerIp = SealedEgressE2ETests.WorkerIpv4();
        using var resolver = new WorkerResolver(workerIp);
        using var view = new NamespaceResolvConf(runId, $"nameserver {workerIp}\n");

        try
        {
            (await RunHostExitAsync(["nft", "-f", "-"], FilteredEgressPlan.BuildVethGuardRuleset(names.Namespace, names.VethHost, "192.0.2.252/30", [workerIp]))).ShouldBe(0, "setup: an earlier round's guard, for a /30 of its own, must load");

            var setup = await FilteredEgressNetns.SetupAsync(runId, new[] { Allowed }, view.Path, timeoutSeconds: 20, CancellationToken.None);
            setup.SetupOk.ShouldBeTrue($"the allowlist namespace must set up over the stale table: {setup.SetupError}");

            var probe = await ProbeTheWorkerAsync(setup, await GatewayOfAsync(runId), workerIp, listener);

            probe["dns_udp"].ShouldBe("answered", $"this setup's DNS rule must not sit behind the earlier guard's drop — check `nft list table inet {names.Namespace}` holds one input chain of four rules; probe: {Describe(probe)}");
            probe["gateway"].ShouldNotBe("open", $"and its guard still stands; probe: {Describe(probe)}");

            output.WriteLine($"{RanMarker} stale-guard-replaced {Describe(probe)}");
        }
        finally { await FilteredEgressNetns.TeardownAsync(runId, CancellationToken.None); }
    }

    [Fact]
    public async Task An_allowlist_run_reaches_port_53_only_at_the_resolver_its_resolv_conf_names()
    {
        if (!BuildsNamespaces()) return;

        // Port 53 open to any address is a tunnel, not DNS: the run opens TCP to any host that listens there and carries
        // whatever bytes it likes. The peer beyond the uplink serves DNS at two addresses; the run's resolv.conf names
        // one. A lookup through that file — the query a tool in the run makes — is answered by it; port 53 at the other
        // is shut over TCP and UDP. The control puts back the any-address accepts the forward table had before the pin:
        // the other address must answer then, or its silence proved nothing about the pin.
        await using var world = await UplinkWorld.StartAsync();
        await world.ServeDnsAsync();

        var runId = Guid.NewGuid().ToString("N");
        using var view = new NamespaceResolvConf(runId, $"nameserver {world.ResolverIp}\n");
        var plan = FilteredEgressPlan.Build(runId, new[] { world.AllowedIp }, world.RunLease, NamespaceResolvers.Read(view.Path));
        var setup = await FilteredEgressNetns.ApplyAsync(runId, plan, timeoutSeconds: 20, CancellationToken.None);

        try
        {
            setup.SetupOk.ShouldBeTrue($"the run's namespace must set up: {setup.SetupError}");

            var pinned = await ProbePort53Async(setup.ExecPrefix, world.ResolverIp, world.UnlistedIp);

            pinned["view"].ShouldBe(File.ReadAllText(view.Path), $"fixture: the namespace must read the resolv.conf the rules were built from, or the lookup below asked some other resolver; probe: {Describe(pinned)}");
            pinned["lookup"].ShouldBe(PinnedAnswer, $"a lookup through the namespace's resolv.conf must be answered by the resolver it names, {world.ResolverIp}; probe: {Describe(pinned)}");
            pinned["resolver_udp"].ShouldBe("answered", $"and DNS to it directly over UDP; probe: {Describe(pinned)}");
            pinned["resolver_tcp"].ShouldBe("answered", $"and over TCP; probe: {Describe(pinned)}");
            pinned["unlisted_tcp"].ShouldNotBe("open", $"a connection to port 53 at {world.UnlistedIp}, which the resolv.conf does not name, is the channel that carries any bytes to any host, and must be shut — check `nft list table ip {plan.Namespace}` names {world.ResolverIp} on each port-53 accept; probe: {Describe(pinned)}");
            pinned["unlisted_udp"].ShouldNotBe("answered", $"and a datagram to it unanswered; probe: {Describe(pinned)}");

            (await RunHostExitAsync(["nft", "insert", "rule", "ip", plan.Namespace, "forward", "ip", "saddr", plan.NsSubnetCidr, "udp", "dport", "53", "accept"])).ShouldBe(0, "control setup: the unpinned UDP accept must load");
            (await RunHostExitAsync(["nft", "insert", "rule", "ip", plan.Namespace, "forward", "ip", "saddr", plan.NsSubnetCidr, "tcp", "dport", "53", "accept"])).ShouldBe(0, "control setup: and the TCP one");
            var unpinned = await ProbePort53Async(setup.ExecPrefix, world.ResolverIp, world.UnlistedIp);

            unpinned["unlisted_tcp"].ShouldBe("open", $"control: with port 53 open to any address the run connects to {world.UnlistedIp}:53, or the refusal above proved nothing about the pin; probe: {Describe(unpinned)}");
            unpinned["unlisted_udp"].ShouldBe("answered", $"control: and is answered there over UDP; probe: {Describe(unpinned)}");

            output.WriteLine($"{RanMarker} dns-pinned {Describe(pinned)} control: {Describe(unpinned)}".Replace('\n', ' '));
        }
        finally { await FilteredEgressNetns.TeardownAsync(runId, CancellationToken.None); }
    }

    [Fact]
    public async Task A_namespace_the_setup_builds_reads_the_worker_s_resolv_conf_and_opens_port_53_to_its_resolvers_alone()
    {
        if (!BuildsNamespaces()) return;

        // The production setup, reading no file but the worker's: the rules are built from /etc/resolv.conf because that
        // is the file the namespace reads, and it is only that file while nothing gives the namespace a /etc of its own
        // (`ip netns exec` binds /etc/netns/<ns>/* over /etc where that exists). The loaded tables open port 53 to exactly
        // that file's reachable resolvers. The file must name one, as a production worker's does: one naming none leaves
        // the tables empty whichever file the setup read, and pins nothing. A container job's names only Docker's embedded
        // 127.0.0.11, the namespace's own loopback, so the sandbox lane appends a resolver to it.
        var runId = Guid.NewGuid().ToString("N");
        var ns = FilteredEgressPlan.NamespaceFor(runId);
        var workerView = await File.ReadAllTextAsync(NamespaceResolvers.ResolvConfPath);
        var resolvers = NamespaceResolvers.Parse(workerView);

        resolvers.ShouldNotBeEmpty($"fixture: {NamespaceResolvers.ResolvConfPath} must name a resolver the namespace can reach (an IPv4 nameserver off loopback), or this arm cannot tell the file the setup read from any other — the sandbox-isolation job appends one after the container's 127.0.0.11; this host's: [{workerView.Replace('\n', '|')}]");

        var setup = await FilteredEgressNetns.SetupAsync(runId, new[] { Allowed }, timeoutSeconds: 20, CancellationToken.None);

        try
        {
            setup.SetupOk.ShouldBeTrue($"the allowlist namespace must set up on this host: {setup.SetupError}");

            Directory.Exists(Path.Combine("/etc/netns", ns)).ShouldBeFalse("nothing may give the plan's namespace a /etc of its own, which its rules would not be built from");
            (await RunHostAsync(setup.ExecPrefix.Concat(["cat", NamespaceResolvers.ResolvConfPath]).ToList())).ShouldBe(workerView, "the namespace reads the worker's resolv.conf, byte for byte");

            foreach (var family in new[] { "ip", "inet" })
            {
                var dns = (await RunHostAsync(["nft", "list", "table", family, ns])).Split('\n').Where(line => line.Contains("dport 53", StringComparison.Ordinal)).Select(line => line.Trim()).ToList();

                dns.Count.ShouldBe(2, $"`nft list table {family} {ns}` must accept DNS over UDP and TCP to the resolvers of {NamespaceResolvers.ResolvConfPath} ({string.Join(", ", resolvers)}); got: {string.Join(" | ", dns)}");
                dns.ShouldAllBe(line => line.Contains("ct original ip daddr", StringComparison.Ordinal) && resolvers.All(resolver => line.Contains(resolver, StringComparison.Ordinal)), $"and each accept names every one of them, as the address the run sent to; got: {string.Join(" | ", dns)}");
            }

            output.WriteLine($"{RanMarker} resolv-conf-view resolvers=[{string.Join(",", resolvers)}]");
        }
        finally { await FilteredEgressNetns.TeardownAsync(runId, CancellationToken.None); }
    }

    [Fact]
    public async Task A_resolver_address_the_worker_dnats_before_its_forward_hook_still_answers_the_run()
    {
        if (!BuildsNamespaces()) return;

        // A resolv.conf may name an address no resolver holds, which the worker's own NAT turns into one before the run's
        // forward table sees the query: kube-proxy DNATs a cluster DNS service address on PREROUTING wherever the worker
        // shares a node's network (hostNetwork, ClusterFirstWithHostNet). By FORWARD the query carries the resolver's own
        // address, which the file does not name, so the pin must admit it by the address the run sent it to. The DNAT
        // here keys on the run's veth alone. Port 53 at the resolver's own address stays shut, and the control deletes the
        // DNAT: the resolver's answer must not come back then, or it came some other way than through the worker's rewrite.
        await using var world = await UplinkWorld.StartAsync();
        await world.ServeDnsAsync();

        var runId = Guid.NewGuid().ToString("N");
        var service = ServiceAddress();
        using var view = new NamespaceResolvConf(runId, $"nameserver {service}\noptions timeout:1 attempts:1\n");
        await using var dnat = await WorkerNat.StageAsync(NamesOf(runId).VethHost, service, $"dnat to {world.ResolverIp}");
        var setup = await FilteredEgressNetns.SetupAsync(runId, new[] { world.AllowedIp }, view.Path, timeoutSeconds: 20, CancellationToken.None);

        try
        {
            setup.SetupOk.ShouldBeTrue($"the allowlist namespace must set up on this host: {setup.SetupError}");

            var rewritten = await ProbePort53Async(setup.ExecPrefix, service, world.ResolverIp);

            rewritten["view"].ShouldBe(File.ReadAllText(view.Path), $"fixture: the namespace must read the resolv.conf the rules were built from; probe: {Describe(rewritten)}");
            rewritten["lookup"].ShouldBe(PinnedAnswer, $"a lookup through the resolv.conf, which names {service}, must be answered by the resolver the worker DNATs it to, {world.ResolverIp} — check each port-53 accept in `nft list table ip {FilteredEgressPlan.NamespaceFor(runId)}` matches `ct original ip daddr`; probe: {Describe(rewritten)}");
            rewritten["resolver_udp"].ShouldBe("answered", $"and DNS to {service} directly over UDP; probe: {Describe(rewritten)}");
            rewritten["resolver_tcp"].ShouldBe("answered", $"and over TCP; probe: {Describe(rewritten)}");
            rewritten["unlisted_tcp"].ShouldNotBe("open", $"but port 53 at the resolver's own address {world.ResolverIp}, which the file does not name, stays shut; probe: {Describe(rewritten)}");
            rewritten["unlisted_udp"].ShouldNotBe("answered", $"and a datagram to it unanswered; probe: {Describe(rewritten)}");

            (await dnat.DeleteAsync()).ShouldBe(0, "control setup: the worker's DNAT must be there to delete");
            var direct = await LookupAsync(setup.ExecPrefix);

            direct.ShouldNotBe(PinnedAnswer, $"control: with the DNAT gone the lookup must not reach the resolver at {world.ResolverIp}, or the answer above did not come through the worker's rewrite");

            output.WriteLine($"{RanMarker} dns-dnat {Describe(rewritten)} control: lookup={direct}".Replace('\n', ' '));
        }
        finally { await FilteredEgressNetns.TeardownAsync(runId, CancellationToken.None); }
    }

    [Fact]
    public async Task A_resolver_address_the_worker_redirects_to_itself_still_answers_the_run()
    {
        if (!BuildsNamespaces()) return;

        // The same rewrite ahead of the guard's input chain: a transparent DNS proxy on the worker REDIRECTs the address
        // the resolv.conf names to itself, so the query reaches the worker at the run's gateway, which the file does not
        // name either. The guard must admit it by the address the run sent it to. The proxy answers at the gateway; port
        // 53 there, asked directly, stays shut; and the control deletes the REDIRECT: the proxy's answer must not come back then.
        var runId = Guid.NewGuid().ToString("N");
        var service = ServiceAddress();
        using var view = new NamespaceResolvConf(runId, $"nameserver {service}\noptions timeout:1 attempts:1\n");
        await using var redirect = await WorkerNat.StageAsync(NamesOf(runId).VethHost, service, "redirect to :53");
        var setup = await FilteredEgressNetns.SetupAsync(runId, new[] { Allowed }, view.Path, timeoutSeconds: 20, CancellationToken.None);

        try
        {
            setup.SetupOk.ShouldBeTrue($"the allowlist namespace must set up on this host: {setup.SetupError}");

            var gateway = await GatewayOfAsync(runId);
            using var proxy = new WorkerResolver(gateway);
            var rewritten = await ProbePort53Async(setup.ExecPrefix, service, gateway);

            rewritten["view"].ShouldBe(File.ReadAllText(view.Path), $"fixture: the namespace must read the resolv.conf the rules were built from; probe: {Describe(rewritten)}");
            rewritten["lookup"].ShouldBe(PinnedAnswer, $"a lookup through the resolv.conf, which names {service}, must be answered by the proxy the worker redirects it to at {gateway} — check each port-53 accept in `nft list table inet {FilteredEgressPlan.NamespaceFor(runId)}` matches `ct original ip daddr`; probe: {Describe(rewritten)}");
            rewritten["resolver_udp"].ShouldBe("answered", $"and DNS to {service} directly over UDP; probe: {Describe(rewritten)}");
            rewritten["resolver_tcp"].ShouldBe("answered", $"and over TCP; probe: {Describe(rewritten)}");
            rewritten["unlisted_tcp"].ShouldNotBe("open", $"but port 53 at the gateway {gateway}, which the file does not name, stays shut though the proxy listens there; probe: {Describe(rewritten)}");
            rewritten["unlisted_udp"].ShouldNotBe("answered", $"and a datagram to it unanswered; probe: {Describe(rewritten)}");

            (await redirect.DeleteAsync()).ShouldBe(0, "control setup: the worker's REDIRECT must be there to delete");
            var direct = await LookupAsync(setup.ExecPrefix);

            direct.ShouldNotBe(PinnedAnswer, $"control: with the REDIRECT gone the lookup must not reach the proxy at {gateway}, or the answer above did not come through the worker's rewrite");

            output.WriteLine($"{RanMarker} dns-redirect {Describe(rewritten)} control: lookup={direct}".Replace('\n', ' '));
        }
        finally { await FilteredEgressNetns.TeardownAsync(runId, CancellationToken.None); }
    }

    /// <summary>An address a resolv.conf names as it names a cluster DNS service's, which only the worker's NAT makes this test's resolver: a random one of TEST-NET-3 (RFC 5737), which is never handed out. A host whose own network answers any DNS query (a fake-IP proxy) answers there too, but never with <see cref="PinnedAnswer"/>.</summary>
    private static string ServiceAddress() => $"203.0.113.{RandomNumberGenerator.GetInt32(1, 255)}";

    /// <summary>
    /// A NAT the worker applies ahead of the run's own rules to DNS the run sends one address — as kube-proxy DNATs a
    /// service address, or a transparent DNS proxy REDIRECTs it to the worker. A table of its own, GUID-named, whose
    /// rules match the run's veth alone, so no other run or test meets them (Rule 12.2); deleted on dispose, best-effort
    /// (12.3).
    /// </summary>
    private sealed class WorkerNat : IAsyncDisposable
    {
        private readonly string _table = $"cs-wnat-{Guid.NewGuid().ToString("N")[..8]}";

        private WorkerNat() { }

        /// <summary>Load the table: DNS over UDP and TCP from <paramref name="vethHost"/> to <paramref name="address"/>, rewritten by <paramref name="rewrite"/> (an nft NAT statement).</summary>
        public static async Task<WorkerNat> StageAsync(string vethHost, string address, string rewrite)
        {
            var nat = new WorkerNat();
            var rules = new[] { "udp", "tcp" }.Select(protocol => $"    iifname \"{vethHost}\" ip daddr {address} {protocol} dport 53 {rewrite}\n");

            (await RunHostExitAsync(["nft", "-f", "-"], $"table ip {nat._table} {{\n  chain prerouting {{\n    type nat hook prerouting priority -100;\n{string.Concat(rules)}  }}\n}}\n")).ShouldBe(0, $"fixture: the worker's NAT for DNS to {address} must load");

            return nat;
        }

        /// <summary>Delete the table, returning <c>nft</c>'s exit code.</summary>
        public Task<int> DeleteAsync() => RunHostExitAsync(["nft", "delete", "table", "ip", _table]);

        public async ValueTask DisposeAsync() => await DeleteAsync();
    }

    /// <summary>
    /// The resolv.conf the run's namespace reads, staged at <c>/etc/netns/&lt;ns&gt;/resolv.conf</c>, which <c>ip netns
    /// exec</c> binds over <c>/etc/resolv.conf</c> for everything it runs there — and handed to the setup as the file its
    /// rules are read from, so the rules and the namespace's view are one file, as the worker's own is for a production
    /// run. The namespace name is the GUID-derived run key's (Rule 12.2); the directory is removed on dispose, and
    /// <c>/etc/netns</c> with it when this made it (12.3).
    /// </summary>
    private sealed class NamespaceResolvConf : IDisposable
    {
        private const string Root = "/etc/netns";

        private readonly string _dir;
        private readonly bool _madeRoot;

        public NamespaceResolvConf(string runId, string text)
        {
            _dir = System.IO.Path.Combine(Root, FilteredEgressPlan.NamespaceFor(runId));
            _madeRoot = !Directory.Exists(Root);

            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path, text);
        }

        public string Path => System.IO.Path.Combine(_dir, "resolv.conf");

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }

            if (_madeRoot)
                try { Directory.Delete(Root); } catch { /* best-effort: not empty, or already gone */ }
        }
    }

    [Fact]
    public async Task Forwarding_a_root_worker_may_not_write_is_named_before_an_allowlist_is_planned_on_it()
    {
        // The setup's `sysctl -w net.ipv4.ip_forward=1` warns and exits 0 on a read-only /proc/sys, so on a root worker
        // whose forwarding reads 0 there the setup succeeds and the allowlist reaches nothing. For root only a mount can
        // refuse the write — file modes never do — so this binds a file reading 0 read-only and asks the real access(2),
        // as the filter probe does of /proc/sys. Needs mount, so root alone; and a lane that builds namespaces, where the
        // forwarding step alone can decide the whole probe — which is asked too, once CanFilter has settled, since both
        // build this process's one probe namespace.
        if (!OperatingSystem.IsLinux() || NonRootWorker.EffectiveUid() != 0 || !BuildsNamespaces()) return;

        using var file = new ReadOnlyForwardingFile();
        await file.StageAsync();

        var refused = $"{file.ReadOnlyPath} reads 0, and this process may not write it";

        FilteredEgressNetns.ForwardingProblem(file.WritablePath).ShouldBeNull("control: the same bytes where root may write them — the setup would turn forwarding on");
        FilteredEgressNetns.ForwardingProblem(file.ReadOnlyPath).ShouldBe(refused, customMessage: $"a read-only mount refuses root's write, so forwarding would stay off; check `grep {file.ReadOnlyPath} /proc/mounts`");

        FilteredEgressNetns.FilterProbe(file.WritablePath).ShouldBeNull("control: the filter probe CanFilter runs holds on this lane where forwarding may be turned on");
        FilteredEgressNetns.FilterProbe(file.ReadOnlyPath).ShouldBe(refused, customMessage: "the probe CanFilter runs asks the forwarding step too: with a namespace buildable, forwarding root may not turn on is what stops an allowlist being filtered");

        output.WriteLine($"{RanMarker} forwarding-read-only");
    }

    /// <summary>A file reading 0 and the same file bound read-only at a second path, both under a GUID-named directory; the bind is unmounted and the directory removed on dispose (Rule 12.2/12.3).</summary>
    private sealed class ReadOnlyForwardingFile : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "cs-forwarding-" + Guid.NewGuid().ToString("N"));

        private bool _mounted;

        public string WritablePath => Path.Combine(_dir, "writable");

        public string ReadOnlyPath => Path.Combine(_dir, "read-only");

        public async Task StageAsync()
        {
            Directory.CreateDirectory(_dir);
            await File.WriteAllTextAsync(WritablePath, "0\n");
            await File.WriteAllTextAsync(ReadOnlyPath, "");

            (await RunHostExitAsync(["mount", "--bind", WritablePath, ReadOnlyPath])).ShouldBe(0, "setup: root must be able to bind a file over another");
            _mounted = true;

            (await RunHostExitAsync(["mount", "-o", "remount,ro,bind", ReadOnlyPath])).ShouldBe(0, "setup: and to make that bind read-only");
        }

        public void Dispose()
        {
            if (_mounted)
                try { using var umount = System.Diagnostics.Process.Start("umount", ReadOnlyPath); umount.WaitForExit(10_000); } catch { /* best-effort */ }

            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>From inside the namespace: the worker's listener at the gateway and at its own address, DNS to the worker over UDP and TCP, port 53 at the gateway, and the allowlisted IP.</summary>
    private static async Task<Dictionary<string, string>> ProbeTheWorkerAsync(FilteredEgressNetns.SetupResult setup, string gateway, string workerIp, TcpListener listener)
    {
        var stdout = await RunHostAsync(setup.ExecPrefix.Concat(["python3", "-c", WorkerProbeScript, gateway, workerIp, PortOf(listener), Allowed]).ToList());
        var line = stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));

        return line is null ? new Dictionary<string, string> { ["gateway"] = $"no probe output: {stdout}", ["worker"] = "?", ["dns_udp"] = "?", ["dns_tcp"] = "?", ["gateway_53"] = "?", ["allowed"] = "?" } : JsonSerializer.Deserialize<Dictionary<string, string>>(line)!;
    }

    /// <summary>From inside the namespace, as JSON: its view of /etc/resolv.conf, a lookup of <see cref="PinnedName"/> through it, DNS to <paramref name="resolver"/> over UDP and TCP, and TCP and a DNS query to port 53 of <paramref name="unlisted"/>.</summary>
    private static async Task<Dictionary<string, string>> ProbePort53Async(IReadOnlyList<string> execPrefix, string resolver, string unlisted)
    {
        var stdout = await RunHostAsync(execPrefix.Concat(["python3", "-c", Port53ProbeScript, resolver, unlisted, PinnedName]).ToList());
        var line = stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));

        return line is null ? new Dictionary<string, string> { ["view"] = $"no probe output: {stdout}", ["lookup"] = "?", ["resolver_udp"] = "?", ["resolver_tcp"] = "?", ["unlisted_tcp"] = "?", ["unlisted_udp"] = "?" } : JsonSerializer.Deserialize<Dictionary<string, string>>(line)!;
    }

    /// <summary>From inside the namespace: a lookup of <see cref="PinnedName"/> through its resolv.conf alone — the address, or the error.</summary>
    private static async Task<string> LookupAsync(IReadOnlyList<string> execPrefix) => (await RunHostAsync(execPrefix.Concat(["python3", "-c", LookupScript, PinnedName]).ToList())).Trim();

    private static string Describe(Dictionary<string, string> probe) => string.Join(' ', probe.Select(p => $"{p.Key}={p.Value}"));

    private static string PortOf(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);

    /// <summary>The run's namespace, table and veth names: the run key's alone, so a plan on any lease names them as the setup did.</summary>
    private static FilteredEgressPlan NamesOf(string runId) => FilteredEgressPlan.Build(runId, Array.Empty<string>(), new EgressSubnetAllocator.Lease { Cidr = "0.0.0.0/30", HostIp = "0.0.0.1", NsIp = "0.0.0.2" }, []);

    /// <summary>The run's gateway: the IPv4 address on the host end of its veth (<see cref="NamesOf"/>), as the kernel holds it — the /30 the setup reserved, read where it took effect.</summary>
    private static async Task<string> GatewayOfAsync(string runId)
    {
        var veth = NamesOf(runId).VethHost;
        var listed = await RunHostAsync(["ip", "-4", "-o", "addr", "show", "dev", veth]);
        var words = listed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var inet = Array.IndexOf(words, "inet");

        (inet >= 0 && inet + 1 < words.Length).ShouldBeTrue($"fixture: the run's host veth {veth} must carry an IPv4 address; `ip -4 -o addr show dev {veth}` printed: {listed}");
        return words[inet + 1].Split('/')[0];
    }

    /// <summary>Connect from the worker, send <paramref name="bytes"/> bytes and half-close, and return the peer's one-line answer — or the socket error that stopped it, or <c>timeout</c>.</summary>
    private static async Task<string> AskAsync(string host, int port, int bytes)
    {
        using var client = new TcpClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            await client.ConnectAsync(IPAddress.Parse(host), port, deadline.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[bytes], deadline.Token);
            client.Client.Shutdown(SocketShutdown.Send);

            using var reader = new StreamReader(stream);
            return (await reader.ReadToEndAsync(deadline.Token)).Trim();
        }
        catch (SocketException exception) { return exception.SocketErrorCode.ToString(); }
        catch (IOException exception) when (exception.InnerException is SocketException socket) { return socket.SocketErrorCode.ToString(); }
        catch (OperationCanceledException) { return "timeout"; }
    }

    /// <summary>Send one datagram from the worker and return how the send went — the guard's reject surfaces as an error on the send itself.</summary>
    private static async Task<string> SendDatagramAsync(string host, int port)
    {
        using var client = new UdpClient();

        try
        {
            await client.SendAsync(new byte[] { 1, 2, 3, 4 }, new IPEndPoint(IPAddress.Parse(host), port));
            return "sent";
        }
        catch (SocketException exception) { return exception.SocketErrorCode.ToString(); }
    }

    /// <summary>What <see cref="ConntrackEntriesAsync"/> runs: the table over netlink, in the layout of <c>/proc/net/nf_conntrack</c>, which a kernel built without CONFIG_NF_CONNTRACK_PROCFS (Ubuntu's, the CI runner's) does not have.</summary>
    private const string ConntrackTable = "conntrack -L -o extended";

    /// <summary>Conntrack's table for this process's network namespace, one tracked flow a line.</summary>
    private static async Task<string[]> ConntrackEntriesAsync() =>
        (await RunHostAsync(["conntrack", "-L", "-o", "extended"])).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Send <paramref name="text"/> on the worker's connected datagram socket and return how the send went.</summary>
    private static async Task<string> SendOnAsync(UdpClient client, string text)
    {
        try
        {
            await client.SendAsync(Encoding.ASCII.GetBytes(text));
            return "sent";
        }
        catch (SocketException exception) { return exception.SocketErrorCode.ToString(); }
    }

    /// <summary>The next datagram on the worker's connected socket, as text — or <c>timeout</c> after <paramref name="seconds"/>, or the socket error that ended the wait.</summary>
    private static async Task<string> ReceiveOnAsync(UdpClient client, int seconds)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));

        try { return Encoding.ASCII.GetString((await client.ReceiveAsync(deadline.Token)).Buffer); }
        catch (OperationCanceledException) { return "timeout"; }
        catch (SocketException exception) { return exception.SocketErrorCode.ToString(); }
    }

    /// <summary>The namespace's count of TCP segments received (<c>InSegs</c> in its <c>/proc/net/snmp</c>): it moves when a segment reaches the namespace's TCP stack, whether or not a socket there takes it.</summary>
    private static async Task<string> TcpSegmentsInAsync(IReadOnlyList<string> execPrefix)
    {
        var tcp = (await RunHostAsync(execPrefix.Concat(["cat", "/proc/net/snmp"]).ToList())).Split('\n').Where(line => line.StartsWith("Tcp: ", StringComparison.Ordinal)).Select(line => line.Trim().Split(' ')).ToList();

        (tcp.Count == 2 && tcp[0].Contains("InSegs")).ShouldBeTrue($"fixture: the namespace's /proc/net/snmp must name its Tcp counters on one line and give them on the next: {string.Join(" | ", tcp.Select(fields => string.Join(' ', fields)))}");
        return tcp[1][Array.IndexOf(tcp[0], "InSegs")];
    }

    /// <summary>The link-local address <paramref name="listArgv"/> reports once duplicate-address detection has finished with it (no longer "tentative"), or null where IPv6 is disabled and there is none. Fails if it never settles.</summary>
    private static async Task<string?> SettledLinkLocalAsync(IReadOnlyList<string> listArgv)
    {
        var watch = Stopwatch.StartNew();

        while (true)
        {
            var line = (await RunHostAsync(listArgv)).Split('\n').FirstOrDefault(l => l.Contains(" fe80:", StringComparison.OrdinalIgnoreCase));

            if (line is null && watch.Elapsed >= TimeSpan.FromSeconds(3)) return null;   // no link-local at all: IPv6 is off here

            if (line is not null && !line.Contains("tentative", StringComparison.Ordinal))
                return line.Split(' ', StringSplitOptions.RemoveEmptyEntries).First(t => t.StartsWith("fe80:", StringComparison.OrdinalIgnoreCase)).Split('/')[0];

            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15), $"the link-local address never left duplicate-address detection: {line?.Trim()}");
            await Task.Delay(200);
        }
    }

    /// <summary>The MAC address in one line of <c>ip -o link show</c> — the word after <c>link/ether</c>.</summary>
    private static string LinkAddress(string line)
    {
        var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var ether = Array.IndexOf(words, "link/ether");

        (ether >= 0 && ether + 1 < words.Length).ShouldBeTrue($"fixture: no link/ether address in `{line.Trim()}`");
        return words[ether + 1];
    }

    /// <summary>From inside the namespace, as JSON: the worker's listener at the gateway (argv 1) and its own address (argv 2) on port argv 3, a DNS query to that address over UDP and TCP, a connection to port 53 at the gateway, and port 80 of the allowlisted argv 4. It never fails on a shut door — the test decides.</summary>
    private const string WorkerProbeScript = """
        import json, socket, struct, sys
        gateway, worker, port, allowed = sys.argv[1], sys.argv[2], int(sys.argv[3]), sys.argv[4]
        query = bytes.fromhex('123401000001000000000000076578616d706c6503636f6d0000010001')
        def tcp(host, port):
            try:
                socket.create_connection((host, port), timeout=3).close(); return 'open'
            except OSError as e:
                return type(e).__name__
        def dns_udp(host):
            try:
                s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.settimeout(3); s.sendto(query, (host, 53)); s.recvfrom(512); return 'answered'
            except OSError as e:
                return type(e).__name__
        def dns_tcp(host):
            try:
                s = socket.create_connection((host, 53), timeout=3); s.sendall(struct.pack('!H', len(query)) + query)
                return 'answered' if len(s.recv(512)) > 2 else 'empty'
            except OSError as e:
                return type(e).__name__
        print(json.dumps({'gateway': tcp(gateway, port), 'worker': tcp(worker, port), 'dns_udp': dns_udp(worker), 'dns_tcp': dns_tcp(worker), 'gateway_53': tcp(gateway, 53), 'allowed': tcp(allowed, 80)}))
        """;

    /// <summary>The name <see cref="Port53ProbeScript"/> looks up through the namespace's resolv.conf, under the reserved <c>.test</c> TLD (RFC 6761), which no /etc/hosts names.</summary>
    private const string PinnedName = "dns-pin.test";

    /// <summary>What <see cref="PeerResolverScript"/> answers every A query with: an address in TEST-NET-1 (RFC 5737), so a lookup that returns it was answered by that resolver and no other.</summary>
    private const string PinnedAnswer = "192.0.2.53";

    /// <summary>
    /// From inside the namespace, as JSON: the namespace's own /etc/resolv.conf; a lookup of argv 3 through the C
    /// library's resolver, which reads that file — the query a tool in the run makes; a DNS query over UDP and TCP to the
    /// resolver argv 1; and to port 53 of argv 2, which the file does not name, a TCP connection — the channel that
    /// carries any bytes — and a DNS query over UDP. It never fails on a shut door — the test decides.
    /// </summary>
    private const string Port53ProbeScript = """
        import json, socket, struct, sys
        resolver, unlisted, name = sys.argv[1], sys.argv[2], sys.argv[3]
        query = bytes.fromhex('123401000001000000000000076578616d706c6503636f6d0000010001')
        def tcp(host, port):
            try:
                socket.create_connection((host, port), timeout=3).close(); return 'open'
            except OSError as e:
                return type(e).__name__
        def dns_udp(host):
            try:
                s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.settimeout(3); s.sendto(query, (host, 53)); s.recvfrom(512); return 'answered'
            except OSError as e:
                return type(e).__name__
        def dns_tcp(host):
            try:
                s = socket.create_connection((host, 53), timeout=3); s.sendall(struct.pack('!H', len(query)) + query)
                return 'answered' if len(s.recv(512)) > 2 else 'empty'
            except OSError as e:
                return type(e).__name__
        def lookup(name):
            try:
                return socket.getaddrinfo(name, 53, socket.AF_INET, socket.SOCK_STREAM)[0][4][0]
            except OSError as e:
                return type(e).__name__ + ':' + str(e)
        print(json.dumps({'view': open('/etc/resolv.conf').read(), 'lookup': lookup(name), 'resolver_udp': dns_udp(resolver), 'resolver_tcp': dns_tcp(resolver), 'unlisted_tcp': tcp(unlisted, 53), 'unlisted_udp': dns_udp(unlisted)}))
        """;

    /// <summary>A lookup of argv 1 through the C library's resolver, which reads the namespace's /etc/resolv.conf: the address, or the error.</summary>
    private const string LookupScript = """
        import socket, sys
        try:
            print(socket.getaddrinfo(sys.argv[1], 53, socket.AF_INET, socket.SOCK_STREAM)[0][4][0])
        except OSError as e:
            print(type(e).__name__ + ':' + str(e))
        """;

    /// <summary>
    /// A resolver in the peer's namespace: TCP port 53 on every address the peer holds, UDP port 53 on each of argv 2…
    /// (a datagram's answer must leave from the address it was sent to, or the worker's NAT cannot hand it back), each A
    /// query answered with the one record argv 1 and anything else with no record. Prints <c>ready</c> once every
    /// socket is bound.
    /// </summary>
    private const string PeerResolverScript = """
        import socket, struct, sys, threading
        record = socket.inet_aton(sys.argv[1])
        def answer(q):
            end = 12
            while q[end]:
                end += 1 + q[end]
            end += 5
            a = q[end - 4:end - 2] == b'\x00\x01'
            header = q[:2] + b'\x81\x80\x00\x01' + (b'\x00\x01' if a else b'\x00\x00') + b'\x00\x00\x00\x00'
            return header + q[12:end] + (b'\xc0\x0c\x00\x01\x00\x01\x00\x00\x00\x3c\x00\x04' + record if a else b'')
        def exactly(c, n):
            data = b''
            while len(data) < n:
                chunk = c.recv(n - len(data))
                if not chunk:
                    raise OSError('closed')
                data += chunk
            return data
        def serve_udp(u):
            while True:
                q, sender = u.recvfrom(512)
                try:
                    u.sendto(answer(q), sender)
                except (IndexError, OSError):
                    pass
        def serve_tcp(c):
            try:
                c.settimeout(10)
                r = answer(exactly(c, struct.unpack('!H', exactly(c, 2))[0]))
                c.sendall(struct.pack('!H', len(r)) + r)
            except (IndexError, OSError, struct.error):
                pass
            c.close()
        t = socket.socket(); t.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1); t.bind(('0.0.0.0', 53)); t.listen(16)
        for address in sys.argv[2:]:
            u = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); u.bind((address, 53))
            threading.Thread(target=serve_udp, args=(u,), daemon=True).start()
        print('ready', flush=True)
        while True:
            threading.Thread(target=serve_tcp, args=(t.accept()[0],), daemon=True).start()
        """;

    /// <summary>Connect to argv 1 (a scoped link-local address) on port argv 2 and print <c>open</c> or the error.</summary>
    private const string ConnectScript = """
        import socket, sys
        try:
            socket.create_connection((sys.argv[1], int(sys.argv[2])), timeout=3).close(); print('open')
        except OSError as e:
            print(type(e).__name__)
        """;

    /// <summary>Upload argv 3 bytes to argv 1:argv 2, half-close, and print the peer's answer or the error that stopped it.</summary>
    private const string UploadScript = """
        import socket, sys
        try:
            s = socket.create_connection((sys.argv[1], int(sys.argv[2])), timeout=5); s.settimeout(20)
            s.sendall(b'x' * int(sys.argv[3])); s.shutdown(socket.SHUT_WR)
            print(s.recv(200).decode().strip())
        except OSError as e:
            print(type(e).__name__)
        """;

    /// <summary>What the sandbox listens with, on TCP and UDP port argv 1: given an address and port in argv 2 and 3, first sends one datagram there from that UDP port, as a peer answering on it would; prints <c>ready</c>, counts every connection and byte that reaches it until its stdin closes, then prints the tally.</summary>
    private const string SandboxListenerScript = """
        import select, socket, sys
        port = int(sys.argv[1])
        t = socket.socket(); t.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1); t.bind(('0.0.0.0', port)); t.listen(8)
        u = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); u.bind(('0.0.0.0', port))
        if len(sys.argv) > 3:
            u.sendto(b'FORGED', (sys.argv[2], int(sys.argv[3])))
        print('ready', flush=True)
        accepted, tcp, udp, conns, done = 0, 0, 0, [], False
        while not done:
            readable, _, _ = select.select([t, u, sys.stdin] + conns, [], [], 30)
            done = not readable or sys.stdin in readable
            for s in readable:
                if s is t:
                    conns.append(t.accept()[0]); accepted += 1
                elif s is u:
                    udp += len(u.recv(65536))
                elif s is not sys.stdin:
                    data = s.recv(65536); tcp += len(data)
                    if not data:
                        conns.remove(s); s.close()
        print('SANDBOX accepted=%d tcp=%d udp=%d' % (accepted, tcp, udp), flush=True)
        """;

    /// <summary>The peer: serves TCP on every address it holds, port argv 1, answering each connection with how many bytes it got before the half-close; and answers each datagram to that port at its address argv 2 with the datagram prefixed <c>PEER:</c>, from that address, as a connected client expects.</summary>
    private const string PeerServerScript = """
        import socket, sys, threading
        s = socket.socket(); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1); s.bind(('0.0.0.0', int(sys.argv[1]))); s.listen(16)
        u = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); u.bind((sys.argv[2], int(sys.argv[1])))
        def echo():
            while True:
                data, sender = u.recvfrom(65536)
                u.sendto(b'PEER:' + data, sender)
        threading.Thread(target=echo, daemon=True).start()
        print('ready', flush=True)
        def serve(c):
            n = 0
            try:
                c.settimeout(30)
                while True:
                    data = c.recv(65536)
                    if not data:
                        break
                    n += len(data)
                c.sendall(b'PEER got %d bytes\n' % n)
            except OSError:
                pass
            c.close()
        while True:
            threading.Thread(target=serve, args=(s.accept()[0],), daemon=True).start()
        """;

    /// <summary>
    /// A resolver the worker serves on its own address, port 53, over UDP and TCP — the resolver a <c>resolv.conf</c>
    /// naming a worker address sends the namespace to, or a transparent DNS proxy redirects it to. It answers an A
    /// question with the one record <see cref="PinnedAnswer"/> and any other with no record, so a probe can tell an
    /// answer from a drop and a lookup that reaches it returns that address.
    /// </summary>
    private sealed class WorkerResolver : IDisposable
    {
        private readonly UdpClient _udp;
        private readonly TcpListener _tcp;
        private readonly CancellationTokenSource _stop = new();

        public WorkerResolver(string address)
        {
            var endpoint = new IPEndPoint(IPAddress.Parse(address), 53);

            _udp = new UdpClient(endpoint);
            _tcp = new TcpListener(endpoint);
            _tcp.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _tcp.Start();

            _ = ServeUdpAsync();
            _ = ServeTcpAsync();
        }

        private async Task ServeUdpAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var query = await _udp.ReceiveAsync(_stop.Token);
                    await _udp.SendAsync(Answer(query.Buffer), query.RemoteEndPoint, _stop.Token);
                }
                catch (Exception) when (_stop.IsCancellationRequested) { return; }
                catch (SocketException) { /* an ICMP error for an earlier answer surfaces here; keep serving */ }
                catch (IndexOutOfRangeException) { /* a datagram too short to hold a question; keep serving */ }
            }
        }

        private async Task ServeTcpAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var client = await _tcp.AcceptTcpClientAsync(_stop.Token);
                    var stream = client.GetStream();
                    var length = new byte[2];
                    await stream.ReadExactlyAsync(length, _stop.Token);

                    var query = new byte[(length[0] << 8) | length[1]];
                    await stream.ReadExactlyAsync(query, _stop.Token);
                    await stream.WriteAsync(length.Concat(Answer(query)).ToArray(), _stop.Token);
                }
                catch (Exception) when (_stop.IsCancellationRequested) { return; }
                catch (Exception) { /* one broken query must not stop the resolver */ }
            }
        }

        /// <summary>The question back with the response bit set, and for an A question the one record <see cref="PinnedAnswer"/>, named by a pointer to the question's name.</summary>
        private static byte[] Answer(byte[] query)
        {
            var end = QuestionEnd(query);
            var isA = query[end - 4] == 0 && query[end - 3] == 1;

            byte[] header = [query[0], query[1], 0x81, 0x80, 0, 1, 0, (byte)(isA ? 1 : 0), 0, 0, 0, 0];
            byte[] record = isA ? [0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 0x3c, 0, 4, .. IPAddress.Parse(PinnedAnswer).GetAddressBytes()] : [];

            return [.. header, .. query[12..end], .. record];
        }

        /// <summary>Where the question ends: past its name's labels and their terminating zero, then its type and class.</summary>
        private static int QuestionEnd(byte[] query)
        {
            var end = 12;
            while (query[end] != 0) end += 1 + query[end];

            return end + 5;
        }

        public void Dispose()
        {
            _stop.Cancel();
            _udp.Dispose();
            _tcp.Stop();
        }
    }

    /// <summary>A python3 process inside a namespace, behind its <c>ip netns exec</c> prefix, that prints <c>ready</c> once it listens. Killed on Dispose, whatever it was doing.</summary>
    private sealed class NamespaceProcess : IDisposable
    {
        private readonly Process _process;

        private NamespaceProcess(Process process) { _process = process; }

        public static async Task<NamespaceProcess> StartAsync(IReadOnlyList<string> execPrefix, string script, params string[] args)
        {
            var psi = new ProcessStartInfo { FileName = execPrefix[0], UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in execPrefix.Skip(1).Concat(["python3", "-u", "-c", script]).Concat(args)) psi.ArgumentList.Add(arg);

            var started = new NamespaceProcess(Process.Start(psi)!);
            var ready = await started._process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(read => read.IsCompletedSuccessfully ? read.Result : null);

            if (ready == "ready") return started;

            started.Dispose();
            throw new Xunit.Sdk.XunitException($"the listener behind `{string.Join(' ', execPrefix)}` did not print ready within 10s (got '{ready}'); stderr: {await started._process.StandardError.ReadToEndAsync()}");
        }

        /// <summary>Close its stdin, which ends it, and return the last line it printed — bounded (Rule 12.10).</summary>
        public async Task<string> StopAsync()
        {
            _process.StandardInput.Close();

            var rest = await _process.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
            return rest.Trim().Split('\n').Last().Trim();
        }

        public void Dispose()
        {
            try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already exited */ }
            _process.Dispose();
        }
    }

    /// <summary>
    /// A peer the worker reaches through an uplink of its own: a namespace behind a veth whose worker end has an MTU of
    /// 1400 while the peer's end keeps 1500, so the peer advertises a segment the uplink cannot carry and the worker must
    /// answer an oversized packet with frag-needed. The worker routes a /28 of TEST-NET-2 (RFC 5737, never handed out)
    /// there; the run's /30 is the first /30 of that /28, so its connected route shadows the peer's address at the run's
    /// .2 exactly as a /30 handed out over a peer's subnet would. The peer holds that .2, the allowlisted .9, a resolver's
    /// .10 and a non-resolver's .11 on its loopback, and serves <see cref="PeerPort"/> on all of them, and DNS on the last
    /// two once asked (<see cref="ServeDnsAsync"/>). The block and every name are random, and Dispose removes the
    /// namespace — and with it both ends of the veth and the route through it.
    /// </summary>
    private sealed class UplinkWorld : IAsyncDisposable
    {
        public const int PeerPort = 7000;

        private readonly string _peerNamespace;
        private readonly string _uplink;
        private readonly string _block;
        private readonly int _base;
        private NamespaceProcess? _server;
        private NamespaceProcess? _resolver;

        private UplinkWorld(string suffix, int @base)
        {
            _peerNamespace = $"cs-peer-{suffix}";
            _uplink = $"csu-{suffix}";
            _base = @base;
            _block = $"198.51.100.{@base}/28";
            RunLease = new EgressSubnetAllocator.Lease { Cidr = $"198.51.100.{@base}/30", HostIp = Address(1), NsIp = Address(2) };
        }

        /// <summary>The run's /30: the first of the routed /28, so it shadows the peer's .2.</summary>
        public EgressSubnetAllocator.Lease RunLease { get; }

        public string ShadowedIp => RunLease.NsIp;

        public string AllowedIp => Address(9);

        /// <summary>The address the run's resolv.conf names as its resolver.</summary>
        public string ResolverIp => Address(10);

        /// <summary>An address the peer serves DNS at too, which no resolv.conf names: port 53 there is the channel the pin shuts.</summary>
        public string UnlistedIp => Address(11);

        private string WorkerEnd => Address(17);

        private string[] Peer => ["ip", "netns", "exec", _peerNamespace];

        private string PeerEnd => Address(18);

        private string Address(int offset) => $"198.51.100.{_base + offset}";

        public static async Task<UplinkWorld> StartAsync()
        {
            var world = new UplinkWorld(Guid.NewGuid().ToString("N")[..8], RandomNumberGenerator.GetInt32(0, 8) * 32);

            try { await world.BuildAsync(); }
            catch
            {
                await world.DisposeAsync();
                throw;
            }

            return world;
        }

        /// <summary>Serve DNS on port 53 at <see cref="ResolverIp"/> and <see cref="UnlistedIp"/> alike, answering each A query with <see cref="PinnedAnswer"/>.</summary>
        public async Task ServeDnsAsync() => _resolver = await NamespaceProcess.StartAsync(Peer, PeerResolverScript, PinnedAnswer, ResolverIp, UnlistedIp);

        private async Task BuildAsync()
        {
            var peer = Peer;
            var down = $"csd-{_uplink[4..]}";

            string[][] steps =
            [
                ["ip", "netns", "add", _peerNamespace],
                ["ip", "link", "add", _uplink, "mtu", "1400", "type", "veth", "peer", "name", down, "mtu", "1500"],
                ["ip", "link", "set", down, "netns", _peerNamespace],
                ["ip", "addr", "add", $"{WorkerEnd}/30", "dev", _uplink],
                ["ip", "link", "set", _uplink, "up"],
                [.. peer, "ip", "addr", "add", $"{PeerEnd}/30", "dev", down],
                [.. peer, "ip", "link", "set", down, "up"],
                [.. peer, "ip", "link", "set", "lo", "up"],
                [.. peer, "ip", "addr", "add", $"{ShadowedIp}/32", "dev", "lo"],
                [.. peer, "ip", "addr", "add", $"{AllowedIp}/32", "dev", "lo"],
                [.. peer, "ip", "addr", "add", $"{ResolverIp}/32", "dev", "lo"],
                [.. peer, "ip", "addr", "add", $"{UnlistedIp}/32", "dev", "lo"],
                [.. peer, "ip", "route", "add", "default", "via", WorkerEnd],
                ["ip", "route", "replace", _block, "via", PeerEnd, "dev", _uplink],
            ];

            foreach (var step in steps)
                (await RunHostExitAsync(step)).ShouldBe(0, $"fixture: `{string.Join(' ', step)}` must succeed to stage the peer behind the uplink");

            _server = await NamespaceProcess.StartAsync(peer, PeerServerScript, PeerPort.ToString(CultureInfo.InvariantCulture), ShadowedIp);
        }

        public async ValueTask DisposeAsync()
        {
            _server?.Dispose();
            _resolver?.Dispose();

            await RunHostExitAsync(["ip", "netns", "del", _peerNamespace]);   // takes the peer's veth end, and so the pair and the route through it
            await RunHostExitAsync(["ip", "link", "del", _uplink]);           // best-effort: gone with its peer already
            await RunHostExitAsync(["ip", "route", "del", _block]);           // best-effort: gone with the device already
        }
    }

    /// <summary>A lane with ip and nft builds namespaces (the root lane); there, one that cannot be built is a failure, not a skip.</summary>
    private static bool BuildsNamespaces()
    {
        if (!FilteredEgressNetns.IsSupported) return false;

        FilteredEgressNetns.CanFilter.ShouldBeTrue($"ip and nft are here, but this process could not filter an allowlist run on this host ({FilteredEgressNetns.FilterUnavailableReason})");
        return true;
    }

    private static async Task<int> RunHostExitAsync(IReadOnlyList<string> argv, string? stdin = null)
    {
        var psi = new System.Diagnostics.ProcessStartInfo { FileName = argv[0], UseShellExecute = false, RedirectStandardInput = stdin is not null, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in argv.Skip(1)) psi.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(psi)!;

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync();

        return process.ExitCode;
    }

    private static async Task<string> RunHostAsync(IReadOnlyList<string> argv)
    {
        var psi = new System.Diagnostics.ProcessStartInfo { FileName = argv[0], UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in argv.Skip(1)) psi.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        return stdout;
    }

    private static Task<FilteredEgressNetns.Outcome> CurlInFilteredNetnsAsync(string allow, string target) =>
        FilteredEgressNetns.RunAsync(
            runId: Guid.NewGuid().ToString("N"),
            allowedIps: new[] { allow },
            command: "curl",
            args: new[] { "-s", "-m", "6", "-o", "/dev/null", $"https://{target}" },
            timeoutSeconds: 40,
            cancellationToken: CancellationToken.None);

    /// <summary>Run curl against <paramref name="target"/> behind the netns ExecPrefix (the durable-launch shape) and return its exit code.</summary>
    private static async Task<int> RunViaPrefixAsync(IReadOnlyList<string> execPrefix, string target)
    {
        var argv = execPrefix.Concat(new[] { "curl", "-s", "-m", "6", "-o", "/dev/null", $"https://{target}" }).ToList();

        var psi = new System.Diagnostics.ProcessStartInfo { FileName = argv[0], UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in argv.Skip(1)) psi.ArgumentList.Add(a);

        using var p = System.Diagnostics.Process.Start(psi)!;
        await p.WaitForExitAsync();
        return p.ExitCode;
    }
}
