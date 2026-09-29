namespace CodeSpace.Core.Services.Agents.Sandbox.Isolation;

/// <summary>
/// The PURE command-sequence builder for a deny-by-default egress allowlist (B3.2 enforcement) — a per-run network
/// namespace whose only egress is NAT'd to the host, with an nftables FORWARD filter that permits the netns subnet
/// to reach ONLY the resolved allowlist IPs (+ DNS to the resolvers its resolv.conf names, <see cref="NamespaceResolvers"/>),
/// dropping everything else, and a guard on the host veth that keeps the namespace and the worker out of each other
/// (<see cref="BuildVethGuardRuleset"/>). It produces the <c>ip</c> /
/// <c>nft</c> / <c>sysctl</c> argv sequences for SETUP, the <c>ip netns exec</c> prefix the confined command runs behind, and
/// the TEARDOWN sequence — all pure data so the rules (the allow set, the default-drop, the teardown) are unit-pinned
/// without root. The privileged executor (and its CI E2E) runs them; this file never touches the kernel.
///
/// <para>FAIL-CLOSED + scoped: the drop is scoped to the netns subnet (<c>ip saddr</c>), so it never affects the
/// host's own forwarding; masquerade is interface-agnostic (no fragile egress-NIC detection). v1 pins the allowlist
/// to IPs resolved at setup — a CDN-backed host with rotating IPs is a known limitation (hostname/SNI filtering via a
/// proxy is a follow-up). Names are GUID-suffixed so concurrent runs never collide on the shared kernel.</para>
/// </summary>
public sealed record FilteredEgressPlan
{
    /// <summary>The netns name (GUID-suffixed) — also the nft table name + the teardown handle.</summary>
    public required string Namespace { get; init; }

    /// <summary>The host-side veth name.</summary>
    public required string VethHost { get; init; }

    /// <summary>The netns-side veth name.</summary>
    public required string VethNs { get; init; }

    /// <summary>The /30 subnet the veth pair uses (host = .1, ns = .2).</summary>
    public required string HostAddrCidr { get; init; }
    public required string NsAddrCidr { get; init; }
    public required string HostIp { get; init; }
    public required string NsIp { get; init; }
    public required string NsSubnetCidr { get; init; }

    /// <summary>The argv sequences (each an executable + args) that build the filtered netns, in order — run BEFORE <see cref="NftRuleset"/> is applied.</summary>
    public required IReadOnlyList<IReadOnlyList<string>> SetupCommands { get; init; }

    /// <summary>
    /// The argv that asks the kernel how the host reaches the namespace's end. Run after <see cref="SetupCommands"/>:
    /// the /30 was chosen from the routes the host lists (<see cref="HostRoutedPrefixes"/>), but only the kernel's own
    /// lookup accounts for a policy rule, or the null route in a table one consults before <c>main</c>, that would
    /// discard the run's replies after a clean setup. The plan has no one port to name, and its NAT'd replies are
    /// routed on input, so a rule keyed on the protocol, a port, a mark or the uplink (<c>iif</c>) can still divert them.
    /// </summary>
    public required IReadOnlyList<string> RouteCheckArgv { get; init; }

    /// <summary>Why <see cref="RouteCheckArgv"/>'s answer does not take the namespace's traffic through its own host veth, or null when it does.</summary>
    internal string? RouteCheckFailure(int exit, string output)
    {
        var asked = string.Join(' ', RouteCheckArgv);

        if (exit != 0) return $"{asked} → exit {exit}: {output.Trim()} — this host cannot route replies back to {NsSubnetCidr} (a null route or a policy rule discards them), so the run could never be answered";

        return RoutedDevice(output) == VethHost ? null : $"{asked} → {output.Trim()} — this host routes {NsIp} through something other than the run's own veth {VethHost}, so the run could never be answered";
    }

