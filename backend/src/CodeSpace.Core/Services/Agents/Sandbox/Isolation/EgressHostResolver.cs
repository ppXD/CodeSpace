using System.Net;
using System.Net.Sockets;

namespace CodeSpace.Core.Services.Agents.Sandbox.Isolation;

/// <summary>
/// Resolves a deny-by-default egress allowlist of host NAMES (and IP literals) to the IPv4 addresses the filtered
/// netns pins (B3.3a). <see cref="FilteredEgressPlan"/> builds an IPv4 <c>table ip</c> over a <c>10.x</c> /30, so only
/// A records apply — an IP literal passes through verbatim, a name is resolved to its A records, and an IPv6 address
/// is DROPPED (the netns has no v6 route, so a v6 destination is severed, not bypassed — fail-closed). Best-effort +
/// de-duped + order-preserving: a name that fails to resolve is skipped. Because the netns still permits DNS, the
/// known v1 limit applies — a destination whose IP rotates off the set resolved at setup (a CDN) is dropped; hostname
/// /SNI-level filtering is a later proxy slice.
///
/// <para><b>SSRF guard (security-critical):</b> a resolved IP in a PRIVATE / LOOPBACK / LINK-LOCAL range is DROPPED.
/// The netns masquerades through the host, so allow-listing a name that resolves to <c>169.254.169.254</c> (cloud
/// metadata), <c>127.0.0.0/8</c>, or an RFC-1918 address would let the sandboxed agent reach the HOST's own
/// metadata / loopback / LAN — an SSRF-style escalation. Only globally-routable IPv4 is ever pinned.</para>
/// </summary>
public static class EgressHostResolver
{
    /// <summary>Resolve <paramref name="hosts"/> (names + IP literals) to the de-duped, globally-routable IPv4 set the netns allowlist pins. Empty in ⇒ empty out; an all-unresolvable / all-non-routable input yields an empty set (the netns then permits DNS only — fail-closed, never widened).</summary>
    public static async Task<IReadOnlyList<string>> ResolveIpv4Async(IReadOnlyList<string> hosts, CancellationToken cancellationToken)
    {
        var ips = new List<string>();

        foreach (var host in hosts)
        {
            if (IPAddress.TryParse(host, out var literal))
            {
                AddIfRoutableIpv4(ips, literal);   // a v6 literal or a private/loopback/link-local one is dropped here

                continue;
            }

            foreach (var addr in await SafeResolveAsync(host, cancellationToken).ConfigureAwait(false))
                AddIfRoutableIpv4(ips, addr);
        }

        return ips.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Add <paramref name="addr"/> to <paramref name="ips"/> only when it is a GLOBALLY-ROUTABLE IPv4 address — IPv6 (no v4 netns route) and every private/loopback/link-local/reserved range (the SSRF vectors) are dropped.</summary>
    private static void AddIfRoutableIpv4(List<string> ips, IPAddress addr)
    {
        if (addr.AddressFamily == AddressFamily.InterNetwork && IsGloballyRoutableIpv4(addr))
            ips.Add(addr.ToString());
    }

    /// <summary>
    /// True only for an IPv4 address the sandbox may safely egress to: NOT loopback (<c>127/8</c>), NOT RFC-1918
    /// (<c>10/8</c>, <c>172.16/12</c>, <c>192.168/16</c>), NOT link-local incl. cloud metadata (<c>169.254/16</c>),
    /// NOT CGNAT (<c>100.64/10</c>), NOT "this network" (<c>0/8</c>), NOT reserved/benchmark (<c>192.0.0/24</c>,
    /// <c>198.18/15</c>), NOT multicast/reserved-class-E (<c>224/3</c>). These would otherwise route out the host's
    /// own interface and reach its metadata / loopback / LAN.
    /// </summary>
    internal static bool IsGloballyRoutableIpv4(IPAddress ip)
    {
        var b = ip.GetAddressBytes();   // 4 bytes for an InterNetwork address

        if (b[0] == 0) return false;                            // 0.0.0.0/8 "this network"
        if (b[0] == 10) return false;                           // RFC1918
        if (b[0] == 127) return false;                          // loopback
        if (b[0] == 100 && b[1] is >= 64 and <= 127) return false;   // 100.64/10 CGNAT
        if (b[0] == 169 && b[1] == 254) return false;           // link-local incl. 169.254.169.254 metadata
        if (b[0] == 172 && b[1] is >= 16 and <= 31) return false;    // RFC1918
        if (b[0] == 192 && b[1] == 0 && b[2] == 0) return false;     // 192.0.0/24 IETF protocol assignments
        if (b[0] == 192 && b[1] == 168) return false;           // RFC1918
        if (b[0] == 198 && b[1] is 18 or 19) return false;      // 198.18/15 benchmarking
        if (b[0] >= 224) return false;                          // 224/3 multicast + reserved class E (incl. 255.255.255.255)

        return true;
    }

    /// <summary>DNS A/AAAA lookup that never throws on a resolution failure (NXDOMAIN, no network, timeout) — it yields an empty set so one bad host can't abort the whole setup, and the caller fails closed on the resulting (possibly empty) IP set. A genuine cancellation IS rethrown (not swallowed as "unresolvable") so an aborted setup tears down promptly.</summary>
    private static async Task<IPAddress[]> SafeResolveAsync(string host, CancellationToken cancellationToken)
    {
        try { return await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Array.Empty<IPAddress>(); }
    }
}
