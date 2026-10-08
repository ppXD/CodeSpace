using System.Net;
using CodeSpace.Core.Services.OutboundHttp;
using CodeSpace.Core.Settings.OutboundHttp;
using CodeSpace.Messages.Failures;
using Shouldly;

namespace CodeSpace.UnitTests.OutboundHttp;

/// <summary>
/// The guard's decision over a host's resolved addresses, with the resolution stubbed. That the guard then dials
/// exactly the addresses <see cref="OutboundDestinationGuard.Permit"/> returns — so what it keeps here is what a
/// connection can reach — is pinned at socket level by
/// <see cref="GuardedHttpClientTests.A_mixed_answer_is_dialled_only_at_the_addresses_the_guard_permits"/>.
/// </summary>
[Trait("Category", "Unit")]
public class OutboundDestinationGuardTests
{
    [Fact]
    public void A_name_resolving_to_public_addresses_is_dialled_at_those_addresses()
    {
        var resolved = Addresses("93.184.216.34", "2606:2800:220:1:248:1893:25c8:1946");

        var permitted = Guard(OutboundDestinationAllowlist.Empty).Permit("api.example.test", resolved);

        permitted.ShouldBe(resolved);
    }

    [Fact]
    public void A_mixed_answer_is_dialled_only_at_its_public_addresses()
    {
        // The DNS-rebinding shape: one name answering with both a public and an internal address. Only the public one
        // may be dialled — the connection cannot land on the internal one however the answers are ordered.
        var permitted = Guard(OutboundDestinationAllowlist.Empty).Permit("rebind.example.test", Addresses("127.0.0.1", "93.184.216.34", "169.254.169.254"));

        permitted.ShouldBe(Addresses("93.184.216.34"));
    }

    [Theory]
    [InlineData("metadata.example.test", "169.254.169.254")]
    [InlineData("localhost", "::1", "127.0.0.1")]
    [InlineData("lan.example.test", "10.0.0.5", "fd00::5")]
    [InlineData("nothing.example.test")]
    public void A_name_resolving_only_to_internal_addresses_is_refused(string host, params string[] resolved)
    {
        var ex = Should.Throw<OutboundDestinationRefusedException>(() => Guard(OutboundDestinationAllowlist.Empty).Permit(host, Addresses(resolved)));

        ex.Message.ShouldContain(host);
        ex.Message.ShouldContain(OutboundHttpAllowlistSetting.ConfigurationKey, customMessage: "the refusal must name the setting that admits an intentional internal target");
        ((IFailure)ex).Kind.ShouldBe(FailureKind.Unprocessable);
        ((IFailure)ex).Code.ShouldBe(FailureCodes.OutboundDestinationRefused);
    }

    [Fact]
    public void The_refusal_does_not_disclose_the_internal_addresses_a_name_resolved_to()
    {
        // The message reaches the run viewer; echoing 10.20.30.40 back would turn http.request into a resolver for the
        // worker's internal DNS.
        var ex = Should.Throw<OutboundDestinationRefusedException>(() => Guard(OutboundDestinationAllowlist.Empty).Permit("db.internal.test", Addresses("10.20.30.40")));

        ex.Message.ShouldNotContain("10.20.30.40");
    }

    [Theory]
    [InlineData("internal-api.corp", "internal-api.corp", "10.1.2.3")]
    [InlineData("127.0.0.0/8", "localhost", "127.0.0.1")]
    [InlineData("fd00::/8", "v6.corp", "fd00::7")]
    public void An_allowlisted_internal_destination_is_dialled(string entry, string host, string address)
    {
        var permitted = Guard(OutboundDestinationAllowlist.Parse(new[] { entry })).Permit(host, Addresses(address));

        permitted.ShouldBe(Addresses(address));
    }

    private static OutboundDestinationGuard Guard(OutboundDestinationAllowlist allowlist) => new(allowlist, new WebProxy());

    private static IPAddress[] Addresses(params string[] addresses) => addresses.Select(IPAddress.Parse).ToArray();
}
