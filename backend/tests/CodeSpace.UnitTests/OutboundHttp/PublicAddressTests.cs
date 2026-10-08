using System.Net;
using CodeSpace.Core.Services.OutboundHttp;
using Shouldly;

namespace CodeSpace.UnitTests.OutboundHttp;

/// <summary>
/// Pins which resolved addresses a guarded client may dial. Every refused row is a way to reach the worker's own
/// loopback, its cloud metadata service, or its LAN; the IPv6 rows include each form that EMBEDS an IPv4 address,
/// because a dual-stack socket connects <c>::ffff:127.0.0.1</c> straight to 127.0.0.1.
/// </summary>
[Trait("Category", "Unit")]
public class PublicAddressTests
{
    [Theory]
    [InlineData("127.0.0.1")]                 // loopback
    [InlineData("127.255.255.254")]           // the whole of 127/8
    [InlineData("0.0.0.0")]                   // "this host" — connects to loopback on Linux and macOS
    [InlineData("10.1.2.3")]                  // RFC 1918
    [InlineData("172.16.0.1")]                // RFC 1918
    [InlineData("192.168.1.1")]               // RFC 1918
    [InlineData("169.254.169.254")]           // link-local: AWS/GCP/Azure metadata
    [InlineData("100.100.100.200")]           // CGNAT range: Aliyun metadata
    [InlineData("100.64.0.1")]                // CGNAT
    [InlineData("198.18.0.1")]                // benchmarking, holds the sandbox's per-run /30s
    [InlineData("224.0.0.1")]                 // multicast
    [InlineData("255.255.255.255")]           // broadcast
    [InlineData("::")]                        // unspecified
    [InlineData("::1")]                       // IPv6 loopback
    [InlineData("::ffff:127.0.0.1")]          // v4-mapped loopback
    [InlineData("::ffff:169.254.169.254")]    // v4-mapped metadata
    [InlineData("::ffff:10.0.0.1")]           // v4-mapped RFC 1918
    [InlineData("::127.0.0.1")]               // v4-compatible loopback
    [InlineData("64:ff9b::7f00:1")]           // NAT64 of 127.0.0.1
    [InlineData("64:ff9b::a9fe:a9fe")]        // NAT64 of 169.254.169.254
    [InlineData("2002:7f00:1::1")]            // 6to4 of 127.0.0.1
    [InlineData("2002:a9fe:a9fe::1")]         // 6to4 of 169.254.169.254
    [InlineData("fe80::1")]                   // link-local
    [InlineData("fec0::1")]                   // deprecated site-local
    [InlineData("fc00::1")]                   // unique local
    [InlineData("fd00:ec2::254")]             // unique local: AWS metadata over IPv6
    [InlineData("ff02::1")]                   // multicast
    [InlineData("2001:db8::1")]               // documentation
    [InlineData("2001::1")]                   // Teredo, which carries an obfuscated IPv4
    public void A_non_public_address_is_refused(string address)
    {
        PublicAddress.IsPublic(IPAddress.Parse(address)).ShouldBeFalse($"{address} reaches the worker's own loopback, metadata or network");
    }

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("::ffff:8.8.8.8")]            // v4-mapped public stays public
    [InlineData("64:ff9b::808:808")]          // NAT64 of a public address: how an IPv6-only cluster reaches IPv4 hosts
    [InlineData("2002:808:808::1")]           // 6to4 of a public address
    public void A_public_address_is_allowed(string address)
    {
        PublicAddress.IsPublic(IPAddress.Parse(address)).ShouldBeTrue($"{address} is a globally routable destination");
    }
}
