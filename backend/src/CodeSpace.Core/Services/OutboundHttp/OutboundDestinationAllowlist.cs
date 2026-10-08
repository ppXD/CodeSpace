using System.Net;
using CodeSpace.Core.Settings.OutboundHttp;

namespace CodeSpace.Core.Services.OutboundHttp;

/// <summary>
/// The internal destinations an operator has deliberately opened to workflow-driven outbound HTTP — a host name, an
/// address, or a CIDR per entry, committed in <c>OutboundHttp:AllowedInternalDestinations</c>. A host entry admits
/// whatever that exact name resolves to (no implicit subdomains), and only that name's virtual host — the request is
/// addressed to its URL's authority, never an author-written <c>Host</c>; an address or CIDR entry admits the addresses
/// inside it, whatever name reached them. Empty by default: nothing internal is reachable until an operator commits it.
///
/// <para>Parsing is strict. An entry that is none of the three fails the setting — and so the host's boot — instead of
/// being skipped, because a skipped entry is a destination the operator believes is open and is not, found only when a
/// workflow fails.</para>
/// </summary>
public sealed class OutboundDestinationAllowlist
{
    public static readonly OutboundDestinationAllowlist Empty = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), Array.Empty<IPNetwork>());

    private readonly IReadOnlySet<string> _hosts;
    private readonly IReadOnlyList<IPNetwork> _networks;

    private OutboundDestinationAllowlist(IReadOnlySet<string> hosts, IReadOnlyList<IPNetwork> networks)
    {
        _hosts = hosts;
        _networks = networks;
    }

    public static OutboundDestinationAllowlist Parse(IEnumerable<string> entries)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var networks = new List<IPNetwork>();

        foreach (var entry in entries.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()))
        {
            if (TryParseNetwork(entry) is { } network)
            {
                networks.Add(network);
                continue;
            }

            hosts.Add(ParseHostName(entry));
        }

        return new OutboundDestinationAllowlist(hosts, networks);
    }

    /// <summary>Whether <paramref name="address"/>, reached through <paramref name="host"/>, is an operator-admitted destination.</summary>
    public bool Admits(string host, IPAddress address)
    {
        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        return _hosts.Contains(host) || _networks.Any(network => network.Contains(normalized));
    }

    private static IPNetwork? TryParseNetwork(string entry)
    {
        if (IPAddress.TryParse(entry, out var address)) return new IPNetwork(address, address.GetAddressBytes().Length * 8);

        if (!entry.Contains('/')) return null;

        if (IPNetwork.TryParse(entry, out var network)) return network;

        throw Malformed(entry);
    }

    private static string ParseHostName(string entry)
    {
        if (Uri.CheckHostName(entry) != UriHostNameType.Dns)
            throw Malformed(entry);

        return entry;
    }

    private static InvalidOperationException Malformed(string entry) =>
        new($"'{entry}' in {OutboundHttpAllowlistSetting.ConfigurationKey} is not a host name, an IP address or a CIDR range (for example internal-api.corp, 10.1.2.3 or 10.0.0.0/8). Fix or remove the entry; it is refused rather than ignored so a destination you believe is open is not silently closed.");
}