    /// <summary>
    /// The device <c>ip route get</c> answered with — the word after its one <c>dev</c> — or null for anything else.
    /// Read from the text answer, not <c>-j</c>: <c>route get</c> learned JSON only in iproute2 5.0, while the route
    /// listing the allocator reads already parses on some 4.x builds (Debian 10's 4.20), where <c>route get</c> prints
    /// text under <c>-j</c> too.
    /// </summary>
    private static string? RoutedDevice(string output)
    {
        var words = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var dev = Array.IndexOf(words, "dev");

        return dev >= 0 && dev == Array.LastIndexOf(words, "dev") && dev + 1 < words.Length ? words[dev + 1] : null;
    }

    /// <summary>The nftables ruleset (NAT masquerade + the scoped default-drop forward allowlist, then the guard on the run's host veth — <see cref="BuildVethGuardRuleset"/>) applied via <c>nft -f -</c> on STDIN after <see cref="SetupCommands"/>, as one transaction. Kept off the argv (multi-line) so it pipes cleanly.</summary>
    public required string NftRuleset { get; init; }

    /// <summary>The argv that applies <see cref="NftRuleset"/> on stdin.</summary>
    public IReadOnlyList<string> NftApplyArgv { get; } = new[] { "nft", "-f", "-" };

    /// <summary>The prefix the confined command runs behind so it executes INSIDE the filtered netns (e.g. <c>ip netns exec &lt;ns&gt;</c>).</summary>
    public required IReadOnlyList<string> ExecPrefix { get; init; }

    /// <summary>The teardown argv sequences (delete the netns + the nft table), in order. Run best-effort even on failure.</summary>
    public required IReadOnlyList<IReadOnlyList<string>> TeardownCommands { get; init; }

    /// <summary>
    /// Build the plan for an allowlist of already-resolved destination IPs. <paramref name="runId"/> seeds the
    /// GUID-derived unique names; <paramref name="subnet"/> is the COLLISION-FREE /30 the caller reserved from
    /// <see cref="EgressSubnetAllocator"/> (so no two concurrent runs on the host — in this worker process or any
    /// other — share a subnet, a host-global nft-chain hazard); <paramref name="allowedIps"/> are the only reachable
    /// destinations, plus DNS to <paramref name="resolvers"/> alone — the nameservers the namespace's tools query
    /// (<see cref="NamespaceResolvers"/>), none when it has none it could reach.
    /// </summary>
    public static FilteredEgressPlan Build(string runId, IReadOnlyList<string> allowedIps, EgressSubnetAllocator.Lease subnet, IReadOnlyList<string> resolvers)
    {
        var ns = NamespaceFor(runId);
        var slug = Slug(runId);
        var vethHost = $"csh-{slug}";
        var vethNs = $"csn-{slug}";

        var hostIp = subnet.HostIp;
        var nsIp = subnet.NsIp;
        var subnetCidr = subnet.Cidr;
        var table = ns;   // one nft table per run, named like the ns

        var nftRuleset = BuildNftRuleset(table, subnetCidr, allowedIps, resolvers) + BuildVethGuardRuleset(table, vethHost, subnetCidr, resolvers);

        var setup = NamespaceSetup(ns, vethHost, vethNs, subnet);
        setup.Add(new[] { "ip", "netns", "exec", ns, "ip", "route", "add", "default", "via", hostIp });
        setup.Add(new[] { "sysctl", "-w", "net.ipv4.ip_forward=1" });

        return new FilteredEgressPlan
        {
            Namespace = ns,
            VethHost = vethHost,
            VethNs = vethNs,
            HostAddrCidr = $"{hostIp}/30",
            NsAddrCidr = $"{nsIp}/30",
            HostIp = hostIp,
            NsIp = nsIp,
            RouteCheckArgv = RouteCheck(nsIp, hostIp),
            NsSubnetCidr = subnetCidr,
            SetupCommands = setup,
            NftRuleset = nftRuleset,
            ExecPrefix = new[] { "ip", "netns", "exec", ns },
            TeardownCommands = TeardownCommandsFor(runId),
        };
    }

