using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the egress allowlist host→IPv4 resolution (B3.3a): IP literals pass through, names resolve to their A records,
/// IPv6 is dropped (the netns is IPv4-only — severed, never bypassed), the set is de-duped, and an unresolvable host is
/// best-effort skipped (never throws, so one bad host can't abort the whole netns setup).
/// </summary>
[Trait("Category", "Unit")]
public class EgressHostResolverTests
{
    [Fact]
    public async Task An_ipv4_literal_passes_through_verbatim()
    {
        var ips = await EgressHostResolver.ResolveIpv4Async(new[] { "1.1.1.1", "8.8.8.8" }, CancellationToken.None);

        ips.ShouldBe(new[] { "1.1.1.1", "8.8.8.8" });
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
    public async Task A_name_resolves_to_its_a_records()
    {
        // localhost resolves to 127.0.0.1 on every platform — a deterministic name→A-record case that needs no network.
        var ips = await EgressHostResolver.ResolveIpv4Async(new[] { "localhost" }, CancellationToken.None);

        ips.ShouldContain("127.0.0.1");
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
