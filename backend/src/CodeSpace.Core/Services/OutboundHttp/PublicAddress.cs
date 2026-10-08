using System.Net;
using System.Net.Sockets;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;

namespace CodeSpace.Core.Services.OutboundHttp;

/// <summary>
/// Whether an address is a GLOBALLY ROUTABLE destination — the only kind workflow-driven outbound HTTP may dial
/// without an operator saying otherwise. Everything else reaches the worker itself: its loopback services, its cloud
/// metadata endpoint (169.254.169.254, fd00:ec2::254, Aliyun's 100.100.100.200), or the network it sits on.
///
/// <para>IPv4 reuses the sandbox egress predicate (<see cref="EgressHostResolver.IsGloballyRoutableIpv4"/>), so the
/// two outbound surfaces cannot drift apart. IPv6 is global unicast (<c>2000::/3</c>) minus documentation and Teredo;
/// that excludes <c>::</c>, <c>::1</c>, link-local, site-local, unique-local and multicast in one test. An IPv6 address
/// that EMBEDS an IPv4 one — v4-mapped, v4-compatible, NAT64 <c>64:ff9b::/96</c>, 6to4 <c>2002::/16</c> — is judged by
/// the IPv4 address inside it: a dual-stack socket dials <c>::ffff:127.0.0.1</c> straight to 127.0.0.1, and an
/// IPv6-only cluster reaches public IPv4 hosts through NAT64, which must keep working.</para>
/// </summary>
public static class PublicAddress
{
    private static readonly byte[] MappedPrefix = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff };
    private static readonly byte[] CompatiblePrefix = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    private static readonly byte[] Nat64Prefix = { 0, 0x64, 0xff, 0x9b, 0, 0, 0, 0, 0, 0, 0, 0 };
    private static readonly byte[] SixToFourPrefix = { 0x20, 0x02 };
    private static readonly byte[] DocumentationPrefix = { 0x20, 0x01, 0x0d, 0xb8 };
    private static readonly byte[] TeredoPrefix = { 0x20, 0x01, 0, 0 };

    public static bool IsPublic(IPAddress address) => address.AddressFamily switch
    {
        AddressFamily.InterNetwork => EgressHostResolver.IsGloballyRoutableIpv4(address),
        AddressFamily.InterNetworkV6 => IsPublicIpv6(address.GetAddressBytes()),
        _ => false,
    };

    private static bool IsPublicIpv6(byte[] bytes)
    {
        if (EmbeddedIpv4(bytes) is { } embedded) return EgressHostResolver.IsGloballyRoutableIpv4(embedded);

        return IsGlobalUnicast(bytes) && !StartsWith(bytes, DocumentationPrefix) && !StartsWith(bytes, TeredoPrefix);
    }

    /// <summary>The IPv4 address an IPv6 address carries, when it is one of the forms that route to that IPv4 address; null otherwise. <c>::</c> and <c>::1</c> fall in the v4-compatible block and come out as 0.0.0.0 / 0.0.0.1, which the IPv4 check refuses.</summary>
    private static IPAddress? EmbeddedIpv4(byte[] bytes)
    {
        if (StartsWith(bytes, MappedPrefix) || StartsWith(bytes, CompatiblePrefix) || StartsWith(bytes, Nat64Prefix)) return new IPAddress(bytes[12..16]);

        if (StartsWith(bytes, SixToFourPrefix)) return new IPAddress(bytes[2..6]);

        return null;
    }

    private static bool IsGlobalUnicast(byte[] bytes) => (bytes[0] & 0xe0) == 0x20;

    private static bool StartsWith(byte[] bytes, byte[] prefix) => bytes.AsSpan().StartsWith(prefix);
}