    /// <summary>The namespace, its veth pair and the /30 on both ends, with loopback up. The plan's routing and forwarding follow it.</summary>
    private static List<IReadOnlyList<string>> NamespaceSetup(string ns, string vethHost, string vethNs, EgressSubnetAllocator.Lease subnet) => new()
    {
        new[] { "ip", "netns", "add", ns },
        new[] { "ip", "link", "add", vethHost, "type", "veth", "peer", "name", vethNs },
        new[] { "ip", "link", "set", vethNs, "netns", ns },
        new[] { "ip", "addr", "add", $"{subnet.HostIp}/30", "dev", vethHost },
        new[] { "ip", "link", "set", vethHost, "up" },
        new[] { "ip", "netns", "exec", ns, "ip", "addr", "add", $"{subnet.NsIp}/30", "dev", vethNs },
        new[] { "ip", "netns", "exec", ns, "ip", "link", "set", vethNs, "up" },
        new[] { "ip", "netns", "exec", ns, "ip", "link", "set", "lo", "up" },
    };

    /// <summary>The route lookup to the namespace's end from the gateway.</summary>
    private static IReadOnlyList<string> RouteCheck(string nsIp, string hostIp) => ["ip", "route", "get", nsIp, "from", hostIp];

    /// <summary>The per-run netns / nft-table name — derived PURELY from <paramref name="runId"/>, so a reaper / teardown reconstructs it with no setup-time state.</summary>
    public static string NamespaceFor(string runId) => $"cs-egr-{Slug(runId)}";

    /// <summary>
    /// The teardown argv sequences (delete the netns + host veth + nft table), in order — reconstructed PURELY from
    /// <paramref name="runId"/> (every name is runId-derived) so a reap needs NO setup-time subnet/state, even from a
    /// different worker after a crash. Run best-effort even on failure.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> TeardownCommandsFor(string runId)
    {
        var ns = NamespaceFor(runId);
        var vethHost = $"csh-{Slug(runId)}";

        return new List<IReadOnlyList<string>>
        {
            new[] { "ip", "netns", "del", ns },          // removes the ns + its veth end
            new[] { "ip", "link", "del", vethHost },     // best-effort: del may already be gone with the ns
            new[] { "nft", "delete", "table", "ip", ns },
            new[] { "nft", "delete", "table", "inet", ns },   // the veth guard, or the seal a network-off run launched before the relay carries; best-effort, absent for an allowlist run launched before the guard
        };
    }

    /// <summary>The nftables ruleset fed to <c>nft -f -</c> on stdin: NAT masquerade for the subnet + a default-drop forward filter scoped to the subnet that permits established + DNS to <paramref name="resolvers"/> (<see cref="DnsAccepts"/>) + the allowed IPs.</summary>
    internal static string BuildNftRuleset(string table, string subnet, IReadOnlyList<string> allowedIps, IReadOnlyList<string> resolvers)
    {
        var lines = new List<string>
        {
            $"table ip {table} {{",
            "  chain postrouting {",
            "    type nat hook postrouting priority 100;",
            $"    ip saddr {subnet} masquerade",
            "  }",
            "  chain forward {",
            "    type filter hook forward priority 0;",
            "    ct state established,related accept",
        };

        lines.AddRange(DnsAccepts($"    ip saddr {subnet}", resolvers));

        if (allowedIps.Count > 0)
            lines.Add($"    ip saddr {subnet} ip daddr {SetOf(allowedIps)} accept");

        lines.Add($"    ip saddr {subnet} drop");   // scoped default-drop: only the netns subnet, never the host's own forwarding
        lines.Add("  }");
        lines.Add("}");

        return string.Join("\n", lines) + "\n";
    }

