using System.Net;
using CodeSpace.Core.Services.OutboundHttp;
using CodeSpace.Core.Settings.OutboundHttp;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace CodeSpace.UnitTests.OutboundHttp;

/// <summary>
/// The operator's committed allowlist is the only way an internal destination becomes reachable, so its parsing is
/// strict: an entry that is neither a host name, an address nor a CIDR fails the setting rather than being skipped,
/// because a skipped entry is a destination the operator believes is admitted and is not.
/// </summary>
[Trait("Category", "Unit")]
public class OutboundDestinationAllowlistTests
{
    [Fact]
    public void ConfigurationKey_ConstantNamePinned()
    {
        // Renaming this key silently empties every deployment's allowlist that pinned the old name, and their
        // intentional internal targets start failing. Hard-pin it so a rename is a visible decision.
        OutboundHttpAllowlistSetting.ConfigurationKey.ShouldBe("OutboundHttp:AllowedInternalDestinations");
    }

    [Theory]
    [InlineData("internal-api.corp", "internal-api.corp", "10.1.2.3")]      // host entry admits whatever the name resolves to
    [InlineData("Internal-API.corp", "internal-api.corp", "10.1.2.3")]      // host names compare case-insensitively
    [InlineData("10.0.0.0/8", "anything.corp", "10.200.0.7")]               // CIDR entry admits an address inside it
    [InlineData("10.1.2.3", "10.1.2.3", "10.1.2.3")]                        // a bare address is a single-address network
    [InlineData("fd00::/8", "v6.corp", "fd12::5")]                          // IPv6 CIDR
    [InlineData("::1", "[::1]", "::1")]                                     // bare IPv6 address
    [InlineData("10.0.0.0/8", "[::ffff:10.1.2.3]", "::ffff:10.1.2.3")]      // a v4-mapped address matches its IPv4 network
    public void An_allowlisted_destination_is_admitted(string entry, string host, string address)
    {
        var allowlist = OutboundDestinationAllowlist.Parse(new[] { entry });

        allowlist.Admits(host, IPAddress.Parse(address)).ShouldBeTrue($"'{entry}' must admit {host} → {address}");
    }

    [Theory]
    [InlineData("internal-api.corp", "other.corp", "10.1.2.3")]             // a different host name
    [InlineData("internal-api.corp", "api.internal-api.corp", "10.1.2.3")]  // no implicit subdomains
    [InlineData("10.0.0.0/8", "anything.corp", "11.0.0.1")]                 // outside the CIDR
    [InlineData("10.1.2.3", "10.1.2.4", "10.1.2.4")]                        // a neighbour of a single address
    [InlineData("127.0.0.1", "[::1]", "::1")]                               // IPv4 loopback does not admit IPv6 loopback
    public void A_destination_outside_the_allowlist_is_not_admitted(string entry, string host, string address)
    {
        var allowlist = OutboundDestinationAllowlist.Parse(new[] { entry });

        allowlist.Admits(host, IPAddress.Parse(address)).ShouldBeFalse($"'{entry}' must not admit {host} → {address}");
    }

    [Fact]
    public void The_empty_allowlist_admits_nothing()
    {
        OutboundDestinationAllowlist.Empty.Admits("localhost", IPAddress.Loopback).ShouldBeFalse();
    }

    [Theory]
    [InlineData("not a host")]
    [InlineData("http://internal.corp")]
    [InlineData("10.0.0.0/99")]
    [InlineData("internal.corp:8080")]
    public void A_malformed_entry_fails_the_setting(string entry)
    {
        var ex = Should.Throw<InvalidOperationException>(() => OutboundDestinationAllowlist.Parse(new[] { entry }));

        ex.Message.ShouldContain(entry);
        ex.Message.ShouldContain(OutboundHttpAllowlistSetting.ConfigurationKey);
    }

    [Fact]
    public void The_setting_reads_the_committed_array_once()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{OutboundHttpAllowlistSetting.ConfigurationKey}:0"] = " internal-api.corp ",
                [$"{OutboundHttpAllowlistSetting.ConfigurationKey}:1"] = "",
                [$"{OutboundHttpAllowlistSetting.ConfigurationKey}:2"] = "192.168.10.0/24",
            })
            .Build();

        var allowlist = new OutboundHttpAllowlistSetting(configuration).Value;

        allowlist.Admits("internal-api.corp", IPAddress.Parse("10.9.9.9")).ShouldBeTrue("a trimmed host entry is admitted");
        allowlist.Admits("printer.lan", IPAddress.Parse("192.168.10.40")).ShouldBeTrue("the CIDR entry is admitted");
        allowlist.Admits("localhost", IPAddress.Loopback).ShouldBeFalse("a blank entry admits nothing");
    }

    [Fact]
    public void An_absent_setting_is_the_empty_allowlist()
    {
        var allowlist = new OutboundHttpAllowlistSetting(new ConfigurationBuilder().Build()).Value;

        allowlist.Admits("localhost", IPAddress.Loopback).ShouldBeFalse();
    }
}
