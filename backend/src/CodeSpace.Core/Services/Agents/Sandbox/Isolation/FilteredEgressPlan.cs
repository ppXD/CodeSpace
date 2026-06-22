namespace CodeSpace.Core.Services.Agents.Sandbox.Isolation;

/// <summary>
/// The PURE command-sequence builder for a deny-by-default egress allowlist (B3.2 enforcement) — a per-run network
/// namespace whose only egress is NAT'd to the host, with an nftables FORWARD filter that permits the netns subnet
/// to reach ONLY the resolved allowlist IPs (+ DNS), dropping everything else. It produces the <c>ip</c> / <c>nft</c>
/// / <c>sysctl</c> argv sequences for SETUP, the <c>ip netns exec</c> prefix the confined command runs behind, and
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
    public required string NsSubnetCidr { get; init; }

    /// <summary>The argv sequences (each an executable + args) that build the filtered netns, in order — run BEFORE <see cref="NftRuleset"/> is applied.</summary>
    public required IReadOnlyList<IReadOnlyList<string>> SetupCommands { get; init; }

    /// <summary>The nftables ruleset (NAT masquerade + the scoped default-drop forward allowlist) applied via <c>nft -f -</c> on STDIN after <see cref="SetupCommands"/>. Kept off the argv (multi-line) so it pipes cleanly.</summary>
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
    /// <see cref="EgressSubnetAllocator"/> (so two concurrent runs never share a subnet — a host-global nft-chain
    /// hazard); <paramref name="allowedIps"/> are the only reachable destinations (plus DNS).
    /// </summary>
    public static FilteredEgressPlan Build(string runId, IReadOnlyList<string> allowedIps, EgressSubnetAllocator.Lease subnet)
    {
        var ns = NamespaceFor(runId);
        var slug = Slug(runId);
        var vethHost = $"csh-{slug}";
        var vethNs = $"csn-{slug}";

        var hostIp = subnet.HostIp;
        var nsIp = subnet.NsIp;
        var subnetCidr = subnet.Cidr;
        var table = ns;   // one nft table per run, named like the ns

        var nftRuleset = BuildNftRuleset(table, subnetCidr, allowedIps);

        var setup = new List<IReadOnlyList<string>>
        {
            new[] { "ip", "netns", "add", ns },
            new[] { "ip", "link", "add", vethHost, "type", "veth", "peer", "name", vethNs },
            new[] { "ip", "link", "set", vethNs, "netns", ns },
            new[] { "ip", "addr", "add", $"{hostIp}/30", "dev", vethHost },
            new[] { "ip", "link", "set", vethHost, "up" },
            new[] { "ip", "netns", "exec", ns, "ip", "addr", "add", $"{nsIp}/30", "dev", vethNs },
            new[] { "ip", "netns", "exec", ns, "ip", "link", "set", vethNs, "up" },
            new[] { "ip", "netns", "exec", ns, "ip", "link", "set", "lo", "up" },
            new[] { "ip", "netns", "exec", ns, "ip", "route", "add", "default", "via", hostIp },
            new[] { "sysctl", "-w", "net.ipv4.ip_forward=1" },
        };

        return new FilteredEgressPlan
        {
            Namespace = ns,
            VethHost = vethHost,
            VethNs = vethNs,
            HostAddrCidr = $"{hostIp}/30",
            NsAddrCidr = $"{nsIp}/30",
            HostIp = hostIp,
            NsSubnetCidr = subnetCidr,
            SetupCommands = setup,
            NftRuleset = nftRuleset,
            ExecPrefix = new[] { "ip", "netns", "exec", ns },
            TeardownCommands = TeardownCommandsFor(runId),
        };
    }

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
        };
    }

    /// <summary>The nftables ruleset fed to <c>nft -f -</c> on stdin: NAT masquerade for the subnet + a default-drop forward filter scoped to the subnet that permits established + the allowed IPs + DNS.</summary>
    internal static string BuildNftRuleset(string table, string subnet, IReadOnlyList<string> allowedIps)
    {
        var allowSet = allowedIps.Count > 0 ? "{ " + string.Join(", ", allowedIps) + " }" : null;

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
            $"    ip saddr {subnet} udp dport 53 accept",
            $"    ip saddr {subnet} tcp dport 53 accept",
        };

        if (allowSet is not null)
            lines.Add($"    ip saddr {subnet} ip daddr {allowSet} accept");

        lines.Add($"    ip saddr {subnet} drop");   // scoped default-drop: only the netns subnet, never the host's own forwarding
        lines.Add("  }");
        lines.Add("}");

        return string.Join("\n", lines) + "\n";
    }

    private static string Slug(string runId)
    {
        var clean = new string((runId ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return clean.Length >= 8 ? clean[..8] : clean.PadRight(8, '0');
    }
}