    /// <summary>
    /// The accepts for DNS, UDP and TCP port 53, from what <paramref name="match"/> matches to <paramref name="resolvers"/>
    /// alone — none at all when there are none. The forward table and the guard read the one list: a resolver beyond the
    /// worker is met on FORWARD, one the worker serves on its own address on INPUT. Port 53 open to any address would be
    /// a tunnel, not DNS: the run could open TCP to any host that listens there and carry whatever bytes it likes.
    ///
    /// <para>A resolver is matched as the address the run sent to, which conntrack records before any NAT
    /// (<c>ct original ip daddr</c>). The worker's own NAT runs ahead of both hooks and may rewrite it: kube-proxy DNATs a
    /// cluster DNS service address on a node's PREROUTING, and a transparent DNS proxy REDIRECTs it to the worker. By
    /// then the packet carries an address no resolv.conf names. The port is the packet's own, 53, as it was before the
    /// pin, so a NAT that moves DNS to another port is still not admitted.</para>
    /// </summary>
    private static IEnumerable<string> DnsAccepts(string match, IReadOnlyList<string> resolvers) =>
        resolvers.Count == 0 ? [] : [$"{match} ct original ip daddr {SetOf(resolvers)} udp dport 53 accept", $"{match} ct original ip daddr {SetOf(resolvers)} tcp dport 53 accept"];

    /// <summary>An anonymous nft set of <paramref name="addresses"/>, which must not be empty.</summary>
    private static string SetOf(IReadOnlyList<string> addresses) => "{ " + string.Join(", ", addresses) + " }";

    /// <summary>
    /// The guard on the host end of the run's veth, both ways. INPUT: what the namespace sends the worker ITSELF — its
    /// gateway, its other addresses, the veth's IPv6 link-local — is dropped, except DNS and the rest of a flow the
    /// namespace opened. DNS is what the forward filter admits (<see cref="DnsAccepts"/>), here for a resolver the worker
    /// serves on one of its own addresses or redirects to itself; a loopback resolver in the worker's <c>resolv.conf</c>
    /// is the namespace's own loopback, which no veth rule reaches, and is never on the list (<see cref="NamespaceResolvers"/>).
    /// OUTPUT: the worker may send into the namespace only the replies to those flows and the errors about the run's own
    /// traffic — ICMP fragmentation-needed among them, without which an upload across a narrower uplink stalls — and
    /// anything it starts is rejected. A peer the worker reaches at an address the run's /30 shadows is then refused at
    /// once instead of handed to the sandbox.
    ///
    /// <para>Each accept is bound to its conntrack direction, because ESTABLISHED alone says nothing about who opened
    /// the flow. Wherever another run already has conntrack running on the worker, a connection the worker opened to
    /// that peer before this /30 existed is ESTABLISHED, and its next packet takes this veth: admitted either way, it
    /// would hand the worker's bytes to the sandbox and the sandbox's answers to the worker as the peer's.</para>
    ///
    /// <para>Keyed on the veth, so another run's namespace is untouched; <c>inet</c>, so IPv6 is covered too. The
    /// table is declared, deleted and redefined in the same <c>nft -f</c> transaction as the forward table, which the
    /// kernel applies atomically: a table of this name that an earlier teardown failed to delete would otherwise keep
    /// its own rules, its drop included, ahead of these.</para>
    /// </summary>
    internal static string BuildVethGuardRuleset(string table, string vethHost, string subnet, IReadOnlyList<string> resolvers) => string.Join("\n", new[]
    {
        $"table inet {table} {{}}",
        $"delete table inet {table}",
        $"table inet {table} {{",
        "  chain input {",
        "    type filter hook input priority 0;",
        $"    iifname \"{vethHost}\" ct direction original ct state established,related accept",
    }.Concat(DnsAccepts($"    iifname \"{vethHost}\" ip saddr {subnet}", resolvers)).Concat(new[]
    {
        $"    iifname \"{vethHost}\" drop",
        "  }",
        "  chain output {",
        "    type filter hook output priority 0;",
        $"    oifname \"{vethHost}\" ct direction reply ct state established,related accept",
        $"    oifname \"{vethHost}\" reject",
        "  }",
        "}",
    })) + "\n";

    private static string Slug(string runId)
    {
        var clean = new string((runId ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return clean.Length >= 8 ? clean[..8] : clean.PadRight(8, '0');
    }
}
