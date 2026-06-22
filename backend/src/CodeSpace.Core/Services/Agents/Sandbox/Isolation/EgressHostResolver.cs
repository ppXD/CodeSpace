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
/// </summary>
public static class EgressHostResolver
{
    /// <summary>Resolve <paramref name="hosts"/> (names + IP literals) to the de-duped IPv4 set the netns allowlist pins. Empty in ⇒ empty out; an all-unresolvable input yields an empty set (the netns then permits DNS only — fail-closed, never widened).</summary>
    public static async Task<IReadOnlyList<string>> ResolveIpv4Async(IReadOnlyList<string> hosts, CancellationToken cancellationToken)
    {
        var ips = new List<string>();

        foreach (var host in hosts)
        {
            if (IPAddress.TryParse(host, out var literal))
            {
                if (literal.AddressFamily == AddressFamily.InterNetwork) ips.Add(literal.ToString());

                continue;   // a v6 literal is dropped — the netns is IPv4-only, so it's severed not bypassed
            }

            foreach (var addr in await SafeResolveAsync(host, cancellationToken).ConfigureAwait(false))
                if (addr.AddressFamily == AddressFamily.InterNetwork) ips.Add(addr.ToString());
        }

        return ips.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>DNS A/AAAA lookup that never throws — a resolution failure (NXDOMAIN, no network, timeout) yields an empty set so one bad host can't abort the whole setup; the caller fails closed on the resulting (possibly empty) IP set.</summary>
    private static async Task<IPAddress[]> SafeResolveAsync(string host, CancellationToken cancellationToken)
    {
        try { return await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false); }
        catch { return Array.Empty<IPAddress>(); }
    }
}
