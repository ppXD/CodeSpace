using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the egress allowlist host→IPv4 resolution (B3.3a): globally-routable IP literals pass through, names resolve
/// to their A records, IPv6 is dropped (the netns is IPv4-only — severed, never bypassed), private/loopback/link-local
/// ranges are dropped (the SSRF guard — a name resolving to cloud-metadata/loopback/LAN must never become reachable
/// through the host's masquerade), the set is de-duped, and an unresolvable host is best-effort skipped (never throws).
/// </summary>
[Trait("Category", "Unit")]
public class EgressHostResolverTests
{
    [Fact]
    public async Task A_globally_routable_ipv4_literal_passes_through_verbatim()
    {
        var ips = await EgressHostResolver.ResolveIpv4Async(new[] { "1.1.1.1", "8.8.8.8" }, CancellationToken.None);

        ips.ShouldBe(new[] { "1.1.1.1", "8.8.8.8" });
    }

    [Theory]
    [InlineData("127.0.0.1")]          // loopback
    [InlineData("10.1.2.3")]           // RFC1918
    [InlineData("172.16.5.5")]         // RFC1918
    [InlineData("192.168.0.1")]        // RFC1918
    [InlineData("169.254.169.254")]    // link-local cloud-metadata — the headline SSRF vector
    [InlineData("100.64.0.1")]         // CGNAT
    [InlineData("0.0.0.0")]            // "this network"
    [InlineData("224.0.0.1")]          // multicast
    public async Task A_private_or_loopback_or_link_local_ip_is_dropped_as_an_ssrf_vector(string ip)
    {
        // The netns masquerades through the host, so a name resolving to any of these would let the agent reach the
        // host's own metadata / loopback / LAN. They must never be pinned into the allow set.
        var ips = await EgressHostResolver.ResolveIpv4Async(new[] { ip, "8.8.8.8" }, CancellationToken.None);

        ips.ShouldBe(new[] { "8.8.8.8" }, $"{ip} is a non-routable SSRF vector and must be dropped, leaving only the routable host");
    }

    [Fact]
    public async Task An_ipv6_literal_is_dropped_because_the_netns_is_ipv4_only()
    {
        var ips = await EgressHostResolver.ResolveIpv4Async(new[] { "::1", "2606:4700:4700::1111", "1.1.1.1" }, CancellationToken.None);

        ips.ShouldBe(new[] { "1.1.1.1" }, "a v6 destination has no route in the v4 netns — dropped, not bypassed");
    }

    [Fact]
    public async Task Duplicate_resolved_ips_are_de_duped()
    {
        var ips = await EgressHostResolver.ResolveIpv4Async(new[] { "1.1.1.1", "1.1.1.1" }, CancellationToken.None);

        ips.ShouldHaveSingleItem().ShouldBe("1.1.1.1");
    }

    [Fact]
    public async Task A_name_resolving_to_loopback_is_dropped()
    {
        // localhost resolves to 127.0.0.1 on every platform (a deterministic name→A-record case that needs no network)
        // — and that loopback address is then dropped by the SSRF guard, so the agent can never egress to the host's
        // own loopback via an allow-listed name. This exercises BOTH the name-resolution path and the range guard.
        var ips = await EgressHostResolver.ResolveIpv4Async(new[] { "localhost" }, CancellationToken.None);

        ips.ShouldNotContain("127.0.0.1");
        ips.ShouldBeEmpty("localhost resolves only to loopback (+ ::1), both of which the guard drops");
    }

    [Fact]
    public async Task An_unresolvable_host_is_skipped_not_thrown()
    {
        // `.invalid` is reserved (RFC 2606) to never resolve. One bad host must not abort the set (fail-closed: it just
        // narrows what's reachable), so the call returns the empty set rather than throwing.
        var ips = await EgressHostResolver.ResolveIpv4Async(new[] { "nope.invalid" }, CancellationToken.None);

        ips.ShouldBeEmpty();
    }

    [Fact]
    public async Task Empty_in_is_empty_out()
    {
        (await EgressHostResolver.ResolveIpv4Async(Array.Empty<string>(), CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_genuine_cancellation_is_rethrown_not_swallowed_as_unresolvable()
    {
        // A cancelled token must abort the setup (the runner tears down), NOT be masked as "this host didn't resolve" —
        // otherwise a cancelled setup would silently narrow the allow set and launch. A name (not an IP literal) forces
        // the DNS path where the token is observed.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => EgressHostResolver.ResolveIpv4Async(new[] { "example.com" }, cts.Token));
    }
}
