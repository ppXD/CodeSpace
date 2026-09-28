using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace CodeSpace.Core.Services.Agents.Sandbox.Isolation;

/// <summary>
/// The IPv4 prefixes this host already routes — every entry of every routing table (<c>ip -j -4 route show table
/// all</c>) but the default route — read so a per-run /30 is never one the host already uses. A /30 is put on a HOST
/// veth, which makes its gateway a local address and its prefix a connected route more specific than anything it
/// overlaps: a /30 inside the worker's own LAN or pod network would shadow real peers, and one still held by a run
/// that outlived its worker (its namespace and veth survive a restart; the reservation lock does not) would split the
/// replies of two runs between two veths. The kernel's own table is the only record that sees both.
/// </summary>
public sealed class HostRoutedPrefixes
{
    private readonly IReadOnlyList<(uint Network, uint Mask, string Destination)> _prefixes;

    private HostRoutedPrefixes(IReadOnlyList<(uint Network, uint Mask, string Destination)> prefixes) => _prefixes = prefixes;

    /// <summary>The argv that lists them, as JSON.</summary>
    public static IReadOnlyList<string> ListArgv { get; } = new[] { "ip", "-j", "-4", "route", "show", "table", "all" };

    /// <summary>Parse <see cref="ListArgv"/>'s output. A destination with no prefix length is a single address (/32). The default route overlaps everything and is not a use of any one /30, so it is left out — and so is anything broader than a /8, which is a default in all but name (a VPN's <c>0.0.0.0/1</c> + <c>128.0.0.0/1</c> pair), not a network with peers in it.</summary>
    public static HostRoutedPrefixes Parse(string ipJson)
    {
        using var document = JsonDocument.Parse(ipJson);
        var prefixes = new List<(uint, uint, string)>();

        foreach (var route in document.RootElement.EnumerateArray())
            if (route.TryGetProperty("dst", out var dst) && dst.GetString() is { } destination && destination != "default" && TryParsePrefix(destination, out var prefix) && prefix.Mask >= NarrowestDefaultLike && !(IsNullRoute(route) && prefix.Mask < SlashThirty))
                prefixes.Add((prefix.Network, prefix.Mask, destination));

        return new HostRoutedPrefixes(prefixes);
    }

    /// <summary>Whether <paramref name="cidr"/> shares any address with a prefix this host routes.</summary>
    public bool Overlaps(string cidr) => WidestOverlap(cidr) is not null;

    /// <summary>The widest routed prefix <paramref name="cidr"/> overlaps — as the host lists it, and its last address — or null when it overlaps none: so a walk can move past the whole routed range at once instead of one /30 at a time, and name what it moved past. Routed prefixes that overlap one /30 either contain it, and then nest, so the widest reaches furthest; or lie inside it, and then any of them ends within it.</summary>
    public (string Prefix, uint End)? WidestOverlap(string cidr)
    {
        if (!TryParsePrefix(cidr, out var candidate)) throw new ArgumentException($"'{cidr}' is not an IPv4 prefix.", nameof(cidr));

        (uint Network, uint Mask, string Destination)? widest = null;

        foreach (var routed in _prefixes)
            if ((routed.Network & (routed.Mask & candidate.Mask)) == (candidate.Network & (routed.Mask & candidate.Mask)) && (widest is null || routed.Mask < widest.Value.Mask))
                widest = routed;

        return widest is { } found ? (found.Destination, found.Network | ~found.Mask) : null;
    }

    /// <summary>
    /// A route that discards what it matches (<c>blackhole</c>, <c>unreachable</c>, <c>prohibit</c>, <c>throw</c>). One
    /// BROADER than a /30 has no peers to shadow and, in the table that holds a run's connected /30, loses longest-prefix
    /// match to it, so a hardened host's bogon null route (RFC 1918, or the 198.18.0.0/15 the /30s come from) must not
    /// refuse every launch. One as narrow as a /30 or narrower — a banned /32 — WINS that match and would discard the
    /// run's replies, so it still occupies what it covers. A table a policy rule consults BEFORE that one is decided by
    /// rule order, not prefix length, which this list cannot see; the setup asks the kernel instead
    /// (<see cref="FilteredEgressPlan.RouteCheckArgv"/>).
    /// </summary>
    private static bool IsNullRoute(JsonElement route) =>
        route.TryGetProperty("type", out var type) && type.GetString() is "blackhole" or "unreachable" or "prohibit" or "throw";

    /// <summary>The mask of a /30 — the prefix a run's veth route has.</summary>
    private const uint SlashThirty = 0xFFFFFFFC;

    /// <summary>The mask of a /8: a route broader than this carries every address as a default route would.</summary>
    private const uint NarrowestDefaultLike = 0xFF000000;

    private static bool TryParsePrefix(string text, out (uint Network, uint Mask) prefix)
    {
        prefix = default;
        var slash = text.IndexOf('/');
        var length = 32;

        if (slash >= 0 && !int.TryParse(text[(slash + 1)..], out length)) return false;
        if (length is < 0 or > 32 || !IPAddress.TryParse(slash >= 0 ? text[..slash] : text, out var address) || address.AddressFamily != AddressFamily.InterNetwork) return false;

        var bytes = address.GetAddressBytes();
        var mask = length == 0 ? 0u : uint.MaxValue << (32 - length);

        prefix = ((uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]) & mask, mask);
        return true;
    }
}
